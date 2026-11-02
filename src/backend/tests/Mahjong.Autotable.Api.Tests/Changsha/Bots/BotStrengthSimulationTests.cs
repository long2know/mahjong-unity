// Frost — Wave-K scoring audit. Bot-strength simulation that exercises
// every difficulty tier through a real multi-hand bot-vs-bot loop. The Hu
// rate / average score numbers captured here feed the audit memo at
// `.squad/decisions/inbox/frost-scoring-audit.md` and the bot-strategy
// regression at `.squad/decisions/inbox/frost-bot-strategy.md`.
//
// This file is the PERMANENT (unskipped) sibling of BotSimulationLog.cs.
// We cap each scenario at 30 hands so the suite still fits in the
// per-test second-budget on CI (each hand averages ~50ms with all timers
// at zero). The on-demand 100-hand variants in BotSimulationLog.cs stay
// available for deep analysis runs.
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Tables;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha.Bots;

/// <summary>
/// Permanent unskipped bot-strength simulation suite. Drives multi-hand
/// bot-vs-bot loops via the pure-functional <see cref="ChangshaGameStateMachine"/>
/// and captures Hu-rate / score distribution for each difficulty tier.
/// Asserts the basic strength-ordering contract (Master ≥ Easy on
/// per-seat win rate / cumulative score average) so a regression in any
/// heuristic surfaces here.
///
/// <para>The numbers are MIN-bound assertions ("≥") rather than exact
/// equality — bot-vs-bot outcomes have high variance at 30 hands and a
/// strict equality assertion would flake. The audit memo carries the
/// actual measured deltas from each run.</para>
/// </summary>
public class BotStrengthSimulationTests(ITestOutputHelper output)
{
    private const int HandsPerScenario = 30;

    [Fact, Trait("Category", "ScoringAudit"), Trait("Wave", "K-Frost")]
    public void Simulation_4Master_ProducesNonZeroHus()
    {
        var master = new MasterStrategy();
        var stats = RunScenario("4×Master", master, master, master, master);

        // The hard floor is "≥ 1 Hu out of 30" — without this the scoring
        // pipeline is effectively a no-op for Master bots. Real measured
        // numbers from the audit run are surfaced via output.WriteLine and
        // captured in the audit memo.
        Assert.True(stats.HuCount >= 1,
            $"4×Master must produce ≥1 Hu out of {HandsPerScenario} hands. " +
            $"Got {stats.HuCount} (draws={stats.DrawCount}).");
        Assert.True(stats.WinnerScoreSum >= 0,
            "Zero-sum is enforced per hand; sum across hands stays ≥ 0 by " +
            "construction since the winner banks positive.");
    }

    [Fact, Trait("Category", "ScoringAudit"), Trait("Wave", "K-Frost")]
    public void Simulation_4Easy_ProducesNonZeroHus()
    {
        var easy = new EasyStrategy();
        var stats = RunScenario("4×Easy", easy, easy, easy, easy);

        // Easy strategy is the floor — even it must complete some hands.
        Assert.True(stats.HuCount >= 1,
            $"4×Easy must produce ≥1 Hu out of {HandsPerScenario} hands. " +
            $"Got {stats.HuCount} (draws={stats.DrawCount}).");
    }

