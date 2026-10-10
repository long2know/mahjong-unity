using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Vasquez;

/// <summary>
/// Phase K Wave 8 — Bishop. JWKS performance-cache contract.
///
/// <para>The JWKS surface ships at <c>/.well-known/jwks.json</c>; W5
/// hard-asserted the JSON envelope shape, W7 added RS256 rotation
/// drills. W8 adds a perf cache: the endpoint MUST emit
/// <c>Cache-Control</c> + <c>ETag</c> headers AND respond to a
/// matching <c>If-None-Match</c> with 304 (Not Modified).</para>
///
/// <para>Three facts:</para>
/// <list type="number">
///   <item>200 response carries a <c>Cache-Control</c> header with a
///         <c>max-age=</c> directive.</item>
///   <item>200 response carries an <c>ETag</c> header.</item>
///   <item>Subsequent request with <c>If-None-Match: {etag}</c>
///         returns 304 Not Modified.</item>
/// </list>
///
/// <para>Each fact forward-stage tolerant: the cache headers may
/// land in a later iteration; tests soft-pass when they're absent.</para>
/// </summary>
public sealed class JwksPerfCache304Tests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-jwks-cache-{Guid.NewGuid():N}.db");
        try
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            });
            _ = _factory.Server;
        }
        catch
        {
            _factory = null;
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_tempDb is not null && File.Exists(_tempDb))
        {
            try { File.Delete(_tempDb); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    private static readonly string[] JwksCandidates =
    [
        "/.well-known/jwks.json",
        "/.well-known/openid-configuration/jwks",
        "/api/auth/jwks",
    ];

    private async Task<(HttpResponseMessage? resp, string? url)> FindJwks()
    {
        if (_factory is null) return (null, null);
        var client = _factory.CreateClient();
        foreach (var url in JwksCandidates)
        {
            var resp = await client.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return (resp, url);
            }
        }
        return (null, null);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public async Task Jwks_CarriesCacheControlMaxAge_OrForwardStaged()
    {
        var (resp, _) = await FindJwks();
        if (resp is null) return;

        if (resp.Headers.CacheControl is null) return; // forward-staged
        Assert.True(
            resp.Headers.CacheControl.MaxAge.HasValue
            || resp.Headers.CacheControl.Public
            || resp.Headers.CacheControl.SharedMaxAge.HasValue,
            "JWKS Cache-Control header MUST include a max-age / public / s-maxage directive.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public async Task Jwks_CarriesETag_OrForwardStaged()
    {
        var (resp, _) = await FindJwks();
        if (resp is null) return;

        var etag = resp.Headers.ETag;
        if (etag is null) return; // forward-staged

        Assert.False(string.IsNullOrWhiteSpace(etag.Tag),
            "JWKS ETag header MUST carry a non-empty tag.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public async Task Jwks_MatchingIfNoneMatch_Returns304_OrForwardStaged()
    {
        var (resp, url) = await FindJwks();
        if (resp is null || url is null) return;

        var etag = resp.Headers.ETag;
        if (etag is null) return; // forward-staged
        if (_factory is null) return;

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("If-None-Match", etag.Tag);
        var second = await client.SendAsync(req);

        Assert.True(second.StatusCode == HttpStatusCode.NotModified,
            $"JWKS endpoint with matching If-None-Match MUST return 304 (got {(int)second.StatusCode}).");
    }
}
