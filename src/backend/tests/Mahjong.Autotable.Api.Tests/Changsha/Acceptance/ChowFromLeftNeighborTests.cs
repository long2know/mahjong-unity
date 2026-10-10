using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Chow restricted to the next-CCW seat per Vasquez §1.6 + MahjongPros §"Chow"
/// + Baidu §"吃". Pung/Kong are unrestricted across seats; only Chow is constrained.
///
/// "Next seat" in Changsha = the seat the discarder would normally pass the turn to
/// (CCW from discarder = (discardSeat + 1) % 4). Identical wording to Riichi's
/// "from the player on your left," confirmed in Vasquez rules-diff manifest.
/// </summary>
public class ChowFromLeftNeighborTests
{
    private static IReadOnlyList<ChangshaHandState> SeededHands(params (int seat, IEnumerable<int> tiles)[] hands)
    {
        var seats = new List<ChangshaHandState>();
        for (var i = 0; i < 4; i++)
        {
            var match = hands.FirstOrDefault(h => h.seat == i);
            seats.Add(new ChangshaHandState
            {
                SeatIndex = i,
                ConcealedTiles = (match.tiles ?? Enumerable.Empty<int>()).ToList()
            });
        }
        return seats;
    }

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(0, 1, true)]   // discarder 0 → next 1 may chow
    [InlineData(0, 2, false)]  // across — no chow
    [InlineData(0, 3, false)]  // right (CCW-prev) — no chow
    [InlineData(1, 2, true)]   // dealer rotates → next-CCW also rotates
    [InlineData(3, 0, true)]   // wrap-around
    public void Chow_OnlyAllowedFromNextCcwSeat(int discardSeat, int claimSeat, bool chowAllowed)
    {
        // Vasquez §1.6: chow restricted to (discardSeat + 1) % 4 only.
        // Every seat OTHER than the discarder gets the same two-tile chow buddy {Wan-4, Wan-6};
        // discarder offers Wan-5. The adjudicator must surface a chow opportunity ONLY for
        // the (discardSeat+1)%4 seat.
        var pairs = new (int seat, IEnumerable<int> tiles)[3];
        var idx = 0;
        for (var s = 0; s < 4; s++)
        {
            if (s == discardSeat) continue;
            pairs[idx++] = (s, Tiles((Suit.Wan, 4), (Suit.Wan, 6)));
        }
        var hands = SeededHands(pairs);
        var discard = Tid(Suit.Wan, 5, 0);

        var opps = new ClaimAdjudicator().GetOpportunities(discardSeat, discard, hands);
        var seatHasChow = opps.Any(o => o.SeatIndex == claimSeat && o.ClaimType == TableClaimType.Chow);

        if (chowAllowed)
            Assert.True(seatHasChow,
                $"Seat {claimSeat} should have a Chow opportunity from seat {discardSeat}'s discard.");
        else
            Assert.False(seatHasChow,
                $"Seat {claimSeat} must NOT have a Chow opportunity from seat {discardSeat}'s discard (Changsha §1.6 restricts chow to the next-CCW seat).");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Chow_FromLeftNeighbor_Succeeds()
    {
        // MahjongPros §Chow: legitimate next-seat chow forms a Chow meld.
        var state = AcceptanceFixture.NewDealtGame(seed: 31, dealerSeat: 0);
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 4));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 5));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 6));

        // Inject the chow buddies into seat 1 (next CCW).
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Wan, 4, 0));
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Wan, 6, 0));

        // Dealer discards Wan-5.
        var wan5 = Tid(Suit.Wan, 5, 0);
        state.Hands[0].ConcealedTiles.Add(wan5);
        ChangshaGameStateMachine.Discard(state, 0, wan5);

        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.Contains(state.ClaimWindow!.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Chow);

        ChangshaGameStateMachine.ResolveClaim(state, 1, TableClaimType.Chow,
            chosenTileIds: new[] { Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 6, 0) });

        var meld = state.Hands[1].Melds.Single();
        Assert.Equal(MeldKind.Chow, meld.Kind);
        Assert.Equal(3, meld.TileIds.Count);
        Assert.Contains(wan5, meld.TileIds);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Chow_FromLeftNeighbor_ButPungAlsoClaimed_PungWins()
    {
        // Vasquez §1.7: Pung beats Chow at the priority tier. Confirms that even when chow
        // is structurally allowed (next-CCW seat) it loses to a same-discard Pung claim.
        var hands = SeededHands(
            (1, Tiles((Suit.Wan, 4), (Suit.Wan, 6))),       // chow ready
            (2, Tiles((Suit.Wan, 5), (Suit.Wan, 5))));      // pung ready
        var discard = Tid(Suit.Wan, 5, 2);

        var winner = new ClaimAdjudicator().Adjudicate(0, discard, hands, Array.Empty<Meld>());

        Assert.NotNull(winner);
        Assert.Equal(TableClaimType.Pung, winner!.ClaimType);
        Assert.Equal(2, winner.SeatIndex);
    }
}