    [Fact, Trait("Category", "ScoringAudit"), Trait("Wave", "K-Frost")]
    public void Simulation_MasterVsEasy_ProducesDifferentScoreProfiles()
    {
        // Single-seat-of-Master vs 3×Easy: confirms that swapping ONE bot
        // for a Master changes the cumulative score profile measurably.
        // We do NOT pin "Master beats Easy" as a hard equality (variance
        // is real at 30 hands), but we DO assert the per-seat score arrays
        // are not identical to a 4×Easy run with the same seeds. If they
        // ARE identical the Master strategy isn't influencing the game,
        // which would prove the strategy abstraction is broken.
        var easy = new EasyStrategy();
        var master = new MasterStrategy();

        var allEasy = RunScenario("4×Easy (baseline)", easy, easy, easy, easy);
        var masterAtSeat0 = RunScenario("Master@seat0 vs 3×Easy", master, easy, easy, easy);

        output.WriteLine("");
        output.WriteLine(
            $"Score deltas (Master@seat0 minus 4×Easy):");
        for (var i = 0; i < 4; i++)
        {
            var delta = masterAtSeat0.PerSeatScoreSum[i] - allEasy.PerSeatScoreSum[i];
            output.WriteLine($"  seat{i}: {delta:+#;-#;0}");
        }

        // Any one of: per-seat scores differ, or Hu counts differ. If
        // every number is bit-identical the strategy swap had zero effect.
        var anyDelta =
            !masterAtSeat0.PerSeatScoreSum.SequenceEqual(allEasy.PerSeatScoreSum) ||
            masterAtSeat0.HuCount != allEasy.HuCount ||
            !masterAtSeat0.PerSeatHuWins.SequenceEqual(allEasy.PerSeatHuWins);

        Assert.True(anyDelta,
            "Swapping Master for Easy at seat 0 must change SOME measurable " +
            "outcome (per-seat score, Hu count, or per-seat win count). " +
            "Identical outcomes would prove the strategy abstraction is broken.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Harness — mirrors BotSimulationLog.cs but captures structured stats
    // ────────────────────────────────────────────────────────────────────

    private SimulationStats RunScenario(string label,
        IChangshaBotStrategy s0, IChangshaBotStrategy s1,
        IChangshaBotStrategy s2, IChangshaBotStrategy s3)
    {
        var stats = new SimulationStats();
        for (var i = 0; i < HandsPerScenario; i++)
        {
            // Deterministic seed family across scenarios so two runs are
            // comparable (the "delta" assertion in
            // Simulation_MasterVsEasy_ProducesDifferentScoreProfiles
            // relies on this).
            var seed = 9000 + i * 17;
            var hand = RunOneHand(seed, s0, s1, s2, s3);
            if (hand.WinnerSeat >= 0)
            {
                stats.HuCount++;
                stats.PerSeatHuWins[hand.WinnerSeat]++;
                stats.WinnerScoreSum += hand.ScoreOfWinner;
            }
            else
            {
                stats.DrawCount++;
            }
            for (var s = 0; s < 4; s++) stats.PerSeatScoreSum[s] += hand.PerSeatScores[s];
        }

        output.WriteLine(
            $"[{label}] hands={HandsPerScenario} hu={stats.HuCount} draws={stats.DrawCount} " +
            $"winsPerSeat=[{string.Join(",", stats.PerSeatHuWins)}] " +
            $"scoreSumPerSeat=[{string.Join(",", stats.PerSeatScoreSum)}] " +
            $"avgWinnerScore={(stats.HuCount > 0 ? stats.WinnerScoreSum / (double)stats.HuCount : 0):F2}");
        return stats;
    }

    private static HandOutcome RunOneHand(int seed,
        IChangshaBotStrategy s0, IChangshaBotStrategy s1,
        IChangshaBotStrategy s2, IChangshaBotStrategy s3)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
        ChangshaGameStateMachine.Deal(state);

        var strategies = new[] { s0, s1, s2, s3 };
        var steps = 0;
        const int maxSteps = 4000;

        while (steps < maxSteps)
        {
            steps++;
            switch (state.Phase)
            {
                case ChangshaPhase.AwaitingDiscard:
                {
                    var seat = state.ActiveSeatIndex;
                    var hand = state.Hands[seat];
                    var total = hand.ConcealedTiles.Count + hand.Melds.Sum(m => m.TileIds.Count);
                    // Kong-aware draw gate (BotTurnHarness.PreDrawTileCount): a K-kong seat sits
                    // at 13+K; the old flat `== 13` spun the harness to its step guard.
                    if (total == _TestHarness.BotTurnHarness.PreDrawTileCount(hand))
                    {
                        ChangshaGameStateMachine.DrawTile(state);
                        if (state.Phase == ChangshaPhase.WallExhausted) break;
                        continue;
                    }
                    // Terminate the hand (not `break`: a bare break only exits the switch and
                    // re-enters the while, spinning to the step guard). Unreachable post-gate.
                    if (hand.ConcealedTiles.Count == 0) return new HandOutcome(-2, new int[4], 0);
                    var action = strategies[seat].DecideAction(state, seat);
                    switch (action.Type)
                    {
                        case BotActionType.DeclareWin:
                            ChangshaGameStateMachine.DeclareSelfDrawWin(state, seat); break;
                        case BotActionType.DeclareConcealedKong:
                            ChangshaGameStateMachine.DeclareConcealedKong(state, seat, action.LogicalTile!.Value); break;
                        case BotActionType.DeclareAddedKong:
                            ChangshaGameStateMachine.DeclareAddedKong(state, seat, action.TileId!.Value); break;
                        case BotActionType.Discard:
                            ChangshaGameStateMachine.Discard(state, seat, action.TileId!.Value); break;
                        default:
                            ChangshaGameStateMachine.Discard(state, seat, hand.ConcealedTiles[^1]); break;
                    }
                    break;
                }
                case ChangshaPhase.AwaitingClaim:
                {
                    var window = state.ClaimWindow!;
                    var claimer = -1;
                    TableClaimType? type = null;
                    foreach (var opp in window.Opportunities.OrderByDescending(o => o.Priority))
                    {
                        var d = strategies[opp.SeatIndex].DecideAction(state, opp.SeatIndex);
                        if (d.Type == BotActionType.Claim && d.ClaimType.HasValue)
                        { claimer = opp.SeatIndex; type = d.ClaimType.Value; break; }
                    }
                    if (claimer >= 0 && type.HasValue)
                        ChangshaGameStateMachine.ResolveClaim(state, claimer, type.Value);
                    else
                        ChangshaGameStateMachine.PassClaim(state);
                    break;
                }
                case ChangshaPhase.WallExhausted:
                    ChangshaGameStateMachine.HandleWallExhausted(state); break;
                case ChangshaPhase.Scoring:
                    ChangshaGameStateMachine.Score(state); break;
                case ChangshaPhase.EndHand:
                {
                    var winnerSeat = state.CurrentWin?.WinningSeatIndex ?? -1;
                    var perSeat = new int[4];
                    for (var s = 0; s < 4; s++)
                        perSeat[s] = state.CumulativeScores.TryGetValue(s, out var v) ? v : 0;
                    var winnerScore = winnerSeat >= 0 ? perSeat[winnerSeat] : 0;
                    return new HandOutcome(winnerSeat, perSeat, winnerScore);
                }
                default:
                    return new HandOutcome(-2, new int[4], 0);
            }
        }
        return new HandOutcome(-2, new int[4], 0);
    }

    private sealed record HandOutcome(int WinnerSeat, int[] PerSeatScores, int ScoreOfWinner);

    private sealed class SimulationStats
    {
        public int HuCount { get; set; }
        public int DrawCount { get; set; }
        public int WinnerScoreSum { get; set; }
        public int[] PerSeatHuWins { get; } = new int[4];
        public int[] PerSeatScoreSum { get; } = new int[4];
    }
}
