using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TournamentEntity = Mahjong.Autotable.Api.Data.Entities.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Bishop;

/// <summary>
/// Phase K Wave 22 — Bishop. Tests for the W22 round timer
/// background service. Drives the service deterministically
/// via <see cref="RoundTimerService.RunOnceAsync"/> with a
/// mocked clock.
/// </summary>
[Collection("DbSerial")]
public sealed class RoundTimerServiceTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w22-round-timer-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;
    private DateTime _now = new(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);

    public RoundTimerServiceTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"timer-{Guid.NewGuid():N}.sqlite");
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

    private RoundTimerService MakeService(TournamentRoundAutoCloseMetrics? metrics = null)
    {
        return new RoundTimerService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RoundTimerService>.Instance,
            metrics,
            () => _now);
    }

    private async Task<Guid> SeedTournamentAsync()
    {
        var tid = Guid.NewGuid();
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tournaments.Add(new TournamentEntity
        {
            Id = tid, Name = "timer-t", Format = "swiss", Status = "in-progress",
        });
        await db.SaveChangesAsync();
        return tid;
    }

    private async Task<Guid> SeedMatchAsync(Guid tid, int round, int timeLimitMinutes, DateTime? startedAt, string status = "in-progress")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var m = new TournamentMatch
        {
            Id = Guid.NewGuid(),
            TournamentId = tid,
            Round = round,
            Player1Id = "p1",
            Player2Id = "p2",
            Status = status,
            TimeLimitMinutes = timeLimitMinutes,
            StartedAtUtc = startedAt,
        };
        db.TournamentMatches.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_EmptyDb_ReturnsZero()
    {
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_MatchPastTimeLimit_GetsClosed()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_MatchWithinTimeLimit_NotClosed()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 60, startedAt: _now.AddMinutes(-5));
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_MatchWithZeroLimit_NotClosed()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 0, startedAt: _now.AddHours(-5));
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_MatchWithNoStart_NotClosed()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: null);
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_AlreadyCompleteMatch_NotClosed()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1), status: "complete");
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_StampsCompletedAt()
    {
        var tid = await SeedTournamentAsync();
        var mid = await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        await MakeService().RunOnceAsync(CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var m = await db.TournamentMatches.AsNoTracking().FirstAsync(x => x.Id == mid);
        Assert.Equal(_now, m.CompletedAt);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_TimeoutLeavesWinnerNull()
    {
        var tid = await SeedTournamentAsync();
        var mid = await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        await MakeService().RunOnceAsync(CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var m = await db.TournamentMatches.AsNoTracking().FirstAsync(x => x.Id == mid);
        Assert.Null(m.WinnerPlayerId);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_IncrementsMetric()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        var metrics = new TournamentRoundAutoCloseMetrics();
        await MakeService(metrics).RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, metrics.Get(tid));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_BatchesAuditByRound()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, round: 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        await SeedMatchAsync(tid, round: 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        await MakeService().RunOnceAsync(CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ReconnectAuditEntries.AsNoTracking()
            .Where(e => e.Kind == ReconnectAuditEntry.KindTournamentRoundAutoClosed).ToListAsync();
        Assert.Single(rows);
        Assert.Contains("matches=2", rows[0].Detail ?? string.Empty);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_MultipleRounds_OneAuditEach()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, round: 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        await SeedMatchAsync(tid, round: 2, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        await MakeService().RunOnceAsync(CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ReconnectAuditEntries.AsNoTracking()
            .Where(e => e.Kind == ReconnectAuditEntry.KindTournamentRoundAutoClosed).ToListAsync();
        Assert.Equal(2, rows.Count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_ExactBoundary_NotClosed()
    {
        // Boundary semantics — match exactly at limit is NOT past.
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddMinutes(-30));
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_OneMillisecondPast_Closed()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddMinutes(-30).AddMilliseconds(-1));
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_IsIdempotent_SecondCallReturnsZero()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        var svc = MakeService();
        var first = await svc.RunOnceAsync(CancellationToken.None);
        var second = await svc.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_NegativeLimitTreatedAsZero()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: -10, startedAt: _now.AddHours(-5));
        var n = await MakeService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_MultipleTournaments_Independent()
    {
        var ta = await SeedTournamentAsync();
        var tb = await SeedTournamentAsync();
        await SeedMatchAsync(ta, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        await SeedMatchAsync(tb, 1, timeLimitMinutes: 30, startedAt: _now.AddHours(-1));
        var metrics = new TournamentRoundAutoCloseMetrics();
        await MakeService(metrics).RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, metrics.Get(ta));
        Assert.Equal(1, metrics.Get(tb));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Service_TickIntervalIsConfigurable()
    {
        var svc = MakeService();
        svc.TickIntervalSeconds = 5;
        Assert.Equal(5, svc.TickIntervalSeconds);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Metrics_AddNegative_NoOp()
    {
        var metrics = new TournamentRoundAutoCloseMetrics();
        metrics.Add(Guid.NewGuid(), -1);
        Assert.Empty(metrics.Snapshot());
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Metrics_PrometheusContainsCounter()
    {
        var metrics = new TournamentRoundAutoCloseMetrics();
        metrics.Add(Guid.Parse("11111111-1111-1111-1111-111111111111"), 3);
        var sb = new System.Text.StringBuilder();
        metrics.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains(TournamentRoundAutoCloseMetrics.MetricName, text);
        Assert.Contains("} 3", text);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Metrics_SameTournament_Accumulates()
    {
        var metrics = new TournamentRoundAutoCloseMetrics();
        var tid = Guid.NewGuid();
        metrics.Add(tid, 1);
        metrics.Add(tid, 2);
        Assert.Equal(3, metrics.Get(tid));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task RunOnce_MovesClockForward_NewlyExpiredMatchClosed()
    {
        var tid = await SeedTournamentAsync();
        await SeedMatchAsync(tid, 1, timeLimitMinutes: 30, startedAt: _now.AddMinutes(-10));
        var svc = MakeService();
        var first = await svc.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, first);
        _now = _now.AddMinutes(25);
        var second = await svc.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, second);
    }
}
