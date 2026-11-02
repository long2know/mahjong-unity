using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using TournamentEntity = Mahjong.Autotable.Api.Data.Entities.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Bishop;

/// <summary>
/// Phase K Wave 22 — Bishop. Tests for the W22 tournament
/// finalization endpoint: admin gate, X-Admin-Reason validation,
/// incomplete-rounds rejection, standings computation, idempotency,
/// audit row writes, TournamentCompleted event emission.
/// </summary>
[Collection("DbSerial")]
public sealed class TournamentFinalizationControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w22-finalize-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public TournamentFinalizationControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w22-finalize-{Guid.NewGuid():N}.sqlite");
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

    private async Task<(AuthCookieService, HttpContext)> MakeSessionAsync(string role = "admin")
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

    private TournamentFinalizationController MakeController(HttpContext ctx, AuthCookieService cookies, string? reason = "finalize-test")
    {
        var c = new TournamentFinalizationController(cookies, _sp.GetRequiredService<IServiceScopeFactory>());
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        if (reason is not null) ctx.Request.Headers[TournamentFinalizationController.AdminReasonHeader] = reason;
        return c;
    }

    private async Task<Guid> SeedTournamentAsync(string status = "in-progress")
    {
        var tid = Guid.NewGuid();
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tournaments.Add(new TournamentEntity
        {
            Id = tid, Name = "finalize-t", Format = "swiss", Status = status,
        });
        await db.SaveChangesAsync();
        return tid;
    }

    private async Task SeedRegistrationAsync(Guid tid, string pid, int seed = 1)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TournamentRegistrations.Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(), TournamentId = tid, PlayerId = pid, Seed = seed,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedMatchAsync(Guid tid, int round, string p1, string p2, string status = "complete", string? winner = null)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TournamentMatches.Add(new TournamentMatch
        {
            Id = Guid.NewGuid(), TournamentId = tid, Round = round,
            Player1Id = p1, Player2Id = p2, Status = status,
            WinnerPlayerId = winner,
            CompletedAt = status == "complete" ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.Finalize(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: null);
        var r = await c.Finalize(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_BlankReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: "   ");
        var r = await c.Finalize(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: new string('r', TournamentFinalizationController.MaxAdminReasonLength + 1));
        var r = await c.Finalize(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_TournamentNotFound_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_DraftTournament_Returns400()
    {
        var tid = await SeedTournamentAsync(status: "draft");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(tid, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_IncompleteRounds_Returns409()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, round: 1, "p1", "p2", status: "pending");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(tid, CancellationToken.None);
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ConflictObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_InProgressMatch_Returns409()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, round: 1, "p1", "p2", status: "in-progress");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(tid, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_AllComplete_Returns200()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedRegistrationAsync(tid, "p2", seed: 2);
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(tid, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_PersistsStandings()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedRegistrationAsync(tid, "p2", seed: 2);
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var standings = await db.TournamentStandings.AsNoTracking()
            .Where(s => s.TournamentId == tid).OrderBy(s => s.Rank).ToListAsync();
        Assert.Equal(2, standings.Count);
        Assert.Equal("p1", standings[0].PlayerId);
        Assert.Equal(1, standings[0].Rank);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_StatusBecomesComplete()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t = await db.Tournaments.AsNoTracking().FirstAsync(x => x.Id == tid);
        Assert.Equal("complete", t.Status);
        Assert.NotNull(t.CompletedAt);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_EmitsTournamentFinalizedAndCompletedAudit()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(await db.ReconnectAuditEntries.AsNoTracking().ToListAsync(),
            e => e.Kind == ReconnectAuditEntry.KindTournamentFinalized);
        Assert.Contains(await db.ReconnectAuditEntries.AsNoTracking().ToListAsync(),
            e => e.Kind == ReconnectAuditEntry.KindTournamentCompleted);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_IsIdempotent_SecondCallReturnsExistingStandings()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Finalize(tid, CancellationToken.None);
        var (cookies2, ctx2) = await MakeSessionAsync();
        var c2 = MakeController(ctx2, cookies2);
        var r2 = await c2.Finalize(tid, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r2);
        Assert.NotNull(ok.Value);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_IdempotentSecondCallDoesNotDoubleStandings()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        int countAfterFirst;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            countAfterFirst = await db.TournamentStandings.AsNoTracking().CountAsync(s => s.TournamentId == tid);
        }
        var (cookies2, ctx2) = await MakeSessionAsync();
        await MakeController(ctx2, cookies2).Finalize(tid, CancellationToken.None);
        using var scope2 = _sp.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var countAfterSecond = await db2.TournamentStandings.AsNoTracking().CountAsync(s => s.TournamentId == tid);
        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_WinnerRankIs1()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "winner");
        await SeedRegistrationAsync(tid, "loser", seed: 2);
        await SeedMatchAsync(tid, round: 1, "winner", "loser", winner: "winner");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var top = await db.TournamentStandings.AsNoTracking().FirstAsync(s => s.Rank == 1);
        Assert.Equal("winner", top.PlayerId);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_TiedPlayersShareRank()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "a");
        await SeedRegistrationAsync(tid, "b", seed: 2);
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var standings = await db.TournamentStandings.AsNoTracking().Where(s => s.TournamentId == tid).ToListAsync();
        Assert.All(standings, s => Assert.Equal(1, s.Rank));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_EmptyTournament_StillSucceeds()
    {
        var tid = await SeedTournamentAsync();
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_FourPlayerRoundRobin_ComputesGamesPlayed()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedRegistrationAsync(tid, "p2", seed: 2);
        await SeedRegistrationAsync(tid, "p3", seed: 3);
        await SeedRegistrationAsync(tid, "p4", seed: 4);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TournamentMatches.Add(new TournamentMatch
            {
                Id = Guid.NewGuid(), TournamentId = tid, Round = 1,
                Player1Id = "p1", Player2Id = "p2", Player3Id = "p3", Player4Id = "p4",
                Status = "complete", WinnerPlayerId = "p1",
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        using var scope2 = _sp.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var standings = await db2.TournamentStandings.AsNoTracking().Where(s => s.TournamentId == tid).ToListAsync();
        Assert.Equal(4, standings.Count);
        Assert.All(standings, s => Assert.Equal(1, s.GamesPlayed));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_WinnerHasOnePoint()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedRegistrationAsync(tid, "p2", seed: 2);
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var standings = await db.TournamentStandings.AsNoTracking().Where(s => s.TournamentId == tid).ToListAsync();
        Assert.Equal(1, standings.First(s => s.PlayerId == "p1").Points);
        Assert.Equal(0, standings.First(s => s.PlayerId == "p2").Points);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_AuditDetailContainsTournamentId()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies, reason: "post-mortem-77").Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries.AsNoTracking()
            .FirstAsync(e => e.Kind == ReconnectAuditEntry.KindTournamentFinalized);
        Assert.Contains(tid.ToString("N"), row.Detail ?? string.Empty);
        Assert.Contains("post-mortem-77", row.Detail ?? string.Empty);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_AuditCompletedRowContainsWinner()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "champ");
        await SeedRegistrationAsync(tid, "runner-up", seed: 2);
        await SeedMatchAsync(tid, round: 1, "champ", "runner-up", winner: "champ");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries.AsNoTracking()
            .FirstAsync(e => e.Kind == ReconnectAuditEntry.KindTournamentCompleted);
        Assert.Contains("champ", row.Detail ?? string.Empty);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_LocksAllRounds_NoMatchesMutated()
    {
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        await SeedMatchAsync(tid, round: 2, "p1", "p2", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matches = await db.TournamentMatches.AsNoTracking().Where(m => m.TournamentId == tid).ToListAsync();
        Assert.All(matches, m => Assert.Equal("complete", m.Status));
    }

    [Theory, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    [InlineData("pending")]
    [InlineData("in-progress")]
    [InlineData("PENDING")]
    public async Task Finalize_AnyNonCompleteStatusBlocksFinalize(string matchStatus)
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, round: 1, "p1", "p2", status: matchStatus);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(tid, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_EnumerateSeats_TwoPlayer()
    {
        var m = new TournamentMatch { Player1Id = "a", Player2Id = "b" };
        Assert.Equal(new[] { "a", "b" }, TournamentFinalizationController.EnumerateSeats(m).ToArray());
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_EnumerateSeats_FourPlayer()
    {
        var m = new TournamentMatch { Player1Id = "a", Player2Id = "b", Player3Id = "c", Player4Id = "d" };
        Assert.Equal(new[] { "a", "b", "c", "d" }, TournamentFinalizationController.EnumerateSeats(m).ToArray());
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_AlreadyCompleteTournament_ReturnsIdempotent()
    {
        var tid = await SeedTournamentAsync(status: "complete");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Finalize(tid, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Finalize_RankSkipsTieCohort()
    {
        // 3 players: p1=2 wins, p2=p3=0 wins → p1 rank1, p2/p3 share rank2
        var tid = await SeedTournamentAsync();
        await SeedRegistrationAsync(tid, "p1");
        await SeedRegistrationAsync(tid, "p2", seed: 2);
        await SeedRegistrationAsync(tid, "p3", seed: 3);
        await SeedMatchAsync(tid, round: 1, "p1", "p2", winner: "p1");
        await SeedMatchAsync(tid, round: 2, "p1", "p3", winner: "p1");
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Finalize(tid, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var standings = await db.TournamentStandings.AsNoTracking()
            .Where(s => s.TournamentId == tid).ToListAsync();
        Assert.Equal(1, standings.First(s => s.PlayerId == "p1").Rank);
        Assert.Equal(2, standings.First(s => s.PlayerId == "p2").Rank);
        Assert.Equal(2, standings.First(s => s.PlayerId == "p3").Rank);
    }
}
