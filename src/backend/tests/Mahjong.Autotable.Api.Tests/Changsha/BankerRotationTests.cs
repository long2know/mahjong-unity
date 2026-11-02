using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-H: Banker rotation per spec §6.2 (v1.2 canonical lock).
/// Winner of a hand becomes the next dealer. On washout (no winner), dealer keeps the seat.
/// Hand counter increments either way. There is NO cyclic +1/-1 rotation in v1.
/// </summary>
public class BankerRotationTests
{
    private static ChangshaGameState NewEndHandState(int dealerSeat = 0)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 1);
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        state.Phase = ChangshaPhase.EndHand;
        // Phase J Wave 2 — these CAT-H tests pre-date the N-hand cap and
        // exercise long banker-rotation sequences (the 16-hand full-game test
        // below being the canonical example). Raise MaxHands well past 16 so
        // the rotation reaches the legacy 4-round EndGame terminal rather than
        // tripping the new MaxHands → GameComplete terminal.
        state.MaxHands = 100;
        return state;
    }

    [Fact, Trait("Category", "Changsha")]
    public void DealerWins_DealerRetainsSeat()
    {
        var state = NewEndHandState(dealerSeat: 0);
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 0, SourceSeatIndex = 0, Method = WinMethod.SelfDraw,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(0, state.DealerSeatIndex);
        Assert.True(state.Seats[0].IsDealer);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BankerRotation_WinnerBecomesDealer_NotPlusOne()
    {
        // Canonical rule: winner becomes the next dealer. NOT (dealer+1) % 4.
        var state = NewEndHandState(dealerSeat: 0);
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 2, SourceSeatIndex = 1, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(2, state.DealerSeatIndex); // winner becomes dealer
        Assert.NotEqual(1, state.DealerSeatIndex); // NOT the old (dealer+1)%4 = 1
        Assert.NotEqual(3, state.DealerSeatIndex); // NOT the old (dealer-1+4)%4 = 3
        Assert.True(state.Seats[2].IsDealer);
        Assert.False(state.Seats[0].IsDealer);
        Assert.False(state.Seats[1].IsDealer);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BankerRotation_Washout_DealerKeepsSeat()
    {
        // Canonical rule: on washout (CurrentWin null), dealer keeps the seat.
        var state = NewEndHandState(dealerSeat: 1);
        // CurrentWin null → washout

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(1, state.DealerSeatIndex); // unchanged
        Assert.True(state.Seats[1].IsDealer);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Washout_FromSeat3_DealerStaysOnSeat3()
    {
        // Counter-test for the old (dealer+1)%4 = 0 rotation: seat 3 must stay seat 3 on washout.
        var state = NewEndHandState(dealerSeat: 3);
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(3, state.DealerSeatIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void HandNumber_IncrementsOnWinnerAndWashout()
    {
        // Hand counter increments regardless of outcome.
        var winState = NewEndHandState(dealerSeat: 0);
        winState.CurrentWin = new WinResult
        {
            WinningSeatIndex = 1, SourceSeatIndex = 0, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };
        var beforeWin = winState.HandNumber;
        ChangshaGameStateMachine.RotateBanker(winState);
        Assert.Equal(beforeWin + 1, winState.HandNumber);

        var drawState = NewEndHandState(dealerSeat: 0);
        var beforeDraw = drawState.HandNumber;
        ChangshaGameStateMachine.RotateBanker(drawState);
        Assert.Equal(beforeDraw + 1, drawState.HandNumber);
    }

    [Fact, Trait("Category", "Changsha")]
    public void RoundWind_AdvancesAfterFourHands_EastToSouth()
    {
        var state = NewEndHandState(dealerSeat: 0);
        state.HandInRound = 4;
        state.RoundNumber = 1;

        ChangshaGameStateMachine.RotateBanker(state); // 4→5 → wraps → round 2

        Assert.Equal(1, state.HandInRound);
        Assert.Equal(2, state.RoundNumber);
        Assert.Equal(Wind.South, state.RoundWind);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Game_Ends_AfterFourthRoundCompletes()
    {
        var state = NewEndHandState(dealerSeat: 0);
        state.HandInRound = 4;
        state.RoundNumber = 4;

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(ChangshaPhase.EndGame, state.Phase);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BankerRotation_FullGame_16Hands_EmitsGameEnded()
    {
        // Simulate 16 consecutive washouts; dealer stays on seat 0 the entire game.
        // After hand 16 completes, RotateBanker must emit "game-ended" and set EndGame phase.
        var state = NewEndHandState(dealerSeat: 0);

        for (var hand = 1; hand <= 16; hand++)
        {
            // Simulate a hand finishing in washout — no CurrentWin set.
            state.Phase = ChangshaPhase.EndHand;
            state.CurrentWin = null;
            var events = ChangshaGameStateMachine.RotateBanker(state);

            if (hand < 16)
            {
                Assert.NotEqual(ChangshaPhase.EndGame, state.Phase);
                Assert.DoesNotContain(events, e => e.EventType == "game-ended");
                Assert.Equal(0, state.DealerSeatIndex); // washout chain — dealer never moves
            }
            else
            {
                Assert.Equal(ChangshaPhase.EndGame, state.Phase);
                Assert.Contains(events, e => e.EventType == "game-ended");
            }
        }
    }

    [Fact, Trait("Category", "Changsha")]
    public void BankerRotation_CanonicalSequence_MatchesSpec62Example()
    {
        // Vasquez §6.2 worked example:
        //   Initial dealer = seat 0
        //   Hand 1: Seat 2 wins → dealer becomes seat 2
        //   Hand 2: washout → dealer stays seat 2
        //   Hand 3: Seat 1 wins → dealer becomes seat 1
        //   Hand 4: Seat 0 wins → dealer becomes seat 0
        var state = NewEndHandState(dealerSeat: 0);

        // Hand 1: Seat 2 wins
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 2, SourceSeatIndex = 0, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(2, state.DealerSeatIndex);

        // Hand 2: washout
        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = null;
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(2, state.DealerSeatIndex);

        // Hand 3: Seat 1 wins
        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 1, SourceSeatIndex = 3, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(1, state.DealerSeatIndex);

        // Hand 4: Seat 0 wins
        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 0, SourceSeatIndex = 2, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(0, state.DealerSeatIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Rotate_EmitsBankerRotatedEvent()
    {
        var state = NewEndHandState(dealerSeat: 0);

        var events = ChangshaGameStateMachine.RotateBanker(state);

        Assert.Contains(events, e => e.EventType == "banker-rotated");
    }
}
