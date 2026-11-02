using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// FIX-5 (Phase 3 stream B): missed-win (过胡) rule per spec §3.6.
/// Once a seat declines a winning discard within a claim window, they are forbidden from
/// winning on subsequent discards within the same hand. Self-draw wins remain allowed.
/// The flag clears on every new hand (in <see cref="ChangshaGameStateMachine.Deal"/>).
/// </summary>
public class MissedWinTests
{
    /// <summary>
    /// Build a 13-tile near-winning hand for seat 1 that wins on Wan-1 (logical 0).
    /// Composition: Wan-2-3 (need Wan-1), Wan-4-5-6, Wan-7-8-9, Tong-5-5 (258 pair), Tong-1-2-3.
    /// </summary>
    private static List<int> ThirteenTileWaitingForWan1()
    {
        // Uses copy-0 of each tile for determinism.
        return new List<int>
        {
            Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 8, 0), Tid(Suit.Wan, 9, 0),
            Tid(Suit.Tong, 5, 0), Tid(Suit.Tong, 5, 1),
            Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0)
        };
    }

    private static ChangshaGameState SetupHandWithSeat1Tenpai()
    {
        // Use NewGameDealtTo to construct a real dealt state, then overwrite seat 1's hand.
        var state = NewGameDealtTo(seed: 17);
        // Ensure dealer is seat 0 for predictability.
        state.DealerSeatIndex = 0;
        state.ActiveSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;

        state.Hands[1].ConcealedTiles = ThirteenTileWaitingForWan1();
        state.Hands[1].Melds.Clear();

        // The dealer holds Wan-1 to be able to discard it on demand. Inject a copy.
        var wan1 = Tid(Suit.Wan, 1, 0);
        var dealerHand = state.Hands[0];
        // Strip the seat 0 hand to a controlled 14-tile state for clean discard semantics.
        dealerHand.ConcealedTiles.Clear();
        for (var i = 0; i < 14; i++)
            dealerHand.ConcealedTiles.Add(i == 0 ? wan1 : Tid(Suit.Tiao, (i % 9) + 1, i / 4));
        state.Phase = ChangshaPhase.AwaitingDiscard;
        return state;
    }

    [Fact, Trait("Category", "Changsha")]
    public void MissedWin_DeclinesWinningDiscard_BlockedFromLaterDiscardWins()
    {
        var state = SetupHandWithSeat1Tenpai();
        var wan1 = Tid(Suit.Wan, 1, 0);

        // Discard 1: dealer discards Wan-1; seat 1 has Hu opportunity but passes.
        ChangshaGameStateMachine.Discard(state, 0, wan1);
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.Contains(state.ClaimWindow!.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);

        ChangshaGameStateMachine.PassClaim(state);
        Assert.Contains(1, state.MissedWinSeats);

        // Drive back to dealer (seat 0) so they can discard another Wan-1 (copy 1).
        // For controlled testing, mutate state to put seat 0 back in AwaitingDiscard.
        state.ActiveSeatIndex = 0;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        // Ensure dealer holds a second Wan-1 copy.
        var wan1Copy2 = Tid(Suit.Wan, 1, 1);
        state.Hands[0].ConcealedTiles.Add(wan1Copy2);

        // Discard 2: dealer discards another Wan-1. Seat 1's Hu opportunity should be STRIPPED.
        ChangshaGameStateMachine.Discard(state, 0, wan1Copy2);
        if (state.ClaimWindow is null)
        {
            // No claim window opened at all — even better; seat 1's only opportunity was Hu and it was filtered.
            Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        }
        else
        {
            Assert.DoesNotContain(state.ClaimWindow.Opportunities,
                o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);
        }
    }

    [Fact, Trait("Category", "Changsha")]
    public void MissedWin_DoesNotBlockSelfDraw()
    {
        // Mark seat 0 as missed-win, then verify a self-draw win still goes through.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 1);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(1));
        ChangshaGameStateMachine.Deal(state);

        var dealer = state.DealerSeatIndex;
        state.MissedWinSeats.Add(dealer);

        // Set up a 14-tile winning hand for the dealer (Standard with 258 pair).
        state.Hands[dealer].ConcealedTiles = new List<int>
        {
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 8, 0), Tid(Suit.Wan, 9, 0),
            Tid(Suit.Tong, 5, 0), Tid(Suit.Tong, 5, 1),
            Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0)
        };
        state.Hands[dealer].Melds.Clear();
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.ActiveSeatIndex = dealer;

        // Self-draw declaration should succeed despite missed-win flag.
        var events = ChangshaGameStateMachine.DeclareSelfDrawWin(state, dealer);
        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.SelfDraw, state.CurrentWin!.Method);
        Assert.Contains(events, e => e.EventType == "win-declared");
    }

    [Fact, Trait("Category", "Changsha")]
    public void MissedWin_ResetsOnNewHand()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 7);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(7));
        ChangshaGameStateMachine.Deal(state);

        state.MissedWinSeats.Add(1);
        state.MissedWinSeats.Add(2);

        // Simulate end of hand → rotate banker → next hand deals.
        state.Phase = ChangshaPhase.EndHand;
        ChangshaGameStateMachine.RotateBanker(state);

        // Next hand: re-roll dice + re-deal.
        state.Phase = ChangshaPhase.RollingDice;
        ChangshaGameStateMachine.RollDice(state, new DiceService(7));
        ChangshaGameStateMachine.Deal(state);

        Assert.Empty(state.MissedWinSeats);
    }

    [Fact, Trait("Category", "Changsha")]
    public void MissedWin_PungOrKongStillAllowedAfterMissedWin()
    {
        // After missing a win, seat 1 is blocked from Hu but should still be able to Pung/Kong.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 19);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(19));
        ChangshaGameStateMachine.Deal(state);

        state.DealerSeatIndex = 0;
        state.ActiveSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.MissedWinSeats.Add(1);

        // Seat 1 holds a pair of Tong-5; dealer discards Tong-5 (copy 2).
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 0));
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 1));
        var dealerTong5 = Tid(Suit.Tong, 5, 2);
        state.Hands[0].ConcealedTiles.Add(dealerTong5);
        state.Phase = ChangshaPhase.AwaitingDiscard;

        ChangshaGameStateMachine.Discard(state, 0, dealerTong5);
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.Contains(state.ClaimWindow!.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Pung);
        // Sanity: no Hu opportunity was offered (seat 1 doesn't have one here anyway, but
        // confirm that Pung is what's available and is honored).
        Assert.DoesNotContain(state.ClaimWindow.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);

        // Pung still resolves cleanly.
        ChangshaGameStateMachine.ResolveClaim(state, 1, TableClaimType.Pung);
        Assert.Single(state.Hands[1].Melds);
        Assert.Equal(MeldKind.Pung, state.Hands[1].Melds[0].Kind);
    }

    [Fact, Trait("Category", "Changsha")]
    public void MissedWin_TwoSeatsHadHu_OneWins_OtherFlagged()
    {
        // Two seats had Hu in the same window; one declared, the other is now blocked.
        // We construct this synthetically via FlagMissedWinSeats public surface (via Discard + Hu claim).
        var state = SetupHandWithSeat1Tenpai();
        // Give seat 2 the same near-winning shape so both have Hu on Wan-1.
        state.Hands[2].ConcealedTiles = new List<int>
        {
            // Use copy-1 of overlapping tiles where seat 1 used copy-0; mix in a non-overlapping
            // pair so the hand can decompose.
            Tid(Suit.Wan, 2, 1), Tid(Suit.Wan, 3, 1),
            Tid(Suit.Wan, 4, 1), Tid(Suit.Wan, 5, 1), Tid(Suit.Wan, 6, 1),
            Tid(Suit.Wan, 7, 1), Tid(Suit.Wan, 8, 1), Tid(Suit.Wan, 9, 1),
            Tid(Suit.Tong, 5, 2), Tid(Suit.Tong, 5, 3),
            Tid(Suit.Tong, 1, 1), Tid(Suit.Tong, 2, 1), Tid(Suit.Tong, 3, 1)
        };
        state.Hands[2].Melds.Clear();

        var wan1 = Tid(Suit.Wan, 1, 0);
        ChangshaGameStateMachine.Discard(state, 0, wan1);
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        var huSeats = state.ClaimWindow!.Opportunities
            .Where(o => o.ClaimType == TableClaimType.Hu).Select(o => o.SeatIndex).ToList();
        Assert.Contains(1, huSeats);
        Assert.Contains(2, huSeats);

        // Seat 1 declares Hu — seat 2 had Hu but didn't take it.
        ChangshaGameStateMachine.ResolveClaim(state, claimingSeatIndex: 1, TableClaimType.Hu);
        Assert.Contains(2, state.MissedWinSeats);
        Assert.DoesNotContain(1, state.MissedWinSeats); // the winner is not flagged
    }
}
