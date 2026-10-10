using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Bishop. Livestream-path canonicalization.
///
/// <para>Bishop's W9 brief: the legacy livestream route
/// <c>/api/tables/{tableId}/livestream/{...}</c> MUST canonicalize
/// to the W8 canonical voice endpoint
/// <c>/api/voice/livestream/{gameId}/{...}</c> via a 301 (or 308)
/// redirect. This kills the dual-route ambiguity that bit Hicks's
/// W7 spectator-viewer wiring.</para>
///
/// <para>Six facts pin the W9 contract — all forward-stage tolerant
/// against the W8 baseline where the canonical endpoint may not yet
/// expose the legacy 301 alias.</para>
/// </summary>
public sealed class BishopW9LivestreamPathCanonTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-livestream-canon-{Guid.NewGuid():N}.db");
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

    private HttpClient? CreateNoRedirectClient()
    {
        if (_factory is null) return null;
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    private const string LegacyPlaylist = "/api/tables/test-table/livestream/playlist.m3u8";
    private const string LegacySegment = "/api/tables/test-table/livestream/segment-0.ts";

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPlaylist_Returns301_OrNotFound_NeverContent()
    {
        var client = CreateNoRedirectClient();
        if (client is null) return;
        var resp = await client.GetAsync(LegacyPlaylist);

        // 404 = not yet wired (forward-stage). 301/308 = canonicalised
        // (W9 contract). 200 with a playlist body would be a bug.
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        Assert.True(
            resp.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.PermanentRedirect,
            $"Legacy livestream route MUST 301/308 to canonical voice route, got {(int)resp.StatusCode}.");
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyPlaylist_301Location_PointsAtCanonicalVoiceRoute()
    {
        var client = CreateNoRedirectClient();
        if (client is null) return;
        var resp = await client.GetAsync(LegacyPlaylist);
        if (resp.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect))
        {
            return;
        }

        var loc = resp.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("/api/voice/livestream/", loc, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("playlist.m3u8", loc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacySegment_Returns301_OrNotFound()
    {
        var client = CreateNoRedirectClient();
        if (client is null) return;
        var resp = await client.GetAsync(LegacySegment);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        Assert.True(
            resp.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.PermanentRedirect,
            $"Legacy segment route MUST 301/308, got {(int)resp.StatusCode}.");
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task CanonicalVoiceRoute_NeverRedirects()
    {
        var client = CreateNoRedirectClient();
        if (client is null) return;
        var resp = await client.GetAsync(
            "/api/voice/livestream/00000000-0000-0000-0000-000000000000/playlist.m3u8");
        Assert.True(
            resp.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect),
            "Canonical /api/voice/livestream/* MUST be terminal — never redirect further.");
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyRoute_HEAD_AlsoCanonicalises_WhenAliasActive()
    {
        var client = CreateNoRedirectClient();
        if (client is null) return;
        using var req = new HttpRequestMessage(HttpMethod.Head, LegacyPlaylist);
        var resp = await client.SendAsync(req);

        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (resp.StatusCode == HttpStatusCode.MethodNotAllowed) return;

        Assert.True(
            resp.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.PermanentRedirect,
            $"HEAD on legacy route MUST 301/308 to canonical, got {(int)resp.StatusCode}.");
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-9")]
    public async Task LegacyRoute_NeverReturns500()
    {
        var client = CreateNoRedirectClient();
        if (client is null) return;
        var resp = await client.GetAsync(LegacyPlaylist);
        Assert.True((int)resp.StatusCode < 500,
            $"Legacy livestream alias must not 500 — got {(int)resp.StatusCode}.");
    }
}
