using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.EntityFrameworkCore;
using TournamentEntity = Mahjong.Autotable.Api.Data.Entities.Tournament;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Tests for the live W20 Swiss
/// pairing service. Exercises the round-1 path (no completed
/// matches), withdrawn-player exclusion, tiebreaker selection
/// (single-Buchholz default, median-Buchholz at ≥5 rounds),
/// audit-row stamping, the unique-index round-already-paired
/// guard, and the wire-stable error codes.
/// </summary>
[Collection("DbSerial")]
public sealed class SwissPairingServiceTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w20-swiss-service-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SwissPairingServiceTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w20-swiss-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<ISwissPairingService, FideC04SwissPairingService>();
        services.AddSingleton<SwissPairingService>();
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
    private SwissPairingService NewService() => _sp.GetRequiredService<SwissPairingService>();

    private async Task<Guid> SeedTournamentAsync(int playerCount, string format = "swiss")
    {
        var tid = Guid.NewGuid();
        await using var db = NewDb();
        db.Tournaments.Add(new TournamentEntity
        {
            Id = tid,
            Name = "test-w20",
            Format = format,
            Status = "in-progress",
        });
        for (var i = 0; i < playerCount; i++)
        {
            db.TournamentRegistrations.Add(new TournamentRegistration
            {
                TournamentId = tid,
                PlayerId = $"player-{i:D2}",
                Seed = i,
            });
        }
        await db.SaveChangesAsync();
        return tid;
    }

    private async Task RecordMatchAsync(Guid tid, int round, string p1, string p2, string? winner)
    {
        await using var db = NewDb();
        db.TournamentMatches.Add(new TournamentMatch
        {
            TournamentId = tid,
            Round = round,
            Player1Id = p1,
            Player2Id = p2,
            WinnerPlayerId = winner,
            Status = "complete",
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_TournamentMissing_ReturnsNotFoundError()
    {
        var result = await NewService().PairNextRoundAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(SwissPairingService.ErrorTournamentNotFound, result.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_NonSwissFormat_ReturnsNotSwissError()
    {
        var tid = await SeedTournamentAsync(4, format: "round-robin");
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(SwissPairingService.ErrorNotSwissFormat, result.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_OnePlayer_ReturnsInsufficientError()
    {
        var tid = await SeedTournamentAsync(1);
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(SwissPairingService.ErrorInsufficientPlayers, result.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_FourPlayers_FirstRound_EmitsTwoBoards()
    {
        var tid = await SeedTournamentAsync(4);
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Pairings.Count);
        Assert.Equal(1, result.NextRound);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_FirstRound_UsesBuchholzTiebreaker()
    {
        var tid = await SeedTournamentAsync(4);
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(SwissPairingService.TiebreakerBuchholz, result.Tiebreaker);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_FirstRound_WritesAuditRows()
    {
        var tid = await SeedTournamentAsync(4);
        await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        await using var db = NewDb();
        var rows = await db.SwissPairingAuditEntries
            .Where(e => e.TournamentId == tid).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(SwissPairingService.TiebreakerBuchholz, r.Tiebreaker));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_FirstRound_WritesReconnectAuditRows()
    {
        var tid = await SeedTournamentAsync(4);
        await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        await using var db = NewDb();
        var rows = await db.ReconnectAuditEntries
            .Where(e => e.Kind == ReconnectAuditEntry.KindTournamentSwissPairingComputed).ToListAsync();
        Assert.Equal(2, rows.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_ReconnectAuditDetail_IncludesTournamentRoundBoardTiebreaker()
    {
        var tid = await SeedTournamentAsync(4);
        await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        await using var db = NewDb();
        var row = await db.ReconnectAuditEntries
            .FirstAsync(e => e.Kind == ReconnectAuditEntry.KindTournamentSwissPairingComputed);
        Assert.Contains("tournamentId=", row.Detail);
        Assert.Contains("round=", row.Detail);
        Assert.Contains("board=", row.Detail);
        Assert.Contains("tiebreaker=", row.Detail);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_ReinvocationIsRejected_RoundAlreadyPaired()
    {
        var tid = await SeedTournamentAsync(4);
        await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        // Reinvoking without completing a round attempts to write
        // the same (TournamentId, Round=1, Board) rows again.
        var result2 = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.False(result2.Succeeded);
        Assert.Equal(SwissPairingService.ErrorRoundAlreadyPaired, result2.ErrorCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_WithCompletedRound_AdvancesNextRoundCounter()
    {
        var tid = await SeedTournamentAsync(4);
        // Inject a completed round-1 match so the next round is 2.
        await RecordMatchAsync(tid, 1, "player-00", "player-01", "player-00");
        await RecordMatchAsync(tid, 1, "player-02", "player-03", "player-02");
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.NextRound);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_AfterFiveRounds_SwitchesToMedianBuchholz()
    {
        var tid = await SeedTournamentAsync(4);
        for (var r = 1; r <= 5; r++)
        {
            await RecordMatchAsync(tid, r, "player-00", "player-01", "player-00");
            await RecordMatchAsync(tid, r, "player-02", "player-03", "player-02");
        }
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(SwissPairingService.TiebreakerMedianBuchholz, result.Tiebreaker);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_OddPlayers_EmitsByeBoard()
    {
        var tid = await SeedTournamentAsync(3);
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Contains(result.Pairings, p => p.IsBye);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_WithdrawnPlayers_ExcludedFromPairing()
    {
        var tid = await SeedTournamentAsync(4);
        // Withdraw player-03 (Seed sentinel < 0).
        await using (var db = NewDb())
        {
            var reg = await db.TournamentRegistrations
                .FirstAsync(r => r.TournamentId == tid && r.PlayerId == "player-03");
            reg.Seed = -1;
            await db.SaveChangesAsync();
        }
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        // 3 remaining players → 1 pairing + 1 bye.
        Assert.Equal(2, result.Pairings.Count);
        Assert.DoesNotContain(result.Pairings,
            p => p.White == "player-03" || p.Black == "player-03");
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_BoardNumbering_StartsAt1AndIsContiguous()
    {
        var tid = await SeedTournamentAsync(6);
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        for (var i = 0; i < result.Pairings.Count; i++)
        {
            Assert.Equal(i + 1, result.Pairings[i].Board);
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_WhiteIsAssignedToHigherPriority()
    {
        var tid = await SeedTournamentAsync(4);
        // Give player-01 a win → match-points = 1 (higher than player-00 = 0).
        await RecordMatchAsync(tid, 1, "player-01", "player-00", "player-01");
        await RecordMatchAsync(tid, 1, "player-02", "player-03", "player-02");
        // For round 2, player-01 should be White when paired with a 0-point player.
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        // Verify that for any pairing including player-01 against a
        // lower-scoring opponent, player-01 is white.
        var p1Pair = result.Pairings.FirstOrDefault(p =>
            p.White == "player-01" || p.Black == "player-01");
        if (p1Pair is not null && !p1Pair.IsBye)
        {
            Assert.Equal("player-01", p1Pair.White);
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_Result_NoErrorFieldsOnSuccess()
    {
        var tid = await SeedTournamentAsync(4);
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.ErrorMessage);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_Result_TournamentIdEchoed()
    {
        var tid = await SeedTournamentAsync(4);
        var result = await NewService().PairNextRoundAsync(tid, CancellationToken.None);
        Assert.Equal(tid, result.TournamentId);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ComputeBuchholz_NoOpponents_ReturnsZero()
    {
        var opps = new Dictionary<string, List<string>>();
        var mp = new Dictionary<string, int>();
        var result = SwissPairingService.ComputeBuchholz("p1", opps, mp, SwissPairingService.TiebreakerBuchholz);
        Assert.Equal(0, result);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ComputeBuchholz_SumsOpponentMatchPoints()
    {
        var opps = new Dictionary<string, List<string>>
        {
            ["p1"] = new List<string> { "p2", "p3" },
        };
        var mp = new Dictionary<string, int> { ["p2"] = 1, ["p3"] = 2 };
        var result = SwissPairingService.ComputeBuchholz("p1", opps, mp, SwissPairingService.TiebreakerBuchholz);
        Assert.Equal(3, result);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ComputeBuchholz_MedianDropsExtremes()
    {
        var opps = new Dictionary<string, List<string>>
        {
            ["p1"] = new List<string> { "p2", "p3", "p4" },
        };
        var mp = new Dictionary<string, int> { ["p2"] = 0, ["p3"] = 2, ["p4"] = 4 };
        // Single-buchholz = 0+2+4 = 6.
        var single = SwissPairingService.ComputeBuchholz("p1", opps, mp, SwissPairingService.TiebreakerBuchholz);
        Assert.Equal(6, single);
        // Median-buchholz drops 0 (low) and 4 (high) → 2.
        var median = SwissPairingService.ComputeBuchholz("p1", opps, mp, SwissPairingService.TiebreakerMedianBuchholz);
        Assert.Equal(2, median);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ComputeBuchholz_MedianFallsBackWhenLessThan3Opponents()
    {
        var opps = new Dictionary<string, List<string>>
        {
            ["p1"] = new List<string> { "p2", "p3" },
        };
        var mp = new Dictionary<string, int> { ["p2"] = 1, ["p3"] = 2 };
        // Fewer than 3 opponents → median doesn't drop anything → sum.
        var median = SwissPairingService.ComputeBuchholz("p1", opps, mp, SwissPairingService.TiebreakerMedianBuchholz);
        Assert.Equal(3, median);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Service_Ctor_NullDeps_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SwissPairingService(
            null!, _sp.GetRequiredService<ISwissPairingService>(), NullLogger<SwissPairingService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SwissPairingService(
            _sp.GetRequiredService<IServiceScopeFactory>(), null!, NullLogger<SwissPairingService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SwissPairingService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            _sp.GetRequiredService<ISwissPairingService>(), null!));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void TiebreakerConstants_HaveStableWireNames()
    {
        Assert.Equal("buchholz", SwissPairingService.TiebreakerBuchholz);
        Assert.Equal("median-buchholz", SwissPairingService.TiebreakerMedianBuchholz);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ErrorConstants_HaveStableWireNames()
    {
        Assert.Equal("tournament-not-found", SwissPairingService.ErrorTournamentNotFound);
        Assert.Equal("tournament-not-swiss", SwissPairingService.ErrorNotSwissFormat);
        Assert.Equal("insufficient-players", SwissPairingService.ErrorInsufficientPlayers);
        Assert.Equal("pairing-engine-empty", SwissPairingService.ErrorPairingEngineEmpty);
        Assert.Equal("round-already-paired", SwissPairingService.ErrorRoundAlreadyPaired);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void MedianBuchholzThreshold_IsFive()
    {
        Assert.Equal(5, SwissPairingService.MedianBuchholzThreshold);
    }
}
