using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Banker rotation per Vasquez §1.13 (v1.2 canonical lock):
///   Winner of a hand becomes the next dealer. On washout (no winner), current dealer
///   keeps the seat. Hand counter increments regardless.
///
/// This DIVERGES from Riichi (which uses 4 east hands then rotates regardless). All three
/// canonical sources (MahjongPros, Baidu, Reddit) explicitly say "winner becomes dealer".
///
/// Existing <c>BankerRotationTests.cs</c> covers the unit-level state transition. The
/// acceptance tests here focus on the end-to-end-shaped scenarios Stephen will perceive:
/// playing as dealer, winning, and then naturally being dealer again at the next deal.
/// </summary>
public class BankerRotationTests
{
    private static ChangshaGameState NewEndHandState(int dealerSeat = 0)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 1);
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        state.Phase = ChangshaPhase.EndHand;
        // Phase J Wave 2 — pre-existing acceptance scenarios exercise banker
        // rotation independent of the new N-hand cap. Raise MaxHands so the
        // rotation never trips GameComplete; these tests target winner-becomes-
        // dealer mechanics, not game length.
        state.MaxHands = 100;
        return state;
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Banker_KeepsSeat_OnHu_ByDealer()
    {
        // MahjongPros: when the dealer wins, they remain dealer. Degenerate case of winner-becomes-dealer.
        var state = NewEndHandState(dealerSeat: 0);
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 0,
            SourceSeatIndex = 0,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.Standard,
            WinningTileId = 0
        };

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(0, state.DealerSeatIndex);
        Assert.True(state.Seats[0].IsDealer);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Banker_PassesToWinner_OnNonBankerHu()
    {
        // Baidu §"庄家轮换": non-dealer wins → that seat becomes the new dealer.
        var state = NewEndHandState(dealerSeat: 0);
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 2,
            SourceSeatIndex = 1,
            Method = WinMethod.Discard,
            Pattern = WinPattern.Standard,
            WinningTileId = 0
        };

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(2, state.DealerSeatIndex);
        Assert.True(state.Seats[2].IsDealer);
        Assert.False(state.Seats[0].IsDealer);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Banker_KeepsSeat_OnWashout_DrawHand()
    {
        // Reddit §"Banker rules": washout (流局) leaves the dealer seated.
        var state = NewEndHandState(dealerSeat: 2);
        state.CurrentWin = null;

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(2, state.DealerSeatIndex);
        Assert.True(state.Seats[2].IsDealer);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Banker_RotatesAcrossThreeHands_WithMixedOutcomes()
    {
        // Canonical sequence from Vasquez §1.13:
        //   Hand 1: dealer=0, seat 2 wins → dealer becomes 2
        //   Hand 2: dealer=2, washout      → dealer stays 2
        //   Hand 3: dealer=2, seat 1 wins → dealer becomes 1
        var state = NewEndHandState(dealerSeat: 0);

        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 2, SourceSeatIndex = 0, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(2, state.DealerSeatIndex);

        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = null;
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(2, state.DealerSeatIndex);

        state.Phase = ChangshaPhase.EndHand;
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 1, SourceSeatIndex = 3, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };
        ChangshaGameStateMachine.RotateBanker(state);
        Assert.Equal(1, state.DealerSeatIndex);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Banker_Rotation_DoesNotUse_PlusOneCcw()
    {
        // Negative assertion: the v1.2 canonical sources explicitly reject cyclic +1 rotation.
        // If a regression reintroduces (dealer + 1) % 4, this test catches it.
        var state = NewEndHandState(dealerSeat: 0);
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 3, SourceSeatIndex = 1, Method = WinMethod.Discard,
            Pattern = WinPattern.Standard, WinningTileId = 0
        };

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(3, state.DealerSeatIndex);
        Assert.NotEqual(1, state.DealerSeatIndex); // explicitly not (0 + 1) % 4
    }
}
