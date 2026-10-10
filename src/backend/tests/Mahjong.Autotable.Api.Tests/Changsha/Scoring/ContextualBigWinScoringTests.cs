using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;
using static Mahjong.Autotable.Api.Tests.Changsha.Acceptance.AcceptanceFixture;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Scoring;

/// <summary>
/// Issue #157 — Contextual Changsha Big Wins must score as <see cref="ScoreCategory.BigWin"/>
/// even when the underlying hand is a plain Standard (258-pair) structure.
///
/// <para>Root cause: <c>ScoringService.ClassifyWin(WinResult)</c> classified the win from
/// <see cref="WinResult.Pattern"/> alone; the six spec §4.2.2 contextual Big Wins
/// (海底捞月 / 河底捞鱼 / 天和 / 地和 / 杠上开花 / 抢杠胡) fell through to
/// <see cref="ScoreCategory.SmallWin"/> on Standard shapes, so payments underpaid
/// (self-draw 1/2 instead of 3/4; discard 1/2 instead of 6/7).</para>
///
/// <para>These cases drive the <b>live default (spec-pure) Score path</b> —
/// <see cref="ChangshaGameStateMachine.Score"/> → <c>ScoringService.CalculateScore</c> —
/// exactly as the runtime does, and assert <see cref="ScoreResult.Category"/>, base
/// points, exact per-seat cumulative deltas, zero-sum, and that
/// <see cref="WinResult.AllPatterns"/> survive the boundary. Dealer is seat 0.</para>
/// </summary>
public class ContextualBigWinScoringTests
{
    public sealed record ContextualCase(
        string Title,
        WinMethod Method,
        WinPattern Pattern,
        WinPattern[] AllPatterns,
        bool IsRobbedKong,
        int DealerSeat,
        int WinnerSeat,
        int SourceSeat,
        int[] ExpectedDeltas,
        int ExpectedBasePoints,
        ScoreCategory ExpectedCategory);

