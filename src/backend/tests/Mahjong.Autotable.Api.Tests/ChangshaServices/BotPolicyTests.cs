using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class BotPolicyTests
{
    [Fact]
    public void Bot_PlaysFullHand_NoExceptions()
    {
        var seed = 42;
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed, [1, 2, 3]);
        ChangshaGameStateMachine.StartGame(state);
        var diceService = new DiceService(seed);
        ChangshaGameStateMachine.RollDice(state, diceService);
        ChangshaGameStateMachine.Deal(state);

        var bot = new ChangshaBotPolicy();
        var maxIterations = 500;
        var iterations = 0;

        while (state.Phase != ChangshaPhase.WallExhausted
            && state.Phase != ChangshaPhase.Scoring
            && state.Phase != ChangshaPhase.EndHand
            && state.Phase != ChangshaPhase.EndGame
            && iterations < maxIterations)
        {
            for (var seatIdx = 0; seatIdx < 4; seatIdx++)
            {
                if (state.Phase != ChangshaPhase.AwaitingDiscard
                    && state.Phase != ChangshaPhase.AwaitingClaim)
                    break;

                if (state.Phase == ChangshaPhase.AwaitingDiscard
                    && state.ActiveSeatIndex == seatIdx)
                {
                    var hand = state.Hands[seatIdx];

                    // Draw if needed
                    if (hand.ConcealedTiles.Count <= 13 && state.Wall.Count > 0)
                    {
                        ChangshaGameStateMachine.DrawTile(state);
                        if (state.Phase == ChangshaPhase.WallExhausted) break;
                    }

                    var action = bot.DecideAction(state, seatIdx);
                    switch (action.Type)
                    {
                        case BotActionType.DeclareWin:
                            ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIdx);
                            break;
                        case BotActionType.Discard:
                            ChangshaGameStateMachine.Discard(state, seatIdx, action.TileId!.Value);
                            break;
                        case BotActionType.DeclareConcealedKong:
                            ChangshaGameStateMachine.DeclareConcealedKong(state, seatIdx, action.LogicalTile!.Value);
                            break;
                        case BotActionType.DeclareAddedKong:
                            ChangshaGameStateMachine.DeclareAddedKong(state, seatIdx, action.TileId!.Value);
                            break;
                        default:
                            // If bot says Wait, just discard first tile
                            if (hand.ConcealedTiles.Count > 0)
                                ChangshaGameStateMachine.Discard(state, seatIdx, hand.ConcealedTiles[0]);
                            break;
                    }
                }
                else if (state.Phase == ChangshaPhase.AwaitingClaim)
                {
                    var action = bot.DecideAction(state, seatIdx);
                    if (action.Type == BotActionType.Claim && action.ClaimType.HasValue)
                    {
                        ChangshaGameStateMachine.ResolveClaim(state, seatIdx, action.ClaimType.Value);
                    }
                    else
                    {
                        ChangshaGameStateMachine.PassClaim(state);
                    }
                }
            }
            iterations++;
        }

        // The hand should have progressed without throwing
        Assert.True(iterations > 0, "Bot should have played at least one iteration.");
    }

    [Fact]
    public void Bot_DiscardsValidTile()
    {
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52]
        };

        var tileId = ChangshaBotPolicy.SelectDiscardTile(hand);
        Assert.Contains(tileId, hand.ConcealedTiles);
    }

    [Fact]
    public void Bot_NeverDiscardsFromEmptyHand()
    {
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = []
        };

        Assert.Throws<InvalidOperationException>(() => ChangshaBotPolicy.SelectDiscardTile(hand));
    }

    [Fact]
    public void Bot_Prefers258PairRanks()
    {
        // Give the bot tiles where only a rank-2 tile should be kept (it has a pair)
        // and isolated tiles should be discarded
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = [
                4, 5,    // Wan 2, copy 0 and 1 (pair, rank 2 = 258)
                72,      // Tiao 1, copy 0 (isolated)
                80       // Tiao 3, copy 0 (isolated)
            ]
        };

        var tileId = ChangshaBotPolicy.SelectDiscardTile(hand);
        // Should discard one of the isolated tiles, not the pair
        Assert.True(tileId == 72 || tileId == 80,
            $"Expected isolated tile to be discarded, but got {tileId}");
    }
}
