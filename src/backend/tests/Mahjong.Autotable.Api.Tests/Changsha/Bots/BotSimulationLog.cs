// Wave-24 (Frost) — one-shot 100-game bot-vs-bot simulation, only run
// when filtered explicitly via FullyQualifiedName ~ BotSimulation.
// Tagged with a Category trait so the suite never picks it up at large.
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Tables;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha.Bots;

public class BotSimulationLog
{
    private readonly ITestOutputHelper _output;
    public BotSimulationLog(ITestOutputHelper output) { _output = output; }

    [Fact(Skip = "On-demand simulation; remove Skip locally to run."), Trait("Category", "Simulation")]
    public void Simulation_4Master_100Hands_WinRateDistribution()
    {
        var master = new MasterStrategy();
        var wins = new int[4];
        var draws = 0;

        for (var i = 0; i < 100; i++)
        {
            var seed = 5000 + i * 31;
            var winner = RunOneHand(seed, master, master, master, master);
            if (winner is >= 0 and < 4) wins[winner]++;
            else draws++;
        }

        _output.WriteLine($"4×Master 100-game win distribution: " +
            $"seat0={wins[0]} seat1={wins[1]} seat2={wins[2]} seat3={wins[3]} draws={draws}");
    }

    [Fact(Skip = "On-demand simulation; remove Skip locally to run."), Trait("Category", "Simulation")]
    public void Simulation_MasterVsHard_100Hands_WinRateLift()
    {
        var master = new MasterStrategy();
        var hard = new HardStrategy();
        var masterWins = 0;
        var hardWinsTotal = 0;
        var draws = 0;

        for (var i = 0; i < 100; i++)
        {
            var seed = 5000 + i * 31;
            var winner = RunOneHand(seed, master, hard, hard, hard);
            if (winner == 0) masterWins++;
            else if (winner >= 1 && winner <= 3) hardWinsTotal++;
            else draws++;
        }

        var hardAvg = hardWinsTotal / 3.0;
        _output.WriteLine(
            $"Master vs 3×Hard over 100 hands: " +
            $"masterWins={masterWins}, hardWinsTotal={hardWinsTotal} (avg/seat={hardAvg:F2}), draws={draws}");
    }

    private static int RunOneHand(int seed,
        IChangshaBotStrategy seat0, IChangshaBotStrategy seat1,
        IChangshaBotStrategy seat2, IChangshaBotStrategy seat3)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
        ChangshaGameStateMachine.Deal(state);

        var strategies = new[] { seat0, seat1, seat2, seat3 };
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
                    if (hand.ConcealedTiles.Count == 0) return -2;
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
                    return state.CurrentWin?.WinningSeatIndex ?? -1;
                default:
                    return -2;
            }
        }
        return -2;
    }
}
