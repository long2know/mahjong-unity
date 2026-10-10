using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Contract tests for the new
/// <see cref="TournamentController.GetSwissPairingAudit"/>
/// endpoint. Covers: admin auth gate (401/403), empty-tournament
/// response shape, round/board ordering, bye-row detection,
/// per-tournament filtering, and audit-row side effects.
/// </summary>
[Collection("DbSerial")]
public sealed class TournamentSwissPairingAuditControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w19-swiss-audit-controller-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public TournamentSwissPairingAuditControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w19-swiss-c-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private TournamentController MakeController(HttpContext httpContext, AuthCookieService cookies)
    {
        var c = new TournamentController(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            cookies);
        c.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return c;
    }

    private async Task<(AuthCookieService cookies, HttpContext context)> MakeSessionAsync(string role = "admin")
    {
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var issueContext = new DefaultHttpContext();
        var session = await cookies.IssueAsync(issueContext, $"player-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var resolveContext = new DefaultHttpContext();
        resolveContext.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={session.Token}";
        return (cookies, resolveContext);
    }

    private async Task SeedAsync(Guid tid, params (int round, int board, string white, string black)[] rows)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (round, board, white, black) in rows)
        {
            db.SwissPairingAuditEntries.Add(new SwissPairingAuditEntry
            {
                TournamentId = tid, Round = round, Board = board,
                White = white, Black = black,
                Tiebreaker = "buchholz",
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(Guid.NewGuid(), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_EmptyTournament_ReturnsOkWithEmptyEntries()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(Guid.NewGuid(), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"rowCount\":0", json);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_ReturnsRowsOrderedByRoundThenBoard()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid,
            (2, 1, "w21", "b21"),
            (1, 2, "w12", "b12"),
            (1, 1, "w11", "b11"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(tid, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        var w11 = json.IndexOf("w11", StringComparison.Ordinal);
        var w12 = json.IndexOf("w12", StringComparison.Ordinal);
        var w21 = json.IndexOf("w21", StringComparison.Ordinal);
        Assert.True(w11 < w12 && w12 < w21,
            $"Ordering violated: w11={w11}, w12={w12}, w21={w21}");
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_TournamentFilter_RestrictsResultSet()
    {
        var tidA = Guid.NewGuid();
        var tidB = Guid.NewGuid();
        await SeedAsync(tidA, (1, 1, "a-w", "a-b"));
        await SeedAsync(tidB, (1, 1, "b-w", "b-b"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(tidA, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"rowCount\":1", json);
        Assert.Contains("\"a-w\"", json);
        Assert.DoesNotContain("\"b-w\"", json);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_FlagsByeRows()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid, (1, 1, "w", FideC04SwissPairingService.ByeOpponent));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(tid, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"isBye\":true", json);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_NonByeRows_FlaggedFalse()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid, (1, 1, "w", "b"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(tid, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"isBye\":false", json);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_WritesAuditRowOnReads()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid, (1, 1, "w", "b"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.GetSwissPairingAudit(tid, CancellationToken.None);
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var n = await db.ReconnectAuditEntries
            .CountAsync(e => e.Kind == ReconnectAuditEntry.KindTournamentSwissPairingAuditRead);
        Assert.Equal(1, n);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_AuditDetail_CarriesTournamentAndRowCount()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid, (1, 1, "w", "b"), (1, 2, "w2", "b2"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.GetSwissPairingAudit(tid, CancellationToken.None);
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.ReconnectAuditEntries
            .Where(e => e.Kind == ReconnectAuditEntry.KindTournamentSwissPairingAuditRead)
            .OrderByDescending(e => e.At).FirstAsync();
        Assert.Contains($"tournament={tid:N}", entry.Detail);
        Assert.Contains("rows=2", entry.Detail);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_ResponseShape_ContainsExpectedFields()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid, (1, 1, "wp", "bp"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(tid, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"tournamentId\"", json);
        Assert.Contains("\"entries\"", json);
        Assert.Contains("\"rowCount\":1", json);
        Assert.Contains("\"white\":\"wp\"", json);
        Assert.Contains("\"black\":\"bp\"", json);
        Assert.Contains("\"tiebreaker\":\"buchholz\"", json);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_MultiRoundOrdering_IsStable()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid,
            (3, 2, "w32", "b32"),
            (3, 1, "w31", "b31"),
            (2, 1, "w21", "b21"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(tid, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        var idx21 = json.IndexOf("w21", StringComparison.Ordinal);
        var idx31 = json.IndexOf("w31", StringComparison.Ordinal);
        var idx32 = json.IndexOf("w32", StringComparison.Ordinal);
        Assert.True(idx21 < idx31 && idx31 < idx32);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task GetSwissPairingAudit_DoesNotLeakOtherTournamentRows()
    {
        var tidA = Guid.NewGuid();
        var tidB = Guid.NewGuid();
        await SeedAsync(tidA, (1, 1, "a1", "a2"));
        await SeedAsync(tidB, (1, 1, "b1", "b2"));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.GetSwissPairingAudit(tidB, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"b1\"", json);
        Assert.DoesNotContain("\"a1\"", json);
    }
}
