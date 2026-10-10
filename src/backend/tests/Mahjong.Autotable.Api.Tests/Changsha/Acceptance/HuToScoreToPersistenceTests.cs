using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Frost — Wave-K scoring-audit end-to-end. Pins the contract:
/// <c>real Hu → FanCalculator → ScoreResult → PlayerStats row</c>. Drives the
/// pure-functional <see cref="ChangshaGameStateMachine"/> through a guaranteed
/// 7-pair self-draw Hu, asserts <see cref="ChangshaGameStateMachine.Score"/>
/// emits the expected fan breakdown, then forwards the cumulative-score
/// projection to <see cref="PlayerProfileService.RecordGameCompletedAsync"/>
/// (mirroring what <see cref="Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime"/>
/// does at game completion) and confirms the persisted
/// <see cref="PlayerStats"/> row carries the wins counter, total score, and
/// non-null <c>LastGameAt</c> stamp.
///
/// <para>The test uses HUMAN player ids (not <c>bot-N</c>) so the
/// <see cref="PlayerProfileService"/> bots-filter does NOT swallow the row —
/// this is what surfaces the persistence regression Drake's
/// <c>LastGameAt</c>-nullable migration is supposed to gate against.</para>
///
/// <para>Lane: this file lives under <c>Changsha/Acceptance/</c> (Frost's
/// scoring tests) and reads PlayerStats via an isolated SQLite
/// <see cref="AppDbContext"/> built in-test. It does NOT mutate any
/// persistence code (Drake's lane); it asserts the contract Drake's row
/// promises to satisfy.</para>
/// </summary>
[Collection("DbSerial")]
public class HuToScoreToPersistenceTests : IAsyncLifetime
{
    private string _sqlitePath = string.Empty;
    private ServiceProvider _sp = null!;
    private PlayerProfileService _profiles = null!;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _sqlitePath = Path.Combine(dataDir, $"frost-hu-persistence-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_sqlitePath}"));
        _sp = services.BuildServiceProvider();

        // EnsureCreated builds the schema from the model — PlayerProfile +
        // PlayerStats tables are all we need for the assertions below.
        using var bootstrapScope = _sp.CreateScope();
        var db = bootstrapScope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        _profiles = new PlayerProfileService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PlayerProfileService>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); } catch { }
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. 7-pair self-draw Hu — full Score pipeline + persistence
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringAudit"), Trait("Wave", "K-Frost")]
    public async Task SevenPairsSelfDrawHu_ScoresAndPersistsWinnerStats()
    {
        // Build a real post-deal state, then surgically swap the dealer's
        // hand with a 14-tile 7-pair shape. SevenPairs is structurally
        // unambiguous (no chow ambiguity), guaranteeing the detector returns
        // IsWin = true.
        var state = BuildPostDealState(dealerSeat: 0);
        SuppressFirstDiscardContext(state);

        // 7 distinct pairs: 1万1万 / 4万4万 / 2筒2筒 / 6筒6筒 / 3条3条 / 7条7条 / 9条9条.
        // Picked so no three identical tiles overlap (which would let the
        // detector reorganise into a pung-bearing Standard pattern and lose
        // the SevenPairs tag).
        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Tong, 2), (Suit.Tong, 2),
            (Suit.Tong, 6), (Suit.Tong, 6),
            (Suit.Tiao, 3), (Suit.Tiao, 3),
            (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 9), (Suit.Tiao, 9));
        ClearOtherHands(state, keepSeat: 0);

        // Use human ids so PlayerProfileService.RecordGameCompletedAsync
        // (which skips "bot-" prefixed ids) actually persists rows. The
        // factory in BuildPostDealState seeded all four seats as bots — we
        // re-stamp them here.
        for (var i = 0; i < 4; i++) state.Seats[i].PlayerId = $"frost-audit-seat-{i}";

        // Drive the actual state machine commands — no synthetic events.
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);
        Assert.Equal(ChangshaPhase.Scoring, state.Phase);
        Assert.NotNull(state.CurrentWin);
        Assert.True(state.CurrentWin!.IsSelfDraw);

        ChangshaGameStateMachine.Score(state);
        Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        Assert.NotNull(state.CurrentScore);

        var score = state.CurrentScore!;

        // ── Fan / score assertions ────────────────────────────────────
        // SevenPairs is a Big Win (BigWinSelfDrawDealer = 4, dealer involved
        // since seat 0 is dealer, × 3 opponents = 12 base). Fans:
        //   SelfDraw (1) + SevenPairs (4) + ConcealedHand (1) = 6 per payment
        //   × 3 payments = 18 fan bonus → BasePoints = 12 + 18 = 30.
        Assert.Equal(ScoreCategory.BigWin, score.Category);
        Assert.Contains(score.Fans, f => f.Fan == Fan.SelfDraw);
        Assert.Contains(score.Fans, f => f.Fan == Fan.SevenPairs);
        Assert.Contains(score.Fans, f => f.Fan == Fan.ConcealedHand);
        Assert.Equal(1 + 4 + 1, score.FanPoints);

        // Non-zero by construction — the user's audit asks for "FanCalculator
        // returns a non-zero score".
        Assert.True(score.BasePoints > 0,
            $"BasePoints must be positive after a real Hu. Got {score.BasePoints}.");
        Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);

        // Zero-sum across the four seats (per spec §5).
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        // Winner banked positive score.
        var winnerScore = state.CumulativeScores[0];
        Assert.True(winnerScore > 0,
            $"Winner's cumulative score must be > 0. Got {winnerScore}.");

        // ── Persistence flow — simulate the runtime hook ──────────────
        // Project per-seat CumulativeScores → per-PlayerId scores + the
        // winners set, exactly mirroring ChangshaGameRuntime.EmitGameCompletedAsync.
        var (finalScores, winners) = ProjectFinalScores(state);
        Assert.Equal(4, finalScores.Count);
        Assert.Single(winners);
        Assert.Contains("frost-audit-seat-0", winners);

        await _profiles.RecordGameCompletedAsync(finalScores, winners);

        // ── PlayerStats row assertions (Drake's contract) ─────────────
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var allStats = await db.PlayerStats.ToListAsync();

        // All four human seats got a PlayerStats row (the service auto-
        // creates on first-game).
        Assert.Equal(4, allStats.Count);
        foreach (var seatIdx in new[] { 0, 1, 2, 3 })
        {
            var pid = $"frost-audit-seat-{seatIdx}";
            var s = allStats.Single(x => x.PlayerId == pid);
            Assert.Equal(1, s.GamesPlayed);
            Assert.NotNull(s.LastGameAt); // Drake's nullable-fix gate.
            Assert.True(s.LastGameAt.HasValue && s.LastGameAt.Value > DateTime.UtcNow.AddMinutes(-5),
                $"LastGameAt must be a fresh UTC stamp; got {s.LastGameAt}.");
        }

        // Winner-only counters: Wins incremented exactly once, others zero.
        var winnerStats = allStats.Single(x => x.PlayerId == "frost-audit-seat-0");
        Assert.Equal(1, winnerStats.GamesWon);
        Assert.Equal(1, winnerStats.CurrentWinStreak);
        Assert.Equal(1, winnerStats.LongestWinStreak);
        Assert.Equal(winnerScore, winnerStats.TotalScore);
        Assert.Equal(winnerScore, winnerStats.HighestSingleGameScore);

        foreach (var seatIdx in new[] { 1, 2, 3 })
        {
            var loser = allStats.Single(x => x.PlayerId == $"frost-audit-seat-{seatIdx}");
            Assert.Equal(0, loser.GamesWon);
            Assert.Equal(0, loser.CurrentWinStreak);
            Assert.Equal(0, loser.LongestWinStreak);
            Assert.Equal(state.CumulativeScores[seatIdx], loser.TotalScore);
        }

        // PlayerProfiles auto-created so the FK resolves (runtime guarantee).
        var profileCount = await db.PlayerProfiles.CountAsync();
        Assert.Equal(4, profileCount);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Discard Hu — fan path differs (no SelfDraw fan), persistence holds
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringAudit"), Trait("Wave", "K-Frost")]
    public async Task DiscardHu_ScoresAndPersistsWinnerStats()
    {
        // Non-dealer seat 1 wins by claiming a discard from dealer seat 0
        // via a 7-pair shape. SevenPairs sidesteps the 258 pair rule and
        // is structurally unambiguous (no chow / pung reorganisation that
        // could leave it un-detected). Seat 1 holds 13 tiles = 6 complete
        // pairs + 1 single waiting on its mate.
        var state = BuildPostDealState(dealerSeat: 0);
        SuppressFirstDiscardContext(state);

        // Seat 1: 6 distinct pairs + Wan-1 single (= 13 tiles). Dealer's
        // discard of Wan-1 completes the 7th pair.
        var seat1Tiles = Tiles(
            (Suit.Wan, 1),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Tong, 2), (Suit.Tong, 2),
            (Suit.Tong, 6), (Suit.Tong, 6),
            (Suit.Tiao, 3), (Suit.Tiao, 3),
            (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 9), (Suit.Tiao, 9));
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].ConcealedTiles.AddRange(seat1Tiles);
        state.Hands[1].Melds.Clear();

        // Strip Wan-1 from elsewhere so the only path to a Wan-1 in the
        // claim window comes from the dealer's discard we plant below.
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 1));
        ClearOtherHands(state, keepSeat: 1);
        // ClearOtherHands cleared seat 1's hand too if keepSeat differed —
        // re-add the seat 1 tiles after the strip (Wan-1 single survives;
        // the other pairs were never stripped since their logicals weren't
        // touched).
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].ConcealedTiles.AddRange(seat1Tiles);

        // Plant a Wan-1 in seat 0 (the dealer) plus 12 benign tiles so the
        // dealer has a 13-tile hand and can legally discard.
        var seat0Tiles = new List<int>
        {
            Tid(Suit.Wan, 1, 1),  // copy 1 so it doesn't collide with seat 1's copy 0
            Tid(Suit.Wan, 9, 0), Tid(Suit.Tong, 9, 0), Tid(Suit.Tiao, 1, 0),
            Tid(Suit.Wan, 8, 0), Tid(Suit.Tong, 8, 0), Tid(Suit.Tiao, 1, 1),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Tong, 7, 0), Tid(Suit.Tiao, 1, 2),
            Tid(Suit.Wan, 6, 0), Tid(Suit.Tong, 5, 0), Tid(Suit.Tiao, 4, 0),
        };
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].ConcealedTiles.AddRange(seat0Tiles);
        state.Hands[0].Melds.Clear();

        // Use human ids so persistence hook fires.
        for (var i = 0; i < 4; i++) state.Seats[i].PlayerId = $"frost-discard-seat-{i}";

        state.ActiveSeatIndex = 0;
        state.Phase = ChangshaPhase.AwaitingDiscard;

        var wan1Id = Tid(Suit.Wan, 1, 1);
        ChangshaGameStateMachine.Discard(state, 0, wan1Id);

        // Seat 1 claims Hu on the discarded Wan-1.
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.NotNull(state.ClaimWindow);

        ChangshaGameStateMachine.ResolveClaim(state, 1, Tables.TableClaimType.Hu);
        Assert.Equal(ChangshaPhase.Scoring, state.Phase);
        Assert.False(state.CurrentWin!.IsSelfDraw);
        Assert.Equal(0, state.CurrentWin.SourceSeatIndex);

        ChangshaGameStateMachine.Score(state);
        Assert.Equal(ChangshaPhase.EndHand, state.Phase);

        var score = state.CurrentScore!;
        // SevenPairs is a BigWin. Discard with dealer involved → BigWinDiscardDealer
        // = 7 (single discarder→winner payment). Fans: SevenPairs (4) + ConcealedHand
        // (1) = 5 fan points × 1 base payment = 5 fan bonus → BasePoints = 7 + 5 = 12.
        // No SelfDraw fan (discard Hu).
        Assert.Equal(ScoreCategory.BigWin, score.Category);
        Assert.Contains(score.Fans, f => f.Fan == Fan.SevenPairs);
        Assert.Contains(score.Fans, f => f.Fan == Fan.ConcealedHand);
        Assert.DoesNotContain(score.Fans, f => f.Fan == Fan.SelfDraw);
        Assert.True(score.BasePoints > 0,
            $"Discard Hu BasePoints must be positive. Got {score.BasePoints}.");
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        // Winner = seat 1.
        var winnerScore = state.CumulativeScores[1];
        Assert.True(winnerScore > 0);

        var (finalScores, winners) = ProjectFinalScores(state);
        Assert.Contains("frost-discard-seat-1", winners);

        await _profiles.RecordGameCompletedAsync(finalScores, winners);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var winnerStats = await db.PlayerStats.SingleAsync(s => s.PlayerId == "frost-discard-seat-1");
        Assert.Equal(1, winnerStats.GamesPlayed);
        Assert.Equal(1, winnerStats.GamesWon);
        Assert.NotNull(winnerStats.LastGameAt);
        Assert.Equal(winnerScore, winnerStats.TotalScore);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Multi-game streak — Wins / LongestWinStreak monotonicity
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringAudit"), Trait("Wave", "K-Frost")]
    public async Task RepeatedHu_AccumulatesWinsAndStreak()
    {
        // Drive 3 independent self-draw Hu's by the same human id. Confirms
        // PlayerStats counters are additive — GamesPlayed=3, GamesWon=3,
        // LongestWinStreak=3, CurrentWinStreak=3.
        var pid = "frost-streak-player";
        const int gameCount = 3;

        for (var iter = 0; iter < gameCount; iter++)
        {
            var state = BuildPostDealState(dealerSeat: 0);
            SuppressFirstDiscardContext(state);
            OverrideConcealedWith14(state, seatIndex: 0,
                (Suit.Wan, 1), (Suit.Wan, 1),
                (Suit.Wan, 4), (Suit.Wan, 4),
                (Suit.Tong, 2), (Suit.Tong, 2),
                (Suit.Tong, 6), (Suit.Tong, 6),
                (Suit.Tiao, 3), (Suit.Tiao, 3),
                (Suit.Tiao, 7), (Suit.Tiao, 7),
                (Suit.Tiao, 9), (Suit.Tiao, 9));
            ClearOtherHands(state, keepSeat: 0);
            state.Seats[0].PlayerId = pid;
            for (var i = 1; i < 4; i++) state.Seats[i].PlayerId = $"frost-streak-opponent-{iter}-{i}";

            ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);
            ChangshaGameStateMachine.Score(state);

            var (finalScores, winners) = ProjectFinalScores(state);
            await _profiles.RecordGameCompletedAsync(finalScores, winners);
        }

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stats = await db.PlayerStats.SingleAsync(s => s.PlayerId == pid);
        Assert.Equal(gameCount, stats.GamesPlayed);
        Assert.Equal(gameCount, stats.GamesWon);
        Assert.Equal(gameCount, stats.LongestWinStreak);
        Assert.Equal(gameCount, stats.CurrentWinStreak);
        Assert.NotNull(stats.LastGameAt);
        Assert.True(stats.TotalScore > 0);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Scenario builders (mirror FanCatalogIntegrationTests so the audit
    //  doesn't drift from the integration suite's contract)
    // ────────────────────────────────────────────────────────────────────

    private static ChangshaGameState BuildPostDealState(int dealerSeat)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 42, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        state.ActiveSeatIndex = dealerSeat;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.MissedWinSeats.Clear();
        state.DiscardPile.Clear();
        state.TurnNumber = 1;
        return state;
    }

    private static void SuppressFirstDiscardContext(ChangshaGameState state)
    {
        // Plant a benign prior discard so HeavenlyHand (dealer's first-action
        // gate) doesn't fire and inflate the fan tally.
        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = (state.DealerSeatIndex + 1) % 4,
            TileId = Tid(Suit.Tiao, 8, 0),
            TurnNumber = 1
        });
    }

    private static void OverrideConcealedWith14(ChangshaGameState state, int seatIndex,
        params (Suit suit, int rank)[] tiles)
    {
        var copies = new Dictionary<int, int>();
        var tileIds = new List<int>(tiles.Length);
        foreach (var (s, r) in tiles)
        {
            var logical = Logical(s, r);
            copies.TryGetValue(logical, out var copy);
            tileIds.Add(Tid(s, r, copy));
            copies[logical] = copy + 1;
        }
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    private static void ClearOtherHands(ChangshaGameState state, int keepSeat)
    {
        for (var i = 0; i < 4; i++)
        {
            if (i == keepSeat) continue;
            state.Hands[i].ConcealedTiles.Clear();
            state.Hands[i].Melds.Clear();
        }
    }

    /// <summary>
    /// Mirrors <c>ChangshaGameRuntime.EmitGameCompletedAsync</c>'s projection:
    /// per-seat CumulativeScores → per-PlayerId scores, plus the set of
    /// PlayerIds tied at the top score. Kept verbatim so the audit pins the
    /// same contract that runtime callers actually rely on.
    /// </summary>
    private static (Dictionary<string, int> FinalScores, HashSet<string> Winners)
        ProjectFinalScores(ChangshaGameState state)
    {
        var topScore = state.CumulativeScores.Values.Max();
        var finalScores = new Dictionary<string, int>(StringComparer.Ordinal);
        var winners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seat in state.Seats)
        {
            if (string.IsNullOrEmpty(seat.PlayerId)) continue;
            if (!state.CumulativeScores.TryGetValue(seat.SeatIndex, out var score)) continue;
            finalScores[seat.PlayerId] = finalScores.GetValueOrDefault(seat.PlayerId) + score;
            if (score == topScore) winners.Add(seat.PlayerId);
        }
        return (finalScores, winners);
    }
}
