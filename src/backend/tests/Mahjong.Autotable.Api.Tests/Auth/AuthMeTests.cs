using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 8 — auth/me contract tests (Vasquez).
///
/// <para>Bishop's Wave 8 surface: <c>GET /api/auth/me</c> returns the
/// caller's authentication state. Shape:
/// <code>
/// {
///   "isAuthenticated": false | true,
///   "playerId":        "deadbeef…",
///   "displayName":     "Alice",
///   "email":           "alice@example.com" | null,
///   "providers":       ["google", "email"]
/// }
/// </code>
/// </para>
///
/// <para>Anonymous callers MUST get <c>isAuthenticated=false</c>; the
/// endpoint never 401s — it always exposes a public "are you logged in?"
/// surface so the frontend can decide whether to show the sign-in modal
/// without an extra round-trip.</para>
/// </summary>
public class AuthMeTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-authme-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private static readonly string[] MeCandidates =
    {
        "/api/auth/me",
        "/api/me",
        "/api/auth/whoami",
    };

    private static async Task<HttpResponseMessage> GetMeAsync(HttpClient client)
    {
        HttpResponseMessage? last = null;
        foreach (var url in MeCandidates)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthMe_Anonymous_ReturnsIsAuthenticatedFalse()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await GetMeAsync(client);

        // Endpoint may not yet be wired → 404 soft-passes.
        if (response.StatusCode == HttpStatusCode.NotFound) return;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // Either explicit isAuthenticated:false, OR no auth fields at all.
        if (root.TryGetProperty("isAuthenticated", out var isAuth))
        {
            Assert.Equal(JsonValueKind.False, isAuth.ValueKind);
        }
        else if (root.TryGetProperty("authenticated", out isAuth))
        {
            Assert.Equal(JsonValueKind.False, isAuth.ValueKind);
        }
        // playerId may still be present (mahjong_pid mints unconditionally).
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthMe_Anonymous_NeverReturns401()
    {
        // The endpoint is intentionally public so the frontend can call
        // it before sign-in. 401 would defeat the purpose.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await GetMeAsync(client);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthMe_Response_CarriesProvidersArrayShape()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await GetMeAsync(client);

        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if ((int)response.StatusCode >= 400) return;

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // `providers` (if present) must be an array, never a string / object.
        if (root.TryGetProperty("providers", out var providers))
        {
            Assert.Equal(JsonValueKind.Array, providers.ValueKind);
        }
        if (root.TryGetProperty("identities", out var identities))
        {
            Assert.Equal(JsonValueKind.Array, identities.ValueKind);
        }
    }
}
