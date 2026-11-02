using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// GOLDEN — multi-hand rotation + game-length correctness across configured
/// <see cref="ChangshaGameState.MaxHands"/> values (Lead follow-up on #117:
/// validate 1 / 8 / 16 / 32, not just the default 4).
///
/// <para>Drives <see cref="ChangshaGameStateMachine.RotateBanker"/> +
/// <see cref="ChangshaGameStateMachine.Score"/> deterministically (forced winners /
/// washouts, no bots) so the assertions are exact, and pins:</para>
/// <list type="bullet">
///   <item><b>Game length:</b> for MaxHands ≤ 16 the game completes after exactly
///         <c>MaxHands</c> hands; for MaxHands &gt; 16 it clamps to the canonical
///         16-hand ceiling (spec §6.3, 4 rounds × 4 hands) via the legacy round
///         terminal in <c>RotateBanker</c> — see the surfaced note on the &gt;16 test.</item>
///   <item><b>Dealer rotation (§6.2):</b> the winner becomes the next dealer; a
///         washout (no winner) retains the current dealer.</item>
///   <item><b>Scoring:</b> the spec-pure §5.1 payout stays zero-sum after every hand
///         as the deal and dealer rotate.</item>
/// </list>
/// </summary>
public class MaxHandsRotationGoldenTests
{
    private const int Budget = 64; // safety cap on hands the harness will drive.

    private sealed record RotationResult(
        int HandsPlayed,
        List<int> DealerAfterEachHand,
        int FinalHandNumber,
        ChangshaPhase FinalPhase,
        bool IsComplete,
        List<int> CumulativeScores);

    // ── 1. MaxHands ≤ 16 → completes at exactly MaxHands ──────────────────────────

    [Theory, Trait("Category", "Changsha"), Trait("Wave", "117-MaxHands")]
    [InlineData(1)]
    [InlineData(4)]   // default
    [InlineData(8)]
    [InlineData(16)]
    public void GameCompletesAtExactlyMaxHands_ForValuesUpTo16(int maxHands)
    {
        // Rotate the winner every hand so the dealer genuinely moves each hand.
        var result = DriveDeterministicGame(maxHands, (hand, _) => (hand + 1) % 4);

        Assert.Equal(maxHands, result.HandsPlayed);
        Assert.Equal(ChangshaPhase.GameComplete, result.FinalPhase);
        Assert.True(result.IsComplete, "IsGameComplete must be true at MaxHands.");
        // RotateBanker increments HandNumber past MaxHands to trigger completion.
        Assert.Equal(maxHands + 1, result.FinalHandNumber);
        // Dealer after the final hand is that hand's winner (§6.2).
        Assert.Equal(maxHands % 4, result.DealerAfterEachHand[^1]);
    }

    // ── 2. MaxHands > 16 → clamps to the canonical 16-hand ceiling (§6.3) ─────────

    [Theory, Trait("Category", "Changsha"), Trait("Wave", "117-MaxHands")]
    [InlineData(17)]
    [InlineData(24)]
    [InlineData(32)]
    public void MaxHandsAbove16_ClampsToCanonical16HandCeiling(int maxHands)
    {
        // SURFACED (Lead #117 follow-up): MaxHands values above 16 do NOT play the
        // requested number of hands. RotateBanker's legacy 4-round terminal
        // (RoundNumber > 4) fires at hand 16 and ends the game — by design per the
        // method's own docs ("the only way to reach this branch" is MaxHands > 16) and
        // consistent with spec §6.3 (a canonical game is 4 rounds × 4 hands = 16).
        // True >16-hand tournament games would require a rules-design decision
        // (deferred; see the rulePreset rules-engine issue). This test PINS the
        // current clamp so any change is deliberate and visible.
        var result = DriveDeterministicGame(maxHands, (hand, _) => (hand + 1) % 4);

        Assert.Equal(16, result.HandsPlayed);
        Assert.Equal(ChangshaPhase.GameComplete, result.FinalPhase);
        Assert.True(result.IsComplete);
    }

