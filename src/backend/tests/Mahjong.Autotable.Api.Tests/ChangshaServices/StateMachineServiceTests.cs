using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class StateMachineServiceTests
{
    [Fact]
    public void CreateGame_InitializesCorrectState()
    {
        var (state, events) = ChangshaGameStateMachine.CreateGame(42);

        Assert.Equal(ChangshaPhase.Seating, state.Phase);
        Assert.Equal(4, state.Seats.Count);
        Assert.Equal(4, state.Hands.Count);
        Assert.Equal(0, state.DealerSeatIndex);
        Assert.Equal(Wind.East, state.RoundWind);
        Assert.Equal(1, state.HandNumber);
        Assert.Single(events);
        Assert.Equal("game-created", events[0].EventType);
    }

    [Fact]
    public void StartGame_TransitionsToRollingDice()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(42);
        var events = ChangshaGameStateMachine.StartGame(state);

        Assert.Equal(ChangshaPhase.RollingDice, state.Phase);
        Assert.Single(events);
        Assert.Equal("game-started", events[0].EventType);
    }

    [Fact]
    public void RollDice_TransitionsToDealing()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(42);
        ChangshaGameStateMachine.StartGame(state);

        var diceService = new DiceService(42);
        var events = ChangshaGameStateMachine.RollDice(state, diceService);

        Assert.Equal(ChangshaPhase.Dealing, state.Phase);
        Assert.NotNull(state.LastDiceRoll);
        Assert.NotNull(state.BreakPoint);
        Assert.Single(events);
    }

    [Fact]
    public void Deal_GivesDealerFourteenTiles()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(42);
        ChangshaGameStateMachine.StartGame(state);

        var diceService = new DiceService(42);
        ChangshaGameStateMachine.RollDice(state, diceService);
        ChangshaGameStateMachine.Deal(state);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(14, state.Hands[state.DealerSeatIndex].ConcealedTiles.Count);

        for (var i = 0; i < 4; i++)
        {
            if (i != state.DealerSeatIndex)
                Assert.Equal(13, state.Hands[i].ConcealedTiles.Count);
        }

        Assert.Equal(55, state.Wall.Count);
    }

    [Fact]
    public void Discard_RemovesTileFromHand()
    {
        var (state, _) = SetupDealDone(42);
        var dealer = state.DealerSeatIndex;
        var hand = state.Hands[dealer];
        var tileToDiscard = hand.ConcealedTiles[0];

        var events = ChangshaGameStateMachine.Discard(state, dealer, tileToDiscard);

        Assert.DoesNotContain(tileToDiscard, hand.ConcealedTiles);
        Assert.Contains(state.DiscardPile, d => d.TileId == tileToDiscard);
        Assert.NotEmpty(events);
    }

    [Fact]
    public void FullHandCycle_DoesNotThrow()
    {
        var (state, _) = SetupDealDone(42);

        // Play through turns until wall exhaustion or someone wins
        var maxTurns = 200;
        var turnCount = 0;

        while (state.Phase != ChangshaPhase.WallExhausted
            && state.Phase != ChangshaPhase.Scoring
            && state.Phase != ChangshaPhase.EndHand
            && state.Phase != ChangshaPhase.EndGame
            && turnCount < maxTurns)
        {
            if (state.Phase == ChangshaPhase.AwaitingDiscard)
            {
                var hand = state.Hands[state.ActiveSeatIndex];
                if (hand.ConcealedTiles.Count == 0) break;

                // Draw if needed (hand should have 14 tiles for dealer, 13+1 after draw for others)
                if (hand.ConcealedTiles.Count <= 13 && state.Wall.Count > 0)
                {
                    ChangshaGameStateMachine.DrawTile(state);
                }

                if (state.Phase == ChangshaPhase.WallExhausted) break;

                var tileId = hand.ConcealedTiles[^1];
                ChangshaGameStateMachine.Discard(state, state.ActiveSeatIndex, tileId);
            }
            else if (state.Phase == ChangshaPhase.AwaitingClaim)
            {
                ChangshaGameStateMachine.PassClaim(state);
            }
            else
            {
                break;
            }
            turnCount++;
        }

        // Should have played some turns without crashing
        Assert.True(turnCount > 0);
    }

    [Fact]
    public void BankerRotation_DealerWins_DealerRetained()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(42);
        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 0, // dealer wins
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.Standard,
            WinningTileId = 0,
            SourceSeatIndex = 0
        };
        state.DealerSeatIndex = 0;

        var events = ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(0, state.DealerSeatIndex); // dealer retained
        Assert.Contains("dealerRetained", events[0].Detail);
    }

    [Fact]
    public void BankerRotation_NonDealerWins_WinnerBecomesDealer()
    {
        // Canonical v1.2 rule (per spec §6.2): winner becomes the next dealer, not (dealer+1)%4.
        var (state, _) = ChangshaGameStateMachine.CreateGame(42);
        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 2,
            Method = WinMethod.Discard,
            Pattern = WinPattern.Standard,
            WinningTileId = 0,
            SourceSeatIndex = 1
        };
        state.DealerSeatIndex = 0;

        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(2, state.DealerSeatIndex); // winner becomes dealer
    }

    [Fact]
    public void BankerRotation_Washout_DealerKeepsSeat()
    {
        // Canonical v1.2 rule: on washout (no winner), dealer keeps the seat.
        var (state, _) = ChangshaGameStateMachine.CreateGame(42);
        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = null;
        state.DealerSeatIndex = 0;

        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(0, state.DealerSeatIndex);
    }

    [Fact]
    public void After16Hands_GameEnds()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(42);
        state.Phase = ChangshaPhase.EndHand;
        state.HandNumber = 16;
        state.HandInRound = 4;
        state.RoundNumber = 4;
        state.CurrentWin = null;
        // Phase J Wave 2 — this test predates the N-hand cap and exercises the
        // legacy 16-hand (4 × 4) EndGame terminal. Raise MaxHands above 16 so
        // RotateBanker falls through to the round-rollover EndGame branch
        // instead of the new MaxHands → GameComplete branch.
        state.MaxHands = 100;

        var events = ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(ChangshaPhase.EndGame, state.Phase);
        Assert.Contains(events, e => e.EventType == "game-ended");
    }

    private static (ChangshaGameState State, List<ChangshaEvent> Events) SetupDealDone(int seed)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed);
        ChangshaGameStateMachine.StartGame(state);
        var diceService = new DiceService(seed);
        ChangshaGameStateMachine.RollDice(state, diceService);
        var events = ChangshaGameStateMachine.Deal(state);
        return (state, events);
    }
}
