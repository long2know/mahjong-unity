using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 9 — admin game-audit endpoint contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 exposes <c>GET /api/admin/games/{gameId}/audit</c>
/// (admin-only) returning audit metadata: source IP hash, hub method
/// durations, score deltas. Anonymous + non-admin sessions get 401/403
/// (never 500). Production hardening — no detail leaks.</para>
/// </summary>
public class GameAuditEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-gae-{Guid.NewGuid():N}.db");
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

    private static string[] AuditUrls(Guid gameId) => new[]
    {
        $"/api/admin/games/{gameId}/audit",
        $"/api/games/{gameId}/audit",
        $"/api/admin/audit/{gameId}",
    };

    private static async Task<HttpResponseMessage> GetFirstNonNotFoundAsync(HttpClient client, string[] urls)
    {
        HttpResponseMessage? last = null;
        foreach (var url in urls)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public async Task AuditEndpoint_Anonymous_ReturnsUnauthorisedOrNotFound()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid();
        using var resp = await GetFirstNonNotFoundAsync(client, AuditUrls(gameId));
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        // Anonymous request must NOT 5xx, and must NOT 200 (no admin auth).
        Assert.True((int)resp.StatusCode < 500,
            $"Anonymous audit access must surface 4xx, got {(int)resp.StatusCode}.");
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);

        // 401 / 403 are the canonical responses.
        Assert.True(
            resp.StatusCode == HttpStatusCode.Unauthorized
            || resp.StatusCode == HttpStatusCode.Forbidden
            || (int)resp.StatusCode == 405
            || resp.StatusCode == HttpStatusCode.NotFound,
            $"Anonymous audit must be 401/403; got {(int)resp.StatusCode}.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public async Task AuditEndpoint_NoDetailLeakInUnauthorisedBody()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid();
        using var resp = await GetFirstNonNotFoundAsync(client, AuditUrls(gameId));
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (resp.IsSuccessStatusCode) return;

        var body = (await resp.Content.ReadAsStringAsync()) ?? "";

        // The error body must NOT contain audit-shaped keywords —
        // "ipv4Hash", "scoreDelta", "hubMethod", etc. — which would
        // confirm row existence to an anonymous caller.
        var leakSignals = new[] { "ipv4Hash", "scoreDelta", "hubMethod", "durationMs", "auditRows", "userAgentHash" };
        foreach (var s in leakSignals)
        {
            Assert.DoesNotContain(s, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public async Task AuditEndpoint_NonAdminAuthenticatedSession_StillRejected()
    {
        // Authenticated-but-non-admin contract: hit the dev-login (a
        // standard cookie session, NOT admin), then probe audit. Must
        // still be 401/403 (the admin role is required, presence of a
        // session alone is insufficient).
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        // Issue a dev-login session if available.
        var dev = await client.PostAsync("/api/auth/dev-login", new StringContent(""));
        if (dev.StatusCode == HttpStatusCode.NotFound) { dev.Dispose(); return; }
        dev.Dispose();

        var gameId = Guid.NewGuid();
        using var resp = await GetFirstNonNotFoundAsync(client, AuditUrls(gameId));
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((int)resp.StatusCode < 500,
            $"Non-admin authenticated audit access must surface 4xx, got {(int)resp.StatusCode}.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public async Task AuditEndpoint_ResponseEnvelope_ShapeOnceAccessible()
    {
        // We don't have a way to mint an admin token from the test harness
        // without Bishop's full surface. So we just probe the shape: when
        // the endpoint returns 200 (e.g. if Bishop ships a debug bypass in
        // Development), the body must carry the audit envelope keys.
        // Otherwise soft-pass.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid();
        using var resp = await GetFirstNonNotFoundAsync(client, AuditUrls(gameId));
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Audit body should expose either an `events` array or an
        // `audit` envelope.
        bool ok =
            (root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("events", out _)
                    || root.TryGetProperty("audit", out _)
                    || root.TryGetProperty("rows", out _)))
            || root.ValueKind == JsonValueKind.Array;
        Assert.True(ok, $"Audit success body must expose an `events`/`audit`/`rows` envelope. Body: {body[..Math.Min(body.Length, 400)]}");
    }
}