    public static TheoryData<ContextualCase> Cases() => new()
    {
        // ── The six spec §4.2.2 contextual Big Wins on a Standard shape ──────────

        // 天和 (Heavenly Hand): dealer self-draws the initial 14-tile hand.
        // Big Win self-draw, dealer winner → each opponent pays 4 → banks 12.
        new ContextualCase("天和 HeavenlyHand — dealer self-draw",
            WinMethod.SelfDraw, WinPattern.HeavenlyHand, new[] { WinPattern.HeavenlyHand },
            IsRobbedKong: false, DealerSeat: 0, WinnerSeat: 0, SourceSeat: 0,
            ExpectedDeltas: new[] { +12, -4, -4, -4 }, ExpectedBasePoints: 12, ScoreCategory.BigWin),

        // 地和 (Earthly Hand): non-dealer Hus the dealer's first discard.
        // Big Win discard, dealer is the payer → dealer pays 7.
        new ContextualCase("地和 EarthlyHand — non-dealer Hu on dealer's first discard",
            WinMethod.Discard, WinPattern.EarthlyHand, new[] { WinPattern.EarthlyHand },
            IsRobbedKong: false, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 0,
            ExpectedDeltas: new[] { -7, +7, 0, 0 }, ExpectedBasePoints: 7, ScoreCategory.BigWin),

        // 海底捞月 (Last Tile from Wall): non-dealer self-draw on the last wall tile.
        // Big Win self-draw, non-dealer winner → dealer pays 4, others 3 → banks 10.
        new ContextualCase("海底捞月 LastTileFromWall — non-dealer self-draw",
            WinMethod.SelfDraw, WinPattern.LastTileFromWall, new[] { WinPattern.LastTileFromWall },
            IsRobbedKong: false, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 1,
            ExpectedDeltas: new[] { -4, +10, -3, -3 }, ExpectedBasePoints: 10, ScoreCategory.BigWin),

        // 河底捞鱼 (Last Discard Catch): non-dealer Hus a non-dealer's final discard.
        // Big Win discard, non-dealer → non-dealer → discarder pays 6.
        new ContextualCase("河底捞鱼 LastDiscardCatch — non-dealer Hu on final discard",
            WinMethod.Discard, WinPattern.LastDiscardCatch, new[] { WinPattern.LastDiscardCatch },
            IsRobbedKong: false, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 2,
            ExpectedDeltas: new[] { 0, +6, -6, 0 }, ExpectedBasePoints: 6, ScoreCategory.BigWin),

        // 杠上开花 (Kong Replacement Win): non-dealer self-draw on a kong-replacement tile.
        // Big Win self-draw, non-dealer winner → dealer pays 4, others 3 → banks 10.
        new ContextualCase("杠上开花 KongReplacementWin — non-dealer self-draw",
            WinMethod.SelfDraw, WinPattern.KongReplacementWin, new[] { WinPattern.KongReplacementWin },
            IsRobbedKong: false, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 1,
            ExpectedDeltas: new[] { -4, +10, -3, -3 }, ExpectedBasePoints: 10, ScoreCategory.BigWin),

        // 抢杠胡 (Robbing the Added Kong): carried on IsRobbedKong, NOT a WinPattern.
        // Non-dealer robs a non-dealer's added kong on a Standard shape → discard-style
        // payment, non-dealer → non-dealer → 6.
        new ContextualCase("抢杠胡 RobbingKong — non-dealer robs non-dealer (Standard shape)",
            WinMethod.RobbingKong, WinPattern.Standard, System.Array.Empty<WinPattern>(),
            IsRobbedKong: true, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 2,
            ExpectedDeltas: new[] { 0, +6, -6, 0 }, ExpectedBasePoints: 6, ScoreCategory.BigWin),

        // 抢杠胡 dealer-winner variant: dealer robs a non-dealer's added kong → dealer
        // as winner bonus → 7.
        new ContextualCase("抢杠胡 RobbingKong — dealer robs non-dealer (Standard shape)",
            WinMethod.RobbingKong, WinPattern.Standard, System.Array.Empty<WinPattern>(),
            IsRobbedKong: true, DealerSeat: 0, WinnerSeat: 0, SourceSeat: 2,
            ExpectedDeltas: new[] { +7, 0, -7, 0 }, ExpectedBasePoints: 7, ScoreCategory.BigWin),

        // ── Combinations with structural Big Wins (no double-count / no regression) ──

        // FullFlush (structural Big Win) + LastTileFromWall (contextual). Spec-pure:
        // no stacking multiplier → identical to a single Big Win self-draw (banks 10).
        new ContextualCase("FullFlush + 海底捞月 — structural + contextual, non-dealer self-draw",
            WinMethod.SelfDraw, WinPattern.FullFlush,
            new[] { WinPattern.FullFlush, WinPattern.LastTileFromWall },
            IsRobbedKong: false, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 1,
            ExpectedDeltas: new[] { -4, +10, -3, -3 }, ExpectedBasePoints: 10, ScoreCategory.BigWin),

        // AllPungs (structural Big Win) + IsRobbedKong. Dealer is the robbed declarer →
        // dealer as payer → 7. Single discard-style payment, no double-count.
        new ContextualCase("AllPungs + 抢杠胡 — structural + robbed-kong flag, dealer declarer",
            WinMethod.RobbingKong, WinPattern.AllPungs, new[] { WinPattern.AllPungs },
            IsRobbedKong: true, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 0,
            ExpectedDeltas: new[] { -7, +7, 0, 0 }, ExpectedBasePoints: 7, ScoreCategory.BigWin),

        // ── Regression guard: a plain Standard win must stay a Small Win ──────────
        new ContextualCase("Standard (no context) — stays Small Win, non-dealer self-draw",
            WinMethod.SelfDraw, WinPattern.Standard, System.Array.Empty<WinPattern>(),
            IsRobbedKong: false, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 1,
            ExpectedDeltas: new[] { -2, +4, -1, -1 }, ExpectedBasePoints: 4, ScoreCategory.SmallWin),
    };

    [Theory, Trait("Category", "ScoringGolden"), Trait("Issue", "157")]
    [MemberData(nameof(Cases))]
    public void ContextualBigWin_ScoredThroughLiveSpecPurePath(ContextualCase c)
    {
        var state = BuildScorableState(c);

        // Live default = ChangshaScoringOptions.SpecPure (no fan folding, no stacking).
        ChangshaGameStateMachine.Score(state);

        var score = state.CurrentScore!;

        Assert.Equal(c.ExpectedCategory, score.Category);
        Assert.Equal(c.ExpectedBasePoints, score.BasePoints);

        // Exact per-seat cumulative deltas.
        for (var seat = 0; seat < 4; seat++)
        {
            Assert.Equal(c.ExpectedDeltas[seat], state.CumulativeScores[seat]);
        }

        // Winner banks exactly what the payers lost.
        var winnerTotal = c.ExpectedDeltas[c.WinnerSeat];
        var totalPaid = c.ExpectedDeltas.Where((_, seat) => seat != c.WinnerSeat).Sum(d => -d);
        Assert.Equal(winnerTotal, totalPaid);

        // Structural invariants: zero-sum + BasePoints == Σ Payments.Amount (no double-count).
        Assert.Equal(0, state.CumulativeScores.Values.Sum());
        Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);

