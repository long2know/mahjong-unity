using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

internal static class BotMatchHarness
{
    public sealed record MatchOutcome(
        ChangshaGameState FinalState,
        int Steps,
        bool WinnerDeclared,
        bool WallExhausted);

    public static MatchOutcome RunUntilHandFinished(int seed, int maxSteps = 4000)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
        ChangshaGameStateMachine.Deal(state);

        var policy = new ChangshaBotPolicy();
        var steps = 0;

        while (steps < maxSteps)
        {
            steps++;

            switch (state.Phase)
            {
                case ChangshaPhase.AwaitingDiscard:
                {
                    var seat = state.ActiveSeatIndex;
                    var hand = state.Hands[seat];

                    // Kong-aware draw gate (BotTurnHarness.PreDrawTileCount): a seat owes a draw
                    // at 13 + kongs (a K-kong seat sits at 13+K); ready to discard once above it.
                    var totalTiles = hand.ConcealedTiles.Count + hand.Melds.Sum(m => m.TileIds.Count);
                    if (totalTiles == BotTurnHarness.PreDrawTileCount(hand))
                    {
                        ChangshaGameStateMachine.DrawTile(state);
                        if (state.Phase == ChangshaPhase.WallExhausted) break;
                        continue;
                    }
                    if (hand.ConcealedTiles.Count == 0)
                    {
                        // Defensive: a fully-melded, zero-concealed hand cannot discard. Unreachable
                        // once the kong-aware gate above fires. Must RETURN a terminal outcome, not
                        // `break` (which only exits the switch and re-enters the while, spinning to
                        // the step guard). Treat as a no-winner force-end.
                        return new MatchOutcome(state, steps, WinnerDeclared: state.CurrentWin is not null, WallExhausted: true);
                    }

                    var action = policy.DecideAction(state, seat);
                    switch (action.Type)
                    {
                        case BotActionType.DeclareWin:
                            ChangshaGameStateMachine.DeclareSelfDrawWin(state, seat);
                            break;
                        case BotActionType.DeclareConcealedKong:
                            ChangshaGameStateMachine.DeclareConcealedKong(state, seat, action.LogicalTile!.Value);
                            break;
                        case BotActionType.DeclareAddedKong:
                            ChangshaGameStateMachine.DeclareAddedKong(state, seat, action.TileId!.Value);
                            break;
                        case BotActionType.Discard:
                            ChangshaGameStateMachine.Discard(state, seat, action.TileId!.Value);
                            break;
                        default:
                            ChangshaGameStateMachine.Discard(state, seat, hand.ConcealedTiles[^1]);
                            break;
                    }
                    break;
                }
                case ChangshaPhase.AwaitingClaim:
                {
                    var window = state.ClaimWindow!;
                    var claimerSeat = -1;
                    TableClaimType? claimType = null;

                    foreach (var opp in window.Opportunities.OrderByDescending(o => o.Priority))
                    {
                        var decision = policy.DecideAction(state, opp.SeatIndex);
                        if (decision.Type == BotActionType.Claim && decision.ClaimType.HasValue)
                        {
                            claimerSeat = opp.SeatIndex;
                            claimType = decision.ClaimType.Value;
                            break;
                        }
                    }

                    if (claimerSeat >= 0 && claimType.HasValue)
                        ChangshaGameStateMachine.ResolveClaim(state, claimerSeat, claimType.Value);
                    else
                        ChangshaGameStateMachine.PassClaim(state);
                    break;
                }
                case ChangshaPhase.WallExhausted:
                    ChangshaGameStateMachine.HandleWallExhausted(state);
                    break;
                case ChangshaPhase.Scoring:
                    ChangshaGameStateMachine.Score(state);
                    break;
                case ChangshaPhase.EndHand:
                    return new MatchOutcome(state, steps, state.CurrentWin is not null, state.CurrentWin is null);
                default:
                    throw new InvalidOperationException($"Unexpected phase {state.Phase}");
            }
        }

        throw new InvalidOperationException($"Bot match did not finish within {maxSteps} steps. Phase={state.Phase}");
    }
}
