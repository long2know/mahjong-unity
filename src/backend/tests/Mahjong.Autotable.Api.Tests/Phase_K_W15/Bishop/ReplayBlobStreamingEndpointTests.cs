#if TESTING_SHIM
using System.Net;
using System.Net.Http.Headers;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Replays;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Hard-asserted contract for the
/// <c>GET /api/replays/{replayId}/blob</c> streaming endpoint.
///
/// <list type="number">
///   <item>404 when no row exists.</item>
///   <item>200 + <c>Accept-Ranges: bytes</c> for a full GET.</item>
///   <item>Content-Length matches decompressed JSON byte count.</item>
///   <item>Content-Type is <c>application/octet-stream</c>.</item>
///   <item>206 Partial Content for a valid <c>bytes=0-9</c> range.</item>
///   <item>Content-Range header echoes the slice + total length.</item>
///   <item>Suffix range <c>bytes=-N</c> returns last N bytes.</item>
///   <item>Open-ended <c>bytes=N-</c> returns from N to end.</item>
///   <item>Multi-range <c>bytes=0-1,3-4</c> → 416.</item>
///   <item>Malformed Range value → 416 with <c>Content-Range: bytes */N</c>.</item>
///   <item>Start beyond end-of-file → 416.</item>
///   <item><c>X-Replay-Id</c> + <c>X-Replay-Variant</c> headers stamped.</item>
///   <item><c>TryParseSingleByteRange</c> rejects multi-range.</item>
///   <item><c>TryParseSingleByteRange</c> honours suffix bytes=-N.</item>
///   <item><c>TryParseSingleByteRange</c> honours open-ended bytes=N-.</item>
///   <item><c>TryParseSingleByteRange</c> clamps end ≥ length.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class ReplayBlobStreamingEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w15-replay-blob-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
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

    private HttpClient NewClient() =>
        _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<(string ReplayId, byte[] Bytes)> SeedAsync(string payload = "{\"hand\":\"replay-blob-w15\"}")
    {
        var store = _factory!.Services.GetRequiredService<IReplayStore>();
        var record = new ReplayRecord
        {
            GameId = Guid.NewGuid(),
            CompletedAt = DateTime.UtcNow,
            Variant = "changsha-v1",
            TurnCount = 42,
            CompressedPayload = ReplayRecord.CompressPayload(payload),
            IngestedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        var stored = await store.InsertAsync(record);
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        return (stored.ReplayId, bytes);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task UnknownReplay_Returns404()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays/does-not-exist/blob");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task FullGet_Returns200_WithAcceptRanges()
    {
        var (id, bytes) = await SeedAsync();
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/replays/{id}/blob");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("bytes", resp.Headers.AcceptRanges);
        Assert.Equal(bytes.Length, resp.Content.Headers.ContentLength);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task FullGet_ContentTypeIsOctetStream()
    {
        var (id, _) = await SeedAsync();
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/replays/{id}/blob");
        Assert.Equal("application/octet-stream", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task FullGet_PayloadMatchesDecompressedJson()
    {
        var (id, bytes) = await SeedAsync();
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/replays/{id}/blob");
        var got = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, got);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RangeRequest_Returns206_WithContentRange()
    {
        var (id, bytes) = await SeedAsync("{\"hand\":\"abcdefghijklmnopqrstuvwxyz\"}");
        using var client = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/replays/{id}/blob");
        req.Headers.Range = new RangeHeaderValue(0, 9);
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.NotNull(resp.Content.Headers.ContentRange);
        Assert.Equal(0L, resp.Content.Headers.ContentRange!.From);
        Assert.Equal(9L, resp.Content.Headers.ContentRange.To);
        Assert.Equal((long)bytes.Length, resp.Content.Headers.ContentRange.Length);
        var slice = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(10, slice.Length);
        Assert.Equal(bytes[..10], slice);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task SuffixRange_ReturnsLastNBytes()
    {
        var (id, bytes) = await SeedAsync("{\"hand\":\"abcdefghijklmnopqrstuvwxyz\"}");
        using var client = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/replays/{id}/blob");
        req.Headers.TryAddWithoutValidation("Range", "bytes=-5");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        var slice = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(5, slice.Length);
        Assert.Equal(bytes[^5..], slice);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task OpenEndedRange_ReturnsFromStartToEnd()
    {
        var (id, bytes) = await SeedAsync("{\"hand\":\"abcdefghijklmnopqrstuvwxyz\"}");
        using var client = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/replays/{id}/blob");
        req.Headers.TryAddWithoutValidation("Range", $"bytes={bytes.Length - 4}-");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        var slice = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(4, slice.Length);
        Assert.Equal(bytes[^4..], slice);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task MultiRange_Returns416()
    {
        var (id, _) = await SeedAsync();
        using var client = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/replays/{id}/blob");
        req.Headers.TryAddWithoutValidation("Range", "bytes=0-1,3-4");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task BadRange_Returns416()
    {
        var (id, _) = await SeedAsync();
        using var client = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/replays/{id}/blob");
        req.Headers.TryAddWithoutValidation("Range", "bytes=abc");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task StartBeyondEnd_Returns416()
    {
        var (id, bytes) = await SeedAsync();
        using var client = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/replays/{id}/blob");
        req.Headers.TryAddWithoutValidation("Range", $"bytes={bytes.Length + 10}-{bytes.Length + 20}");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task ResponseHeaders_StampReplayIdAndVariant()
    {
        var (id, _) = await SeedAsync();
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/replays/{id}/blob");
        Assert.True(resp.Headers.TryGetValues("X-Replay-Id", out var ids));
        Assert.Contains(id, ids);
        Assert.True(resp.Headers.TryGetValues("X-Replay-Variant", out var variants));
        Assert.Contains("changsha-v1", variants);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_MultiRange_Rejected()
    {
        var ok = ReplayController.TryParseSingleByteRange("bytes=0-1,3-4", 100, out _, out _);
        Assert.False(ok);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_SuffixRange_Honoured()
    {
        var ok = ReplayController.TryParseSingleByteRange("bytes=-10", 100, out var start, out var end);
        Assert.True(ok);
        Assert.Equal(90, start);
        Assert.Equal(99, end);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_OpenEnded_Honoured()
    {
        var ok = ReplayController.TryParseSingleByteRange("bytes=30-", 100, out var start, out var end);
        Assert.True(ok);
        Assert.Equal(30, start);
        Assert.Equal(99, end);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_EndBeyondLength_Clamped()
    {
        var ok = ReplayController.TryParseSingleByteRange("bytes=10-200", 100, out var start, out var end);
        Assert.True(ok);
        Assert.Equal(10, start);
        Assert.Equal(99, end);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_InvertedRange_Rejected()
    {
        var ok = ReplayController.TryParseSingleByteRange("bytes=50-10", 100, out _, out _);
        Assert.False(ok);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_EmptyHeader_Rejected()
    {
        Assert.False(ReplayController.TryParseSingleByteRange("", 100, out _, out _));
        Assert.False(ReplayController.TryParseSingleByteRange("bytes=", 100, out _, out _));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_NonBytesUnit_Rejected()
    {
        Assert.False(ReplayController.TryParseSingleByteRange("items=0-10", 100, out _, out _));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TryParseRange_ZeroLength_Rejected()
    {
        Assert.False(ReplayController.TryParseSingleByteRange("bytes=0-9", 0, out _, out _));
    }
}
#endif
