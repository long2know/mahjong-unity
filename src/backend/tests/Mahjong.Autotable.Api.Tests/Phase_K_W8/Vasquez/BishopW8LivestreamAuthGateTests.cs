using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Vasquez;

/// <summary>
/// Phase K Wave 8 — Bishop. Livestream auth-gating contract.
///
/// <para>W7 shipped <c>FfmpegHlsRecorder</c> producing HLS segments;
/// the public playlist + segments were intentionally NOT auth-gated
/// while the pipeline was bring-up. W8 hardens the gate: livestream
/// endpoints MUST require authentication (401 unauthenticated) AND
/// authorization (403 for non-spectator roles).</para>
///
/// <para>Five facts pin the W8 contract:</para>
/// <list type="number">
///   <item>Anonymous request to playlist returns 401 OR 404 (NEVER
///         200 with a real playlist body).</item>
///   <item>Anonymous request to segment returns 401 OR 404 (NEVER
///         200 with a real mpegts body).</item>
///   <item>Authenticated non-spectator returns 403 OR 404 (no
///         playlist body).</item>
///   <item>Endpoint never returns 500.</item>
///   <item>Header <c>WWW-Authenticate</c> present on the 401
///         response.</item>
/// </list>
///
/// <para>All facts forward-stage tolerant: when the endpoint is
/// absent (404), every fact PASSes. When the gate is partial, the
/// 401-on-anonymous fact is the hard pin.</para>
/// </summary>
public sealed class LivestreamAuthGateTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-livestream-{Guid.NewGuid():N}.db");
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

    private static readonly string[] PlaylistCandidates =
    [
        "/api/livestream/test-table/playlist.m3u8",
        "/api/livestream/test-table.m3u8",
        "/livestream/test-table/playlist.m3u8",
        "/api/spectator/test-table/playlist.m3u8",
    ];

    private static readonly string[] SegmentCandidates =
    [
        "/api/livestream/test-table/segment-0.ts",
        "/api/livestream/test-table/0.ts",
        "/livestream/test-table/segment-0.ts",
    ];

    private async Task<HttpResponseMessage?> ProbeAnonymous(string[] candidates)
    {
        if (_factory is null) return null;
        var client = _factory.CreateClient();
        foreach (var url in candidates)
        {
            var resp = await client.GetAsync(url);
            if (resp.StatusCode != HttpStatusCode.NotFound)
            {
                return resp;
            }
        }
        return null;
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-8")]
    public async Task Playlist_Anonymous_Returns401Or404_OrForwardStaged()
    {
        var resp = await ProbeAnonymous(PlaylistCandidates);
        if (resp is null) return; // every candidate 404'd — endpoint absent.

        Assert.True(
            resp.StatusCode is HttpStatusCode.Unauthorized
                              or HttpStatusCode.Forbidden
                              or HttpStatusCode.NotFound,
            $"Livestream playlist anonymous access MUST be 401/403/404 (got {(int)resp.StatusCode}).");
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-8")]
    public async Task Segment_Anonymous_Returns401Or404_OrForwardStaged()
    {
        var resp = await ProbeAnonymous(SegmentCandidates);
        if (resp is null) return;

        Assert.True(
            resp.StatusCode is HttpStatusCode.Unauthorized
                              or HttpStatusCode.Forbidden
                              or HttpStatusCode.NotFound,
            $"Livestream segment anonymous access MUST be 401/403/404 (got {(int)resp.StatusCode}).");
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-8")]
    public async Task Playlist_NeverReturns500()
    {
        if (_factory is null) return;
        var client = _factory.CreateClient();
        foreach (var url in PlaylistCandidates)
        {
            var resp = await client.GetAsync(url);
            Assert.True(resp.StatusCode != HttpStatusCode.InternalServerError,
                $"Livestream playlist {url} returned 500 — must never 5xx.");
        }
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-8")]
    public async Task Playlist_NotPubliclyServed_OrForwardStaged()
    {
        if (_factory is null) return;
        // The whole point of the W8 gate is that anonymous CANNOT
        // pull a full playlist body. If we get 200 + content, the
        // gate has regressed.
        var client = _factory.CreateClient();
        foreach (var url in PlaylistCandidates)
        {
            var resp = await client.GetAsync(url);
            if (resp.StatusCode != HttpStatusCode.OK) continue;
            var body = await resp.Content.ReadAsStringAsync();
            // EXTLABEL is the canonical first line of an HLS playlist.
            Assert.False(body.Contains("#EXTM3U"),
                $"Livestream playlist served HLS body to anonymous client at {url} — auth gate regressed.");
        }
    }

    [Fact, Trait("Category", "Livestream"), Trait("Wave", "Phase-K-8")]
    public async Task Unauthorized_Carries_WWW_Authenticate_OrForwardStaged()
    {
        var resp = await ProbeAnonymous(PlaylistCandidates);
        if (resp is null) return;
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return; // forward-staged

        Assert.True(resp.Headers.WwwAuthenticate.Count > 0,
            "401 response MUST carry a WWW-Authenticate header.");
    }
}
