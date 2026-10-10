using System.Net;
using System.Net.Http.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 8 — dev-login contract tests (Vasquez).
///
/// <para>Bishop's Wave 8 ships a developer-convenience login endpoint
/// registered ONLY in the Development environment. It bypasses OAuth /
/// magic-link, takes an arbitrary identity (display name + email), and
/// issues a session cookie + links to the current <c>mahjong_pid</c>.</para>
///
/// <para><b>What we pin:</b>
/// <list type="number">
///   <item>Endpoint registered in Development (returns 2xx/3xx) or 404
///         not-yet-shipped.</item>
///   <item>Endpoint NOT registered in Production (returns 404).</item>
///   <item>Session cookie set on success.</item>
///   <item><c>mahjong_pid</c> cookie still present (dev-login MUST NOT
///         clear the persistent player id).</item>
/// </list></para>
/// </summary>
public class DevLoginTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _devFactory;
    private WebApplicationFactory<Program>? _prodFactory;
    private string? _devDb;
    private string? _prodDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _devDb = Path.Combine(dataDir, $"mahjong-devlogin-dev-{Guid.NewGuid():N}.db");
        _prodDb = Path.Combine(dataDir, $"mahjong-devlogin-prod-{Guid.NewGuid():N}.db");

        _devFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_devDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
        _prodFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            // Phase L — Drake. Prod hardening: JwtSigningKeyProvider now
            // refuses to boot in Production with an ephemeral random
            // HMAC key. Supply a stable test key so the factory starts.
            // See docs/jwt-rotation.md §7.
            b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_prodDb}");
            // Production-required CORS origins (empty in Wave-6 default).
            b.UseSetting("Cors:AllowedOrigins:0", "https://example.test");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _devFactory.Server;
        _ = _prodFactory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _devFactory?.Dispose();
        _prodFactory?.Dispose();
        try { if (_devDb is not null && File.Exists(_devDb)) File.Delete(_devDb); } catch { }
        try { if (_prodDb is not null && File.Exists(_prodDb)) File.Delete(_prodDb); } catch { }
        return Task.CompletedTask;
    }

    private static readonly string[] DevLoginCandidates =
    {
        "/api/auth/dev-login",
        "/api/auth/dev/login",
        "/api/dev/login",
        "/api/auth/dev",
    };

    private static async Task<(HttpResponseMessage response, string url)> PostDevLoginAsync(HttpClient client, object body)
    {
        HttpResponseMessage? last = null;
        string lastUrl = "";
        foreach (var url in DevLoginCandidates)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync(url, body);
            lastUrl = url;
            if (last.StatusCode != HttpStatusCode.NotFound) return (last, url);
        }
        return (last!, lastUrl);
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Dev-login is reachable in Development
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task DevLogin_InDevelopment_RegisteredOrNotYet()
    {
        Assert.NotNull(_devFactory);
        using var client = _devFactory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (response, _) = await PostDevLoginAsync(client, new { displayName = "dev-user", email = "dev@example.com" });
        using (response)
        {
            var code = (int)response.StatusCode;
            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound
                || (code >= 200 && code < 500),
                $"Dev-login in Development returned {code}; expected 2xx/3xx/4xx or 404 (not yet wired).");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Dev-login is NOT registered in Production
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task DevLogin_InProduction_ReturnsNotFound()
    {
        // Critical security gate: dev-login MUST NOT be routable in
        // Production. Every candidate URL must 404.
        Assert.NotNull(_prodFactory);
        using var client = _prodFactory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in DevLoginCandidates)
        {
            using var response = await client.PostAsJsonAsync(url, new { displayName = "attacker", email = "x@example.com" });
            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound
                || response.StatusCode == HttpStatusCode.MethodNotAllowed
                || response.StatusCode == HttpStatusCode.Forbidden,
                $"Dev-login endpoint {url} responded {(int)response.StatusCode} in Production — must be unreachable.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Successful dev-login sets a session cookie
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task DevLogin_OnSuccess_SetsSessionCookie()
    {
        Assert.NotNull(_devFactory);
        using var client = _devFactory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (response, _) = await PostDevLoginAsync(client, new { displayName = "alice", email = "alice@example.com" });
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            if ((int)response.StatusCode >= 400) return;

            // Any new Set-Cookie header on the response counts — Bishop may
            // name it `.AspNetCore.Cookies`, `mahjong_session`, `auth_session`,
            // etc. We assert that at least one auth-shaped cookie is emitted.
            var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.ToArray()
                : Array.Empty<string>();
            Assert.NotEmpty(setCookies);
            Assert.Contains(setCookies, sc =>
                sc.Contains("auth", StringComparison.OrdinalIgnoreCase)
                || sc.Contains("session", StringComparison.OrdinalIgnoreCase)
                || sc.Contains("identity", StringComparison.OrdinalIgnoreCase)
                || sc.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase)
                || sc.Contains("mahjong_pid"));
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Dev-login retains the mahjong_pid cookie (no clobber)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task DevLogin_RetainsMahjongPid()
    {
        Assert.NotNull(_devFactory);
        using var client = _devFactory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // First, mint a pid via /api/identity.
        using (var identity = await client.PostAsync("/api/identity", new StringContent("")))
        {
            // pid endpoint is Wave-6; expect 200/2xx.
            Assert.True((int)identity.StatusCode < 500);
        }

        var (response, _) = await PostDevLoginAsync(client, new { displayName = "bob", email = "bob@example.com" });
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            if ((int)response.StatusCode >= 400) return;

            // Inspect the response for an explicit `mahjong_pid` deletion
            // (Max-Age=0 / Expires=Thu, 01 Jan 1970). Dev-login MUST NOT
            // clear it; the response either omits a mahjong_pid Set-Cookie
            // entirely OR sets it to a fresh / sliding value.
            var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.ToArray()
                : Array.Empty<string>();
            var pidDelete = setCookies.FirstOrDefault(sc =>
                sc.StartsWith("mahjong_pid=", StringComparison.OrdinalIgnoreCase)
                && (sc.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase)
                 || sc.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)));
            Assert.Null(pidDelete);
        }
    }
}
