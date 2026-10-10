using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Voice;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Bishop;

/// <summary>
/// Phase K Wave 9 — Bishop. Hard-asserted facts for the legacy
/// livestream-path alias that 301-redirects
/// <c>/api/tables/{id}/livestream/...</c> to the canonical
/// <c>/api/voice/livestream/{id}/...</c> URL.
///
/// <list type="number">
///   <item>GET on the legacy playlist returns 301.</item>
///   <item>Location header points at the canonical voice route.</item>
///   <item>Cache-Control directive surfaces the 24h max-age.</item>
///   <item>Sunset + Deprecation + Link headers are stamped.</item>
///   <item>HEAD on the legacy playlist also 301s.</item>
///   <item>GET on a legacy segment URL also 301s and preserves the
///         segment filename in the rewritten Location.</item>
///   <item>POST on the legacy start endpoint 308s (method-preserving)
///         so the request body isn't dropped on the second hop.</item>
///   <item>Canonical voice route never 301s (no further hop).</item>
/// </list>
/// </summary>
public sealed class LivestreamPathAliasTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-livestream-alias-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
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

    private HttpClient CreateNoRedirectClient() =>
        _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    private const string LegacyPlaylist = "/api/tables/test-game-id/livestream/playlist.m3u8";
    private const string LegacySegment = "/api/tables/test-game-id/livestream/segment-12.ts";

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPlaylist_ReturnsMovedPermanently()
    {
        var client = CreateNoRedirectClient();
        var resp = await client.GetAsync(LegacyPlaylist);
        Assert.Equal(HttpStatusCode.MovedPermanently, resp.StatusCode);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPlaylist_LocationHeader_PointsAtCanonical()
    {
        var client = CreateNoRedirectClient();
        var resp = await client.GetAsync(LegacyPlaylist);
        var loc = resp.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("/api/voice/livestream/", loc, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("playlist.m3u8", loc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-game-id", loc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPlaylist_CacheControl_Is24h()
    {
        var client = CreateNoRedirectClient();
        var resp = await client.GetAsync(LegacyPlaylist);
        var cacheControl = resp.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("public", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=86400", cacheControl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPlaylist_Stamps_DeprecationAndSunset_Headers()
    {
        var client = CreateNoRedirectClient();
        var resp = await client.GetAsync(LegacyPlaylist);
        Assert.True(resp.Headers.Contains("Sunset"), "Sunset header MUST be stamped.");
        Assert.True(resp.Headers.Contains("Deprecation"), "Deprecation header MUST be stamped.");
        Assert.True(resp.Headers.Contains("Link"), "Link rel=sunset header MUST be stamped.");
        var link = resp.Headers.GetValues("Link").FirstOrDefault() ?? string.Empty;
        Assert.Contains("rel=\"sunset\"", link, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPlaylist_HEAD_AlsoRedirects()
    {
        var client = CreateNoRedirectClient();
        using var req = new HttpRequestMessage(HttpMethod.Head, LegacyPlaylist);
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.MovedPermanently, resp.StatusCode);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacySegment_RedirectsWithSegmentFilenamePreserved()
    {
        var client = CreateNoRedirectClient();
        var resp = await client.GetAsync(LegacySegment);
        Assert.Equal(HttpStatusCode.MovedPermanently, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("segment-12.ts", loc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPostStart_Returns308_MethodPreserving()
    {
        var client = CreateNoRedirectClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "/api/tables/test-game-id/livestream/start")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PermanentRedirect, resp.StatusCode);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task CanonicalRoute_NeverRedirects()
    {
        var client = CreateNoRedirectClient();
        var resp = await client.GetAsync(
            "/api/voice/livestream/00000000-0000-0000-0000-000000000000/playlist.m3u8");
        Assert.False(
            resp.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.PermanentRedirect,
            $"Canonical voice route MUST be terminal; got {(int)resp.StatusCode}.");
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public void LegacyAlias_ExposesPublicSunsetConstants()
    {
        // The controller surfaces its sunset date + cache directive
        // as public constants so operator dashboards + the OpenAPI
        // generator can pull the canonical values without scraping
        // the response headers.
        Assert.False(string.IsNullOrEmpty(LegacyLivestreamAliasController.SunsetDate));
        Assert.False(string.IsNullOrEmpty(LegacyLivestreamAliasController.CacheControlDirective));
        Assert.Contains("max-age=86400",
            LegacyLivestreamAliasController.CacheControlDirective,
            StringComparison.OrdinalIgnoreCase);
    }
}
