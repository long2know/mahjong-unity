using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-D: Turn Flow Tests (Draw → Discard → Claim Window)
/// </summary>
public class TurnFlowTests
{
    private static int FindNonClaimableTile(ChangshaGameState state, int seatIndex)
    {
        // Pick a tile from the seat's hand whose discard would not open a claim window.
        var hand = state.Hands[seatIndex];
        var adjudicator = new ClaimAdjudicator();
        foreach (var t in hand.ConcealedTiles)
        {
            var opps = adjudicator.GetOpportunities(seatIndex, t, state.Hands);
            if (opps.Count == 0) return t;
        }
        return hand.ConcealedTiles[^1];
    }

    [Fact, Trait("Category", "Changsha")]
    public void DrawTile_FromFrontOfWall_AddsToActiveSeatHand()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        var beforeWall = state.Wall.Count;
        var beforeHand = state.Hands[dealer].ConcealedTiles.Count;

        var tile = FindNonClaimableTile(state, dealer);
        ChangshaGameStateMachine.Discard(state, dealer, tile);
        // After discard, advance past any claim window.
        if (state.Phase == ChangshaPhase.AwaitingClaim)
            ChangshaGameStateMachine.PassClaim(state);

        // Now next seat draws.
        var nextSeat = state.ActiveSeatIndex;
        var beforeNext = state.Hands[nextSeat].ConcealedTiles.Count;
        ChangshaGameStateMachine.DrawTile(state);

        Assert.Equal(beforeNext + 1, state.Hands[nextSeat].ConcealedTiles.Count);
        Assert.Equal(beforeWall - 1, state.Wall.Count);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Discard_RemovesTileFromHand_AddsToDiscardPile()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        var tile = FindNonClaimableTile(state, dealer);

        ChangshaGameStateMachine.Discard(state, dealer, tile);

        Assert.DoesNotContain(tile, state.Hands[dealer].ConcealedTiles);
        Assert.Contains(state.DiscardPile, d => d.TileId == tile && d.SeatIndex == dealer);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Discard_FromWrongSeat_Throws()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        var notDealer = (dealer + 1) % 4;

        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.Discard(state, notDealer, state.Hands[notDealer].ConcealedTiles[0]));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Discard_TileNotInHand_Throws()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        var foreignTile = Enumerable.Range(0, 108)
            .First(t => !state.Hands[dealer].ConcealedTiles.Contains(t));

        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.Discard(state, dealer, foreignTile));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Discard_OpensClaimWindow_WhenOtherSeatHasMatchingPair()
    {
        // Construct a hand-built state where seat 1 holds a pair of dots-5 and seat 0 discards dots-5.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        // Sanitise: remove any pre-existing Tong-5 from ALL hands so the injected scenario
        // is deterministic regardless of what the seed-11 deal produced. Without this the
        // test is flaky (a Tong-5 from the deal can promote the injected pair to a Kong).
        var tong5Logical = ChangshaTestHelpers.Logical(Suit.Tong, 5);
        foreach (var hand in state.Hands)
            hand.ConcealedTiles.RemoveAll(t => t / 4 == tong5Logical);
        // Force seat 1 to hold two copies of Tong-5 (we mutate the test state directly).
        state.Hands[1].ConcealedTiles.Add(ChangshaTestHelpers.Tid(Suit.Tong, 5, 0));
        state.Hands[1].ConcealedTiles.Add(ChangshaTestHelpers.Tid(Suit.Tong, 5, 1));
        // Ensure seat 0 holds Tong-5 copy 2 (different copy id) for discard.
        var dealer = state.DealerSeatIndex;
        var t5 = ChangshaTestHelpers.Tid(Suit.Tong, 5, 2);
        state.Hands[dealer].ConcealedTiles.Add(t5);

        ChangshaGameStateMachine.Discard(state, dealer, t5);

        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.NotNull(state.ClaimWindow);
        Assert.Contains(state.ClaimWindow!.Opportunities, o =>
            o.SeatIndex == 1 && o.ClaimType == TableClaimType.Pung);
    }

    [Fact, Trait("Category", "Changsha")]
    public void PassClaim_AdvancesToNextSeatCounterClockwise()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        // Sanitise pre-existing Tong-5 from all hands (same flake guard as the test above).
        var tong5Logical = ChangshaTestHelpers.Logical(Suit.Tong, 5);
        foreach (var hand in state.Hands)
            hand.ConcealedTiles.RemoveAll(t => t / 4 == tong5Logical);
        // Force a claimable discard via injected pung opportunity.
        state.Hands[1].ConcealedTiles.Add(ChangshaTestHelpers.Tid(Suit.Tong, 5, 0));
        state.Hands[1].ConcealedTiles.Add(ChangshaTestHelpers.Tid(Suit.Tong, 5, 1));
        var t5 = ChangshaTestHelpers.Tid(Suit.Tong, 5, 2);
        state.Hands[dealer].ConcealedTiles.Add(t5);

        ChangshaGameStateMachine.Discard(state, dealer, t5);
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);

        ChangshaGameStateMachine.PassClaim(state);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal((dealer + 1) % 4, state.ActiveSeatIndex);
    }
}
