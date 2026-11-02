using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;

namespace Mahjong.Autotable.Api.Tests.Changsha.Scoring;

/// <summary>
/// FROZEN GOLDEN — Changsha spec §5.1 "V1 Worked Examples" (Examples 1-10).
///
/// <para>Issue #117: the live payment path (<see cref="ChangshaGameStateMachine.Score"/>)
/// had drifted above the binding §5.1 magnitudes because the Post-W23 fan layer folded
/// fan points into payments and the Big-Win stacking multiplier was applied. This golden
/// pins the binding-spec numbers through the <b>live default (spec-pure) Score path</b>
/// — the exact surface that regressed — so any future drift RED-fails here.</para>
///
/// <para>Each case asserts the per-seat cumulative deltas the winner banks and each
/// payer loses, plus the two structural invariants: zero-sum across the table and
/// <c>BasePoints == Σ Payments.Amount</c>. Dealer is seat 0 in every case.</para>
///
/// <para>The numbers are frozen from spec §5.1 (docs/rules/changsha-spec.md):
/// Small Win 1/2, Big Win self-draw 3/4 &amp; discard 6/7, +1 dealer bonus whenever the
/// dealer is winner or payer. No fan bonus, no stacking (spec-pure).</para>
/// </summary>
public class Section51GoldenTests
{
    public sealed record GoldenCase(
        int Example,
        string Title,
        WinMethod Method,
        WinPattern Pattern,
        int DealerSeat,
        int WinnerSeat,
        int SourceSeat,
        int[] ExpectedDeltas,
        ScoreCategory ExpectedCategory);

    public static TheoryData<GoldenCase> Examples() => new()
    {
        // ── Small Win ────────────────────────────────────────────────────────────
        // Ex1: Small Win self-draw, non-dealer winner. Dealer pays 2, others pay 1
        //      → winner banks 1 + 1 + 2 = 4.
        new GoldenCase(1, "Small Win self-draw (non-dealer winner)",
            WinMethod.SelfDraw, WinPattern.Standard, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 1,
            ExpectedDeltas: new[] { -2, +4, -1, -1 }, ScoreCategory.SmallWin),

        // Ex2: Small Win self-draw, dealer winner. Each opponent pays 2 → banks 6.
        new GoldenCase(2, "Small Win self-draw (dealer winner)",
            WinMethod.SelfDraw, WinPattern.Standard, DealerSeat: 0, WinnerSeat: 0, SourceSeat: 0,
            ExpectedDeltas: new[] { +6, -2, -2, -2 }, ScoreCategory.SmallWin),

        // Ex3: Small Win discard, non-dealer winner from non-dealer. Discarder pays 1.
        new GoldenCase(3, "Small Win discard (non-dealer winner, non-dealer discarder)",
            WinMethod.Discard, WinPattern.Standard, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 2,
            ExpectedDeltas: new[] { 0, +1, -1, 0 }, ScoreCategory.SmallWin),

        // Ex4: Small Win discard, non-dealer winner from dealer. Dealer pays 2.
        new GoldenCase(4, "Small Win discard (non-dealer winner, dealer discarder)",
            WinMethod.Discard, WinPattern.Standard, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 0,
            ExpectedDeltas: new[] { -2, +2, 0, 0 }, ScoreCategory.SmallWin),

        // Ex5: Small Win discard, dealer winner. Discarder pays 2 (dealer as winner).
        new GoldenCase(5, "Small Win discard (dealer winner)",
            WinMethod.Discard, WinPattern.Standard, DealerSeat: 0, WinnerSeat: 0, SourceSeat: 2,
            ExpectedDeltas: new[] { +2, 0, -2, 0 }, ScoreCategory.SmallWin),

        // ── Big Win ──────────────────────────────────────────────────────────────
        // Ex6: Big Win self-draw, non-dealer winner. Dealer pays 4, others 3 → banks 10.
        new GoldenCase(6, "Big Win self-draw (non-dealer winner)",
            WinMethod.SelfDraw, WinPattern.AllPungs, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 1,
            ExpectedDeltas: new[] { -4, +10, -3, -3 }, ScoreCategory.BigWin),

        // Ex7: Big Win self-draw, dealer winner. Each opponent pays 4 → banks 12.
        new GoldenCase(7, "Big Win self-draw (dealer winner)",
            WinMethod.SelfDraw, WinPattern.SevenPairs, DealerSeat: 0, WinnerSeat: 0, SourceSeat: 0,
            ExpectedDeltas: new[] { +12, -4, -4, -4 }, ScoreCategory.BigWin),

        // Ex8: Big Win discard, non-dealer winner from non-dealer. Discarder pays 6.
        new GoldenCase(8, "Big Win discard (non-dealer winner, non-dealer discarder)",
            WinMethod.Discard, WinPattern.FullFlush, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 2,
            ExpectedDeltas: new[] { 0, +6, -6, 0 }, ScoreCategory.BigWin),

        // Ex9: Big Win discard, non-dealer winner from dealer. Dealer pays 7.
        new GoldenCase(9, "Big Win discard (non-dealer winner, dealer discarder)",
            WinMethod.Discard, WinPattern.SevenPairs, DealerSeat: 0, WinnerSeat: 1, SourceSeat: 0,
            ExpectedDeltas: new[] { -7, +7, 0, 0 }, ScoreCategory.BigWin),

        // Ex10: Big Win discard, dealer winner. Discarder pays 7 (dealer as winner).
        new GoldenCase(10, "Big Win discard (dealer winner)",
            WinMethod.Discard, WinPattern.AllPungs, DealerSeat: 0, WinnerSeat: 0, SourceSeat: 2,
            ExpectedDeltas: new[] { +7, 0, -7, 0 }, ScoreCategory.BigWin),
    };