        // Spec-pure never folds the read-only fan breakdown into the money.
        Assert.Equal(winnerTotal, score.BasePoints);

        // AllPatterns survive the detector → WinResult → scoring boundary unchanged.
        Assert.Equal(c.AllPatterns, state.CurrentWin!.AllPatterns.ToArray());
        Assert.Equal(c.IsRobbedKong, state.CurrentWin!.IsRobbedKong);
    }

    // ── Real-hand integration: the read-only Fan breakdown surfaces the contextual
    //    fan while spec-pure payments stay at the §5.1 Big-Win magnitude. ───────────

    [Fact, Trait("Category", "ScoringGolden"), Trait("Issue", "157")]
    public void HeavenlyHand_RealWinningHand_SurfacesFan_AndScoresBigWin()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 157);
        SetDealer(state, 0);

        // A complete Standard-shaped winning hand for the dealer (seat 0):
        // chows 123/456/789-Wan + 123-Tong + 55-Tong pair — no structural Big Win shape.
        var winningTile = Tid(Suit.Wan, 1, 0);
        var winning = ThirteenTileWaitingForWan1();
        winning.Add(winningTile);
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].ConcealedTiles.AddRange(winning);
        state.Hands[0].Melds.Clear();

        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 0,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.HeavenlyHand,
            WinningTileId = winningTile,
            SourceSeatIndex = 0,
            IsSelfDraw = true,
            AllPatterns = new[] { WinPattern.HeavenlyHand },
        };
        state.Phase = ChangshaPhase.Scoring;

        ChangshaGameStateMachine.Score(state);
        var score = state.CurrentScore!;

        Assert.Equal(ScoreCategory.BigWin, score.Category);
        Assert.Equal(12, score.BasePoints);
        Assert.Equal(new[] { +12, -4, -4, -4 },
            new[] { 0, 1, 2, 3 }.Select(s => state.CumulativeScores[s]).ToArray());
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        // The read-only fan breakdown names the contextual win …
        Assert.Contains(score.Fans, f => f.Fan == Fan.HeavenlyHand);
        // … but spec-pure keeps the money at the §5.1 Big-Win magnitude (not fan-folded).
        Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);
    }

    [Fact, Trait("Category", "ScoringGolden"), Trait("Issue", "157")]
    public void RobbingKong_RealWinningHand_SurfacesFan_AndScoresBigWin()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 158);
        SetDealer(state, 0);

        // Non-dealer (seat 1) wins on a Standard shape by robbing seat 0's (dealer's)
        // added kong. IsRobbedKong is the only Big-Win signal — no structural pattern.
        var winningTile = Tid(Suit.Wan, 1, 0);
        var winning = ThirteenTileWaitingForWan1();
        winning.Add(winningTile);
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].ConcealedTiles.AddRange(winning);
        state.Hands[1].Melds.Clear();

        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 1,
            Method = WinMethod.RobbingKong,
            Pattern = WinPattern.Standard,
            WinningTileId = winningTile,
            SourceSeatIndex = 0,
            IsRobbedKong = true,
            AllPatterns = System.Array.Empty<WinPattern>(),
        };
        state.Phase = ChangshaPhase.Scoring;

        ChangshaGameStateMachine.Score(state);
        var score = state.CurrentScore!;

        Assert.Equal(ScoreCategory.BigWin, score.Category);
        // Dealer is the robbed declarer/payer → Big-Win discard dealer bonus → 7.
        Assert.Equal(7, score.BasePoints);
        Assert.Equal(new[] { -7, +7, 0, 0 },
            new[] { 0, 1, 2, 3 }.Select(s => state.CumulativeScores[s]).ToArray());
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        Assert.Contains(score.Fans, f => f.Fan == Fan.RobbingKong);
        Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static ChangshaGameState BuildScorableState(ContextualCase c)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 157);
        SetDealer(state, c.DealerSeat);

        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = c.WinnerSeat,
            Method = c.Method,
            Pattern = c.Pattern,
            WinningTileId = 0,
            SourceSeatIndex = c.SourceSeat,
            IsFullFlush = c.Pattern == WinPattern.FullFlush,
            IsSelfDraw = c.Method == WinMethod.SelfDraw,
            IsRobbedKong = c.IsRobbedKong,
            AllPatterns = c.AllPatterns,
        };
        state.Phase = ChangshaPhase.Scoring;
        return state;
    }

    private static void SetDealer(ChangshaGameState state, int dealerSeat)
    {
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
    }
}
