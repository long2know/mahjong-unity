using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5.Bishop;

/// <summary>
/// Phase K Wave 5 — Bishop. JWKS endpoint contract pin.
///
/// <para>The HMAC-signed Wave-5 surface cannot publish a real JWKS
/// document (the shared secret would defeat the purpose of having
/// one), so the endpoint exists ONLY to reserve the route + force
/// cache-bypass: any intermediate proxy/CDN that pins the 404 with
/// a long TTL would prevent the Phase L RS256 flip from rolling out
/// cleanly. The <c>Cache-Control: no-store</c> header + the
/// structured 404 body together pin the contract.</para>
/// </summary>
public sealed class JwksEndpointContractTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-bishop-jwks-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Auth:JwtSigningKeys:0", "phase-k-w5-bishop-jwks-32-bytes!!");
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

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public async Task Jwks_Returns404WithNoStoreNegative()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/api/auth/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        // Phase K Wave 5 contract — re-pinned. A previous Wave-6 tuning
        // attempted to relax the negative to `public, max-age=60` so
        // CDNs could briefly absorb the 404, but the public-facing
        // contract (tests/e2e/jwks-endpoint-shape.spec.ts) hard-pins
        // `Cache-Control: no-store` so a misconfigured CDN cannot
        // 30-day-cache the envelope and block the Phase L RS256 flip.
        // We honour the e2e contract — it's the load-bearing one.
        Assert.True(
            resp.Headers.CacheControl?.NoStore == true,
            "JWKS 404 MUST carry Cache-Control: no-store (Wave-5 contract; e2e jwks-endpoint-shape.spec.ts)."
        );
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public async Task Jwks_BodyCarriesAlgorithmAndNote()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/api/auth/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("algorithm", out var algEl));
        Assert.Equal("HS256", algEl.GetString());
        Assert.True(root.TryGetProperty("note", out _));
        Assert.True(root.TryGetProperty("error", out _));
        // Phase K Wave 6 — Bishop. Negative envelope advertises the
        // migration target so downstream operators can wire a JWKS
        // verifier without a separate doc trip.
        Assert.True(root.TryGetProperty("reason", out var reasonEl));
        Assert.Equal("jwt-algorithm-is-hs256", reasonEl.GetString());
        Assert.True(root.TryGetProperty("migrateTo", out var migrateEl)
            || root.TryGetProperty("migrate_to", out migrateEl));
        Assert.Equal("RS256", migrateEl.GetString());
    }
}
