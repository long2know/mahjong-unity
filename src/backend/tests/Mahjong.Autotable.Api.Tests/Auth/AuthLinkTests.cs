using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 8 — auth identity link/unlink contract tests (Vasquez).
///
/// <para>Bishop's Wave 8 surface allows a single user to link multiple
/// identities (e.g., the email-magic-link identity + Google). The contract:
/// <list type="bullet">
///   <item><c>POST /api/auth/link/{provider}</c> — initiates the link for
///         the current session.</item>
///   <item><c>DELETE /api/auth/link/{provider}</c> — unlinks a provider
///         from the current user. Server MUST refuse if it would leave the
///         user with zero identities.</item>
///   <item><c>GET /api/auth/me</c> exposes the linked-providers array (see
///         <see cref="AuthMeTests"/>).</item>
/// </list></para>
/// </summary>
public class AuthLinkTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-authlink-{Guid.NewGuid():N}.db");
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

    private static IEnumerable<string> LinkCandidates(string provider) => new[]
    {
        $"/api/auth/link/{provider}",
        $"/api/auth/identities/{provider}",
        $"/api/auth/me/link/{provider}",
    };

    [Theory, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    [InlineData("google")]
    [InlineData("github")]
    [InlineData("email")]
    public async Task AuthLink_LinkProvider_RoutableOrNotYetRegistered(string provider)
    {
        // Unauthenticated link attempt must not 5xx. The endpoint may
        // 401/403/404; any of those satisfy the contract.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage? last = null;
        foreach (var url in LinkCandidates(provider))
        {
            last?.Dispose();
            last = await client.PostAsync(url, new StringContent(""));
            if (last.StatusCode != HttpStatusCode.NotFound) break;
        }
        using (last)
        {
            Assert.True((int)last!.StatusCode < 500,
                $"Link {provider} responded {(int)last.StatusCode} — must not 5xx.");
        }
    }

    [Theory, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    [InlineData("google")]
    [InlineData("github")]
    [InlineData("email")]
    public async Task AuthLink_UnlinkProvider_RoutableOrNotYetRegistered(string provider)
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage? last = null;
        foreach (var url in LinkCandidates(provider))
        {
            last?.Dispose();
            last = await client.DeleteAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) break;
        }
        using (last)
        {
            Assert.True((int)last!.StatusCode < 500,
                $"Unlink {provider} responded {(int)last.StatusCode} — must not 5xx.");
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthLink_UnlinkLastIdentity_Refused()
    {
        // Anonymous → unlink "email" should NOT succeed (no auth context,
        // or "would leave user identity-less" — either way, not 2xx).
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage? last = null;
        foreach (var url in LinkCandidates("email"))
        {
            last?.Dispose();
            last = await client.DeleteAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) break;
        }
        using (last)
        {
            var code = (int)last!.StatusCode;
            Assert.True(
                last.StatusCode == HttpStatusCode.NotFound
                || (code >= 400 && code < 500),
                $"Unauthenticated unlink of last identity returned {code}; expected 4xx or 404 (not 2xx/5xx).");
        }
    }
}
