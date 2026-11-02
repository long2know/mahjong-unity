using System.Text;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Replays;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Bishop;

/// <summary>
/// Phase K Wave 22 — Bishop. Tests for the W22 admin-gated
/// chunked replay download endpoint: auth gate, chunk indexing,
/// out-of-range rejection, ETag stability, 304 Not Modified,
/// Range request handling, custom chunk size.
/// </summary>
public sealed class ReplayChunksControllerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbPath;
    private readonly InMemoryReplayStore _store;

    public ReplayChunksControllerTests()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "bishop-w22-chunks-sqlite");
        Directory.CreateDirectory(scratch);
        _dbPath = Path.Combine(scratch, $"chunks-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        _store = new InMemoryReplayStore();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<(AuthCookieService, HttpContext)> MakeSessionAsync(string role = "admin")
    {
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var issue = new DefaultHttpContext();
        var s = await cookies.IssueAsync(issue, $"p-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={s.Token}";
        ctx.Response.Body = new MemoryStream();
        return (cookies, ctx);
    }

    private ReplayChunksController MakeController(HttpContext ctx, AuthCookieService cookies)
    {
        var c = new ReplayChunksController(cookies, _store, NullLogger<ReplayChunksController>.Instance);
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        return c;
    }

    private async Task<string> SeedReplayAsync(string payload)
    {
        var record = new ReplayRecord
        {
            ReplayId = $"r-{Guid.NewGuid():N}".Substring(0, 10),
            GameId = Guid.NewGuid(),
            CompletedAt = DateTime.UtcNow,
            CompressedPayload = ReplayRecord.CompressPayload(payload),
            IngestedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        await _store.InsertAsync(record);
        return record.ReplayId;
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var c = MakeController(ctx, cookies);
        var r = await c.GetChunk("rid", 1, null, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.GetChunk("rid", 1, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_ReplayNotFound_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetChunk("rid-does-not-exist", 1, null, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_InvalidChunkIndex_Returns400()
    {
        var rid = await SeedReplayAsync("hello world");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetChunk(rid, 0, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_NegativeChunkIndex_Returns400()
    {
        var rid = await SeedReplayAsync("hi");
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetChunk(rid, -3, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_InvalidChunkSizeTooSmall_Returns400()
    {
        var rid = await SeedReplayAsync("hello world");
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetChunk(rid, 1, 10, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_InvalidChunkSizeTooLarge_Returns400()
    {
        var rid = await SeedReplayAsync("hello");
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies)
            .GetChunk(rid, 1, ReplayChunksController.MaxChunkSize + 1, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_OutOfRange_Returns404()
    {
        var rid = await SeedReplayAsync("hello");
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetChunk(rid, 999, null, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_FirstChunk_Returns200()
    {
        var rid = await SeedReplayAsync(new string('A', 200_000));
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.IsType<EmptyResult>(r);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_StampsETag()
    {
        var rid = await SeedReplayAsync("hello");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(ctx.Response.Headers.ETag.ToString()));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_StampsChunkHeaders()
    {
        var rid = await SeedReplayAsync(new string('X', 300_000));
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.True(ctx.Response.Headers.ContainsKey("X-Replay-Chunk-Index"));
        Assert.True(ctx.Response.Headers.ContainsKey("X-Replay-Chunk-Count"));
        Assert.True(ctx.Response.Headers.ContainsKey("X-Replay-Chunk-Size"));
        Assert.True(ctx.Response.Headers.ContainsKey("X-Replay-Total-Length"));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_AcceptsCustomChunkSize()
    {
        var rid = await SeedReplayAsync(new string('Y', 50_000));
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetChunk(rid, 1, 4096, CancellationToken.None);
        Assert.IsType<EmptyResult>(r);
        Assert.Equal("4096", ctx.Response.Headers["X-Replay-Chunk-Size"].ToString());
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_ChunkCountMatchesPayload()
    {
        var rid = await SeedReplayAsync(new string('Z', 100_000));
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).GetChunk(rid, 1, 1024, CancellationToken.None);
        Assert.Equal("98", ctx.Response.Headers["X-Replay-Chunk-Count"].ToString()); // 100000/1024 = 97.66 → 98
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_IfNoneMatch_Returns304()
    {
        var rid = await SeedReplayAsync("etag-test");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        var etag = ctx.Response.Headers.ETag.ToString();

        var (cookies2, ctx2) = await MakeSessionAsync();
        ctx2.Request.Headers["If-None-Match"] = etag;
        var r = await MakeController(ctx2, cookies2).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status304NotModified,
            Assert.IsType<StatusCodeResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_ETagDiffersByChunkIndex()
    {
        var rid = await SeedReplayAsync(new string('A', 200_000));
        var (cookies, ctx1) = await MakeSessionAsync();
        await MakeController(ctx1, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        var etag1 = ctx1.Response.Headers.ETag.ToString();

        var (cookies2, ctx2) = await MakeSessionAsync();
        await MakeController(ctx2, cookies2).GetChunk(rid, 2, null, CancellationToken.None);
        var etag2 = ctx2.Response.Headers.ETag.ToString();
        Assert.NotEqual(etag1, etag2);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_RangeHeader_Returns206()
    {
        var rid = await SeedReplayAsync(new string('A', 100_000));
        var (cookies, ctx) = await MakeSessionAsync();
        ctx.Request.Headers["Range"] = "bytes=0-99";
        await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status206PartialContent, ctx.Response.StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_MalformedRange_Returns416()
    {
        var rid = await SeedReplayAsync("hello world");
        var (cookies, ctx) = await MakeSessionAsync();
        ctx.Request.Headers["Range"] = "garbage";
        var r = await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable,
            Assert.IsType<StatusCodeResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_SuffixRange_HonoursLastBytes()
    {
        var rid = await SeedReplayAsync(new string('A', 10000));
        var (cookies, ctx) = await MakeSessionAsync();
        ctx.Request.Headers["Range"] = "bytes=-100";
        await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status206PartialContent, ctx.Response.StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_RangeOutOfChunk_Returns416()
    {
        var rid = await SeedReplayAsync("abc");
        var (cookies, ctx) = await MakeSessionAsync();
        ctx.Request.Headers["Range"] = "bytes=99999-100000";
        var r = await MakeController(ctx, cookies).GetChunk(rid, 1, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable,
            Assert.IsType<StatusCodeResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_BlankReplayId_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetChunk("", 1, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ComputeChunkCount_HandlesExactBoundary()
    {
        Assert.Equal(2, ReplayChunksController.ComputeChunkCount(2048, 1024));
        Assert.Equal(3, ReplayChunksController.ComputeChunkCount(2049, 1024));
        Assert.Equal(1, ReplayChunksController.ComputeChunkCount(1, 1024));
        Assert.Equal(0, ReplayChunksController.ComputeChunkCount(0, 1024));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ComputeEtag_IsStableForSamePayload()
    {
        var payload = Encoding.UTF8.GetBytes("stable");
        var a = ReplayChunksController.ComputeEtag(payload, 1024, 1);
        var b = ReplayChunksController.ComputeEtag(payload, 1024, 1);
        Assert.Equal(a, b);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ComputeEtag_ChangesWithChunkSize()
    {
        var payload = Encoding.UTF8.GetBytes("changes-with-chunk-size");
        var a = ReplayChunksController.ComputeEtag(payload, 1024, 1);
        var b = ReplayChunksController.ComputeEtag(payload, 2048, 1);
        Assert.NotEqual(a, b);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void TryParseSingleByteRange_AcceptsCanonical()
    {
        Assert.True(ReplayChunksController.TryParseSingleByteRange("bytes=0-99", 1000, out var s, out var e));
        Assert.Equal(0, s);
        Assert.Equal(99, e);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void TryParseSingleByteRange_RejectsMulti()
    {
        Assert.False(ReplayChunksController.TryParseSingleByteRange("bytes=0-99,200-299", 1000, out _, out _));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void TryParseSingleByteRange_OpenEnded()
    {
        Assert.True(ReplayChunksController.TryParseSingleByteRange("bytes=10-", 100, out var s, out var e));
        Assert.Equal(10, s);
        Assert.Equal(99, e);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task GetChunk_LastChunkSliceIsTruncated()
    {
        var rid = await SeedReplayAsync(new string('A', 5000));
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).GetChunk(rid, 5, 1024, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        // 5000/1024 = 4 full + 904 byte tail. Chunk 5 of total 5 has length 904.
        Assert.Equal(904, ctx.Response.ContentLength);
    }
}
