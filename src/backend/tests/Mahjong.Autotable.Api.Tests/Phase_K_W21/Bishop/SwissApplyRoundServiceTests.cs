using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TournamentEntity = Mahjong.Autotable.Api.Data.Entities.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Bishop;

/// <summary>
/// Phase K Wave 21 — Bishop. Tests for the W21 Swiss apply-round
/// service + controller. Verifies the idempotent
/// audit-rows → TournamentMatch projection + the wire-stable
/// error codes + the admin / X-Admin-Reason gates.
/// </summary>
[Collection("DbSerial")]
public sealed class SwissApplyRoundServiceTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w21-swiss-apply-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SwissApplyRoundServiceTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w21-apply-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<SwissApplyRoundService>();
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private AppDbContext NewDb() => _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
    private SwissApplyRoundService NewService() => _sp.GetRequiredService<SwissApplyRoundService>();

    private async Task<Guid> SeedTournamentAsync(string format = "swiss")
    {
        var tid = Guid.NewGuid();
        await using var db = NewDb();
        db.Tournaments.Add(new TournamentEntity
        {
            Id = tid, Name = "swiss-w21", Format = format, Status = "in-progress",
        });
        await db.SaveChangesAsync();
        return tid;
    }

    private async Task SeedAuditRowsAsync(Guid tid, int round, params (string white, string black)[] pairs)
    {
        await using var db = NewDb();
        int board = 1;
        foreach (var (w, b) in pairs)
        {
            db.SwissPairingAuditEntries.Add(new SwissPairingAuditEntry
            {
                Id = Guid.NewGuid(),
                TournamentId = tid,
                Round = round,
                Board = board++,
                White = w,
                Black = b,
                Tiebreaker = "buchholz",
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_TournamentNotFound_ReturnsErrorCode()
    {
        var r = await NewService().ApplyRoundAsync(Guid.NewGuid(), 1, CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Equal(SwissApplyRoundService.ErrorTournamentNotFound, r.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_NonSwissFormat_ReturnsErrorCode()
    {
        var tid = await SeedTournamentAsync(format: "round-robin");
        var r = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Equal(SwissApplyRoundService.ErrorNotSwissFormat, r.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_RoundZero_ReturnsErrorCode()
    {
        var tid = await SeedTournamentAsync();
        var r = await NewService().ApplyRoundAsync(tid, 0, CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Equal(SwissApplyRoundService.ErrorRoundOutOfRange, r.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_NegativeRound_ReturnsErrorCode()
    {
        var tid = await SeedTournamentAsync();
        var r = await NewService().ApplyRoundAsync(tid, -3, CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Equal(SwissApplyRoundService.ErrorRoundOutOfRange, r.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_RoundNeverPaired_ReturnsErrorCode()
    {
        var tid = await SeedTournamentAsync();
        var r = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Equal(SwissApplyRoundService.ErrorRoundNotPaired, r.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_HappyPath_CreatesMatches()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("p-01", "p-02"), ("p-03", "p-04"));
        var r = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        Assert.True(r.Succeeded);
        Assert.True(r.Created);
        Assert.Equal(2, r.Boards.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_PersistsTournamentMatchRows()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("p-01", "p-02"), ("p-03", "p-04"));
        await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        await using var db = NewDb();
        var matches = await db.TournamentMatches
            .Where(m => m.TournamentId == tid && m.Round == 1)
            .ToListAsync();
        Assert.Equal(2, matches.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_Idempotent_SecondCallReturnsCreatedFalse()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("p-01", "p-02"));
        var r1 = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        var r2 = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        Assert.True(r1.Created);
        Assert.False(r2.Created);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_Idempotent_NoDuplicateMatches()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("p-01", "p-02"));
        await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        await using var db = NewDb();
        var count = await db.TournamentMatches.CountAsync(m => m.TournamentId == tid);
        Assert.Equal(1, count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_StampsReconnectAudit()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("p-01", "p-02"));
        await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        await using var db = NewDb();
        var audits = await db.ReconnectAuditEntries
            .Where(a => a.Kind == ReconnectAuditEntry.KindTournamentSwissRoundApplied)
            .ToListAsync();
        Assert.NotEmpty(audits);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_PreservesWhiteBlackOrder()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("white-1", "black-1"));
        var r = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        Assert.Equal("white-1", r.Boards[0].Player1Id);
        Assert.Equal("black-1", r.Boards[0].Player2Id);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_MultipleBoards_OrderedByBoardNumber()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("a", "b"), ("c", "d"), ("e", "f"));
        var r = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        Assert.Equal(3, r.Boards.Count);
        Assert.Equal(1, r.Boards[0].BoardNumber);
        Assert.Equal(2, r.Boards[1].BoardNumber);
        Assert.Equal(3, r.Boards[2].BoardNumber);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_MultipleRounds_ScopedByRound()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("p1", "p2"));
        await SeedAuditRowsAsync(tid, 2, ("p3", "p4"));
        await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        await using var db = NewDb();
        var r1Matches = await db.TournamentMatches.CountAsync(m => m.TournamentId == tid && m.Round == 1);
        var r2Matches = await db.TournamentMatches.CountAsync(m => m.TournamentId == tid && m.Round == 2);
        Assert.Equal(1, r1Matches);
        Assert.Equal(0, r2Matches);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_ErrorCodesAreWireStable()
    {
        Assert.Equal("tournament-not-found", SwissApplyRoundService.ErrorTournamentNotFound);
        Assert.Equal("not-swiss-format", SwissApplyRoundService.ErrorNotSwissFormat);
        Assert.Equal("round-not-paired", SwissApplyRoundService.ErrorRoundNotPaired);
        Assert.Equal("round-out-of-range", SwissApplyRoundService.ErrorRoundOutOfRange);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_BoardCountMatchesPairings()
    {
        var tid = await SeedTournamentAsync();
        await SeedAuditRowsAsync(tid, 1, ("p1", "p2"), ("p3", "p4"), ("p5", "p6"), ("p7", "p8"));
        var r = await NewService().ApplyRoundAsync(tid, 1, CancellationToken.None);
        Assert.Equal(4, r.Boards.Count);
    }
}

[Collection("DbSerial")]
public sealed class SwissApplyRoundControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w21-swiss-apply-ctrl-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SwissApplyRoundControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w21-apply-ctrl-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<SwissApplyRoundService>();
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SwissApplyRoundController MakeController(
        HttpContext httpContext,
        AuthCookieService cookies,
        string? adminReason = "apply-round-test")
    {
        var controller = new SwissApplyRoundController(
            cookies,
            _sp.GetRequiredService<SwissApplyRoundService>());
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        if (adminReason is not null)
        {
            httpContext.Request.Headers[SwissApplyRoundController.AdminReasonHeader] = adminReason;
        }
        return controller;
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

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.ApplyRound(Guid.NewGuid(),
            new SwissApplyRoundController.ApplyRequest { Round = 1 },
            CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.ApplyRound(Guid.NewGuid(),
            new SwissApplyRoundController.ApplyRequest { Round = 1 },
            CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, adminReason: null);
        var r = await c.ApplyRound(Guid.NewGuid(),
            new SwissApplyRoundController.ApplyRequest { Round = 1 },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, adminReason: new string('x', SwissApplyRoundController.MaxAdminReasonLength + 1));
        var r = await c.ApplyRound(Guid.NewGuid(),
            new SwissApplyRoundController.ApplyRequest { Round = 1 },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.ApplyRound(Guid.NewGuid(), null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_TournamentMissing_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.ApplyRound(Guid.NewGuid(),
            new SwissApplyRoundController.ApplyRequest { Round = 1 },
            CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_RoundNotPaired_Returns422()
    {
        var tid = Guid.NewGuid();
        await using (var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>())
        {
            db.Tournaments.Add(new TournamentEntity
            {
                Id = tid, Name = "t", Format = "swiss", Status = "in-progress",
            });
            await db.SaveChangesAsync();
        }
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.ApplyRound(tid,
            new SwissApplyRoundController.ApplyRequest { Round = 1 },
            CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, s.StatusCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task ApplyRound_HappyPath_Returns200()
    {
        var tid = Guid.NewGuid();
        await using (var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>())
        {
            db.Tournaments.Add(new TournamentEntity
            {
                Id = tid, Name = "t", Format = "swiss", Status = "in-progress",
            });
            db.SwissPairingAuditEntries.Add(new SwissPairingAuditEntry
            {
                TournamentId = tid, Round = 1, Board = 1,
                White = "p1", Black = "p2",
                Tiebreaker = "buchholz", CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.ApplyRound(tid,
            new SwissApplyRoundController.ApplyRequest { Round = 1 },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }
}