    // ── 3. Dealer rotation (§6.2): winner becomes dealer; washout retains ─────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "117-MaxHands")]
    public void DealerRotation_FollowsSpec62_WinnerBecomesDealer_And_WashoutRetains()
    {
        // Forced sequence over 8 hands (null = washout).
        int?[] winners = { 2, 3, null, 0, 0, null, 1, 2 };
        var result = DriveDeterministicGame(8, (hand, _) => winners[hand]);

        // Expected dealer after each hand:
        //  h0 win2→2, h1 win3→3, h2 washout→3, h3 win0→0, h4 win0→0(retained by winning),
        //  h5 washout→0, h6 win1→1, h7 win2→2.
        Assert.Equal(new[] { 2, 3, 3, 0, 0, 0, 1, 2 }, result.DealerAfterEachHand.ToArray());
        Assert.Equal(8, result.HandsPlayed);
        Assert.Equal(ChangshaPhase.GameComplete, result.FinalPhase);
    }

    // ── 4. Scoring stays zero-sum after every hand across rotation ────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "117-MaxHands")]
    public void Scoring_ZeroSum_HoldsAfterEveryHand_AndAccumulatesAcrossRotation()
    {
        // Seat 0 (the dealer) self-draws a Small Win every hand → dealer keeps the seat
        // (winner == dealer) and banks 2 from each of the 3 opponents per hand.
        var result = DriveDeterministicGame(16, (_, _) => 0);

        // DriveDeterministicGame asserts zero-sum inside the loop after each hand.
        Assert.Equal(0, result.CumulativeScores.Sum());
        // Frozen accumulation: 16 hands × (dealer Small-Win self-draw = 2 × 3 opponents).
        Assert.Equal(new[] { 96, -32, -32, -32 }, result.CumulativeScores.ToArray());
    }

    // ── Deterministic harness ─────────────────────────────────────────────────────

    /// <summary>
    /// Drives a game to completion, forcing the outcome of each hand via
    /// <paramref name="winnerForHand"/> (returns the winning seat, or <c>null</c> for a
    /// washout). Uses <see cref="ChangshaGameStateMachine.Score"/> (spec-pure default) so
    /// the payout is exercised, and asserts zero-sum after every hand.
    /// </summary>
    private static RotationResult DriveDeterministicGame(
        int maxHands, Func<int, int, int?> winnerForHand)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 4242, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.MaxHands = maxHands;

        var dealers = new List<int>();
        var handsPlayed = 0;

        while (state.Phase != ChangshaPhase.GameComplete && handsPlayed < Budget)
        {
            var dealerBefore = state.DealerSeatIndex;
            var winner = winnerForHand(handsPlayed, dealerBefore);

            if (winner is int w)
            {
                state.CurrentWin = new WinResult
                {
                    WinningSeatIndex = w,
                    Method = WinMethod.SelfDraw,
                    Pattern = WinPattern.Standard,
                    WinningTileId = 0,
                    SourceSeatIndex = w,
                };
                state.Phase = ChangshaPhase.Scoring;
                ChangshaGameStateMachine.Score(state); // spec-pure §5.1; Phase → EndHand.
            }
            else
            {
                // Washout: no winner, land straight in EndHand for RotateBanker.
                state.CurrentWin = null;
                state.Phase = ChangshaPhase.EndHand;
            }

            // Zero-sum invariant must hold after every scored/washout hand.
            Assert.Equal(0, state.CumulativeScores.Values.Sum());

            ChangshaGameStateMachine.RotateBanker(state);
            handsPlayed++;
            dealers.Add(state.DealerSeatIndex);
        }

        Assert.True(handsPlayed < Budget,
            $"Harness budget ({Budget}) exhausted without GameComplete for MaxHands={maxHands} " +
            $"(Phase={state.Phase}, HandNumber={state.HandNumber}, RoundNumber={state.RoundNumber}).");

        var scores = Enumerable.Range(0, 4).Select(i => state.CumulativeScores[i]).ToList();
        return new RotationResult(
            handsPlayed, dealers, state.HandNumber, state.Phase, state.IsGameComplete, scores);
    }
}