    [Theory, Trait("Category", "ScoringGolden"), Trait("Wave", "117-SpecReconciliation")]
    [MemberData(nameof(Examples))]
    public void Section51_Example_MatchesBindingSpec_ThroughLiveSpecPureScorePath(GoldenCase c)
    {
        var state = BuildScorableState(c.DealerSeat, c.WinnerSeat, c.SourceSeat, c.Method, c.Pattern);

        // Live default = ChangshaScoringOptions.SpecPure (no fan folding, no stacking).
        ChangshaGameStateMachine.Score(state);

        var score = state.CurrentScore!;
        Assert.Equal(c.ExpectedCategory, score.Category);

        // Per-seat cumulative deltas match the frozen §5.1 example exactly.
        for (var seat = 0; seat < 4; seat++)
        {
            Assert.Equal(c.ExpectedDeltas[seat], state.CumulativeScores[seat]);
        }

        // Winner banks the spec's "Total received"; equals Σ of what payers lost.
        var winnerTotal = c.ExpectedDeltas[c.WinnerSeat];
        var totalPaid = c.ExpectedDeltas.Where((_, seat) => seat != c.WinnerSeat).Sum(d => -d);
        Assert.Equal(winnerTotal, totalPaid);

        // Structural invariants (C-3): zero-sum + BasePoints == Σ Payments.Amount.
        Assert.Equal(0, state.CumulativeScores.Values.Sum());
        Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);

        // Spec-pure never folds fan points into the money: the winner's banked total
        // equals the base payout, never inflated by FanPoints even when fans are detected.
        Assert.Equal(winnerTotal, score.BasePoints);
    }

    /// <summary>
    /// Property: across every winner/discarder/method/pattern permutation the live
    /// spec-pure Score path stays zero-sum and keeps <c>BasePoints == Σ Payments.Amount</c>.
    /// </summary>
    [Fact, Trait("Category", "ScoringGolden"), Trait("Wave", "117-SpecReconciliation")]
    public void ZeroSum_And_BasePointsInvariant_HoldForAllConfigurations()
    {
        var patterns = new[] { WinPattern.Standard, WinPattern.AllPungs, WinPattern.SevenPairs, WinPattern.FullFlush };
        foreach (var method in new[] { WinMethod.SelfDraw, WinMethod.Discard })
        foreach (var pattern in patterns)
        foreach (var dealer in new[] { 0, 1 })
        foreach (var winner in new[] { 0, 1, 2, 3 })
        {
            // Self-draw: source == winner. Discard: pick a distinct discarder.
            var source = method == WinMethod.SelfDraw ? winner : (winner + 1) % 4;
            var state = BuildScorableState(dealer, winner, source, method, pattern);

            ChangshaGameStateMachine.Score(state);
            var score = state.CurrentScore!;

            Assert.Equal(0, state.CumulativeScores.Values.Sum());
            Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);
            Assert.True(score.BasePoints > 0,
                $"BasePoints must be positive (method={method}, pattern={pattern}, dealer={dealer}, winner={winner}).");
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────────
    // A minimal, fully-initialized state (Seats + Hands + CumulativeScores are seeded
    // by CreateGame) with CurrentWin planted and phase forced to Scoring. Hands are left
    // empty, so the fan detector returns no win → no fans fire; the spec-pure payment path
    // is exercised in isolation regardless of hand contents.
    private static ChangshaGameState BuildScorableState(
        int dealerSeat, int winnerSeat, int sourceSeat, WinMethod method, WinPattern pattern)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 117);
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;

        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = winnerSeat,
            Method = method,
            Pattern = pattern,
            WinningTileId = 0,
            SourceSeatIndex = sourceSeat,
            IsFullFlush = pattern == WinPattern.FullFlush,
        };
        state.Phase = ChangshaPhase.Scoring;
        return state;
    }
}
