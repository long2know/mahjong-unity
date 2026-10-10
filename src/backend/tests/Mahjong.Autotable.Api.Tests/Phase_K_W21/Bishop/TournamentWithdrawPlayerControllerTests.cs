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

namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Bishop;

/// <summary>
/// Phase K Wave 21 — Bishop. Tests for the W21 tournament
/// withdraw-player surface: admin gate, registration sentinel
/// stamping, in-flight match removal, audit row writes,
/// idempotency guards.
/// </summary>
[Collection("DbSerial")]
public sealed class TournamentWithdrawPlayerControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w21-withdraw-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public TournamentWithdrawPlayerControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w21-withdraw-{Guid.NewGuid():N}.sqlite");
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

    private TournamentWithdrawPlayerController MakeController(HttpContext ctx, AuthCookieService cookies, string? reason = "withdraw-test")
    {
        var c = new TournamentWithdrawPlayerController(cookies, _sp.GetRequiredService<IServiceScopeFactory>());
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        if (reason is not null) ctx.Request.Headers[TournamentWithdrawPlayerController.AdminReasonHeader] = reason;
        return c;
    }

    private async Task<Guid> SeedTournamentAsync(string playerId, int seed = 1)
    {
        var tid = Guid.NewGuid();
        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tournaments.Add(new TournamentEntity
        {
            Id = tid, Name = "withdraw-t", Format = "swiss", Status = "in-progress",
        });
        db.TournamentRegistrations.Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(), TournamentId = tid, PlayerId = playerId, Seed = seed,
        });
        await db.SaveChangesAsync();
        return tid;
    }

    private async Task SeedMatchAsync(Guid tid, int round, string p1, string p2, string status = "pending")
    {
        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        db.TournamentMatches.Add(new TournamentMatch
        {
            Id = Guid.NewGuid(), TournamentId = tid, Round = round,
            Player1Id = p1, Player2Id = p2, Status = status,
        });
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.WithdrawPlayer(Guid.NewGuid(), new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p1" }, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(Guid.NewGuid(), new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p1" }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: null);
        var r = await c.WithdrawPlayer(Guid.NewGuid(), new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p1" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies,
            reason: new string('x', TournamentWithdrawPlayerController.MaxAdminReasonLength + 1));
        var r = await c.WithdrawPlayer(Guid.NewGuid(), new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p1" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(Guid.NewGuid(), null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_EmptyPlayerId_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(Guid.NewGuid(), new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_TournamentMissing_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(Guid.NewGuid(),
            new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p1" }, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_PlayerNotRegistered_Returns404()
    {
        var tid = await SeedTournamentAsync("p-real");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(tid,
            new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p-not-here" }, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_HappyPath_StampsSentinel()
    {
        var tid = await SeedTournamentAsync("p-withdraw");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(tid,
            new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p-withdraw" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var reg = await db.TournamentRegistrations.FirstAsync(r => r.PlayerId == "p-withdraw");
        Assert.Equal(TournamentWithdrawPlayerController.WithdrawnSeedSentinel, reg.Seed);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_RemovesInFlightMatches()
    {
        var tid = await SeedTournamentAsync("p-withdraw-m");
        await SeedMatchAsync(tid, round: 2, p1: "p-withdraw-m", p2: "p-other", status: "pending");
        await SeedMatchAsync(tid, round: 3, p1: "p-other2", p2: "p-withdraw-m", status: "in-progress");
        await SeedMatchAsync(tid, round: 1, p1: "p-withdraw-m", p2: "p-finished", status: "complete");

        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.WithdrawPlayer(tid,
            new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p-withdraw-m" }, CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.TournamentMatches
            .Where(m => m.TournamentId == tid && (m.Player1Id == "p-withdraw-m" || m.Player2Id == "p-withdraw-m"))
            .ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("complete", remaining[0].Status);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_StampsAuditRow()
    {
        var tid = await SeedTournamentAsync("p-audit");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.WithdrawPlayer(tid,
            new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p-audit" }, CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.ReconnectAuditEntries
            .Where(a => a.Kind == ReconnectAuditEntry.KindTournamentPlayerWithdrawn)
            .ToListAsync();
        Assert.NotEmpty(audits);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_AlreadyWithdrawn_Returns409()
    {
        var tid = await SeedTournamentAsync("p-double", seed: -1);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(tid,
            new TournamentWithdrawPlayerController.WithdrawRequest { PlayerId = "p-double" }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_BodyReasonTooLong_Returns400()
    {
        var tid = await SeedTournamentAsync("p-1");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.WithdrawPlayer(tid, new TournamentWithdrawPlayerController.WithdrawRequest
        {
            PlayerId = "p-1",
            Reason = new string('y', TournamentWithdrawPlayerController.MaxReasonBodyLength + 1),
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_Sentinel_IsNegative()
    {
        Assert.True(TournamentWithdrawPlayerController.WithdrawnSeedSentinel < 0);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Withdraw_ErrorConstants_AreStable()
    {
        Assert.Equal("tournament-not-found", TournamentWithdrawPlayerController.ErrorTournamentNotFound);
        Assert.Equal("player-not-registered", TournamentWithdrawPlayerController.ErrorPlayerNotRegistered);
        Assert.Equal("already-withdrawn", TournamentWithdrawPlayerController.ErrorAlreadyWithdrawn);
    }
}
