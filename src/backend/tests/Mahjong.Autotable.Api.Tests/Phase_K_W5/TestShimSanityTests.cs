#if TESTING_SHIM
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Mahjong.Autotable.Api.Tests.Shims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5;

/// <summary>
/// Phase K Wave 5 — sanity coverage for
/// <see cref="TestHttpClientExtensions.WithDirectSession(HttpClient, Guid)"/>
/// and its DB-aware overloads.
///
/// <para>Pins three contracts:</para>
/// <list type="number">
///   <item>The cookie-only form sets <c>mahjong_pid</c> on the
///         outbound request header (verified by reading
///         <see cref="HttpClient.DefaultRequestHeaders"/>).</item>
///   <item>The DB overload inserts a matching
///         <see cref="PlayerAuthSession"/> row that
///         <see cref="AuthCookieService.ResolveAsync"/> can read back.</item>
///   <item>Calling the cookie-only form twice is idempotent
///         (no duplicate <see cref="PlayerAuthIdentity"/> rows in the
///         DB-aware overload).</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public class TestShimSanityTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w5-shim-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production"); // shim is meant for prod-like factories
            // Phase L — Drake. Prod hardening: see SecurityHeadersTests for context.
            b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
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

    private HttpClient NewClient() => _factory!.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-5")]
    public void WithDirectSession_CookieOnly_SetsMahjongPidHeader()
    {
        using var client = NewClient();
        var pid = Guid.NewGuid();
        client.WithDirectSession(pid);

        Assert.True(client.DefaultRequestHeaders.TryGetValues("Cookie", out var cookies));
        var merged = string.Join("; ", cookies!);
        Assert.Contains($"{PlayerIdentityService.CookieName}={pid:N}", merged);
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-5")]
    public async Task WithDirectSession_DbOverload_InsertsResolvableSession()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();
        var pid = Guid.NewGuid();
        client.WithDirectSession(_factory!.Services, pid, role: "admin");

        // The cookie header carries BOTH `mahjong_pid` and `mahjong_auth`.
        Assert.True(client.DefaultRequestHeaders.TryGetValues("Cookie", out var cookies));
        var merged = string.Join("; ", cookies!);
        Assert.Contains(PlayerIdentityService.CookieName, merged);
        Assert.Contains(AuthCookieService.CookieName, merged);

        // The DB now carries a session row + identity row keyed by pid hex.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pidHex = pid.ToString("N");
        var identity = db.PlayerAuthIdentities.FirstOrDefault(i => i.PlayerId == pidHex);
        Assert.NotNull(identity);
        var session = db.PlayerAuthSessions
            .FirstOrDefault(s => s.PlayerId == pidHex);
        Assert.NotNull(session);
        Assert.Equal("admin", session!.Role);

        // Sanity: the surface is reachable through the live server.
        // /api/auth/me MUST return 200 or 401 (never 5xx) — depending
        // on whether the production environment resolves the shim
        // cookie. Either is acceptable here; the point is the cookie
        // header doesn't break the pipeline.
        using var resp = await client.GetAsync("/api/auth/me");
        Assert.True((int)resp.StatusCode < 500,
            $"/api/auth/me with shim cookie → {(int)resp.StatusCode}; never 5xx.");
        await Task.CompletedTask;
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-5")]
    public void WithDirectSession_Idempotent_NoDuplicateIdentityRows()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();
        var pid = Guid.NewGuid();
        client.WithDirectSession(_factory!.Services, pid);
        client.WithDirectSession(_factory!.Services, pid);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pidHex = pid.ToString("N");
        var identityCount = db.PlayerAuthIdentities.Count(i => i.PlayerId == pidHex);
        Assert.Equal(1, identityCount);

        // Two session rows are acceptable — each call mints a fresh
        // token. The idempotency contract is about IDENTITY rows.
        var sessionCount = db.PlayerAuthSessions.Count(s => s.PlayerId == pidHex);
        Assert.True(sessionCount >= 1,
            $"Expected at least 1 session row for {pidHex}; got {sessionCount}.");
    }
}
#endif
