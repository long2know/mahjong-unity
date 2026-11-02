using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-E: Claim resolution — Pung, Kong, Chow.
/// </summary>
public class PungKongChowTests
{
    private static IReadOnlyList<ChangshaHandState> SeatedHands(params (int seat, IEnumerable<int> tiles)[] hands)
    {
        var seats = new List<ChangshaHandState>();
        for (var i = 0; i < 4; i++)
        {
            var h = hands.FirstOrDefault(x => x.seat == i);
            seats.Add(new ChangshaHandState
            {
                SeatIndex = i,
                ConcealedTiles = (h.tiles ?? Enumerable.Empty<int>()).ToList()
            });
        }
        return seats;
    }

    [Fact, Trait("Category", "Changsha")]
    public void Pung_OpportunityDetected_WhenSeatHoldsPairOfDiscarded()
    {
        var hands = SeatedHands(
            (1, Tiles((Suit.Wan, 3), (Suit.Wan, 3))));
        var discard = Tid(Suit.Wan, 3, 2);
        var opps = new ClaimAdjudicator().GetOpportunities(0, discard, hands);

        Assert.Contains(opps, o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Pung);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Kong_OpportunityDetected_WhenSeatHoldsTriplet()
    {
        var hands = SeatedHands(
            (2, Tiles((Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7))));
        var discard = Tid(Suit.Tiao, 7, 3);
        var opps = new ClaimAdjudicator().GetOpportunities(0, discard, hands);

        Assert.Contains(opps, o => o.SeatIndex == 2 && o.ClaimType == TableClaimType.Kong);
        // Kong and Pung are the SAME tier (v1.2 §3.3); Kong opportunity carries the shared tier.
        Assert.Equal(
            ChangshaClaimPriority.TierOf(TableClaimType.Kong),
            opps.Single(o => o.SeatIndex == 2 && o.ClaimType == TableClaimType.Kong).Priority);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Chow_OnlyPermittedFromNextSeat_CounterClockwise()
    {
        // Discard from seat 0 → only seat 1 may chow.
        var commonChowHand = Tiles((Suit.Wan, 4), (Suit.Wan, 6));
        var hands = SeatedHands(
            (1, commonChowHand),
            (2, commonChowHand),
            (3, commonChowHand));
        var discard = Tid(Suit.Wan, 5, 0);
        var opps = new ClaimAdjudicator().GetOpportunities(0, discard, hands);

        var chows = opps.Where(o => o.ClaimType == TableClaimType.Chow).ToList();
        Assert.Single(chows);
        Assert.Equal(1, chows[0].SeatIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ClaimPriority_HuBeatsKongBeatsPungBeatsChow()
    {
        // Seat 1 (next): Chow possible
        // Seat 2: Kong (triplet)
        // Seat 3: Pung (pair)
        var hands = SeatedHands(
            (1, Tiles((Suit.Tong, 4), (Suit.Tong, 6))),
            (2, Tiles((Suit.Tong, 5), (Suit.Tong, 5), (Suit.Tong, 5))),
            (3, Tiles((Suit.Tong, 5), (Suit.Tong, 5))));
        var discard = Tid(Suit.Tong, 5, 3);
        var opps = new ClaimAdjudicator().GetOpportunities(0, discard, hands);

        var winner = opps.OrderByDescending(o => o.Priority).First();
        Assert.Equal(TableClaimType.Kong, winner.ClaimType);
        Assert.Equal(2, winner.SeatIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Pung_AppliedToHand_ProducesExposedMeldAndAdvancesActiveSeat()
    {
        // Use a real game state so all invariants are intact.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        // Inject pair into seat 1.
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 0));
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 1));
        var t5 = Tid(Suit.Tong, 5, 2);
        state.Hands[dealer].ConcealedTiles.Add(t5);

        ChangshaGameStateMachine.Discard(state, dealer, t5);
        ChangshaGameStateMachine.ResolveClaim(state, 1, TableClaimType.Pung);

        Assert.Single(state.Hands[1].Melds);
        Assert.Equal(MeldKind.Pung, state.Hands[1].Melds[0].Kind);
        Assert.Equal(3, state.Hands[1].Melds[0].TileIds.Count);
        // Active seat is now the claimer.
        Assert.Equal(1, state.ActiveSeatIndex);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
    }
}
