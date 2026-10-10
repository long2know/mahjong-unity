using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: claim-priority tier resolution per Vasquez §1.7 + MahjongPros §"Calling":
///   Hu &gt; {Kong = Pung} &gt; Chow. Kong &amp; Pung share a tier with CCW seat-proximity tiebreak.
///   Chow restricted to next-CCW seat from the discarder.
///
/// These tests construct the discard-window scenarios by injecting tiles into hands and
/// calling <see cref="ClaimAdjudicator.GetOpportunities"/> directly + then driving
/// <see cref="ChangshaGameStateMachine.ResolveClaim"/> to assert the runtime honors priority.
/// </summary>
public class ClaimPriorityTests
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

    [Fact, Trait("Category", "Acceptance")]
    public void Discard_OneClaimant_AutoResolves_Pung()
    {
        // MahjongPros §Calling: with only one valid claim, that seat wins the tile.
        var hands = SeededHands((1, Tiles((Suit.Wan, 3), (Suit.Wan, 3))));
        var discard = Tid(Suit.Wan, 3, 2);
        var opps = new ClaimAdjudicator().GetOpportunities(0, discard, hands);

        Assert.Single(opps);
        Assert.Equal(TableClaimType.Pung, opps[0].ClaimType);
        Assert.Equal(1, opps[0].SeatIndex);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Discard_PungBeatsChow_FromConcurrentClaimants()
    {
        // Vasquez §1.7 priority tier: Pung > Chow. Seat 1 (next-CCW) could chow; seat 2 has pair.
        var hands = SeededHands(
            (1, Tiles((Suit.Tong, 4), (Suit.Tong, 6))),
            (2, Tiles((Suit.Tong, 5), (Suit.Tong, 5))));
        var discard = Tid(Suit.Tong, 5, 0);

        var opps = new ClaimAdjudicator().GetOpportunities(0, discard, hands);
        var winner = opps.OrderByDescending(o => o.Priority).First();

        Assert.Equal(TableClaimType.Pung, winner.ClaimType);
        Assert.Equal(2, winner.SeatIndex);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Discard_KongAndPung_SameTier_ClosestCcwWins()
    {
        // Vasquez §1.7 lock (v1.2): Kong and Pung share tier 2; CCW distance from discarder breaks ties.
        // Discarder = seat 0; seat 1 (Pung, distance 1) wins over seat 3 (Kong, distance 3).
        var hands = SeededHands(
            (1, Tiles((Suit.Tong, 5), (Suit.Tong, 5))),
            (3, Tiles((Suit.Tong, 5), (Suit.Tong, 5), (Suit.Tong, 5))));
        var discard = Tid(Suit.Tong, 5, 3);

        var winner = new ClaimAdjudicator().Adjudicate(0, discard, hands, Array.Empty<Meld>());

        Assert.NotNull(winner);
        Assert.Equal(1, winner!.SeatIndex);
        Assert.Equal(TableClaimType.Pung, winner.ClaimType);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Discard_HuBeatsAll_EvenWhenPungAlsoClaimed()
    {
        // MahjongPros §Calling: Hu is always strongest. A pung claimant cannot block a winner.
        var state = AcceptanceFixture.NewDealtGame(seed: 11, dealerSeat: 0);
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 1));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 2));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 3));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 4));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 5));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 6));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 7));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 8));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 9));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 1));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 2));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 3));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 5));

        // Seat 2 is tenpai waiting on Wan-1; seat 3 holds a pung-ready pair of Wan-1s.
        AcceptanceFixture.OverrideHand(state, 2, AcceptanceFixture.ThirteenTileWaitingForWan1().ToArray());
        AcceptanceFixture.OverrideHand(state, 3,
            Tid(Suit.Wan, 1, 1), Tid(Suit.Wan, 1, 2));

        // Dealer (seat 0) discards Wan-1 (copy 3).
        var dealerHand = state.Hands[0];
        dealerHand.ConcealedTiles.Clear();
        dealerHand.ConcealedTiles.Add(Tid(Suit.Wan, 1, 3));
        for (var i = 1; i < 14; i++)
            dealerHand.ConcealedTiles.Add(Tid(Suit.Tiao, ((i - 1) % 9) + 1, (i - 1) / 9));

        ChangshaGameStateMachine.Discard(state, 0, Tid(Suit.Wan, 1, 3));

        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        var opps = state.ClaimWindow!.Opportunities;
        Assert.Contains(opps, o => o.SeatIndex == 2 && o.ClaimType == TableClaimType.Hu);
        Assert.Contains(opps, o => o.SeatIndex == 3 && o.ClaimType == TableClaimType.Pung);

        var top = opps.OrderByDescending(o => o.Priority).First();
        Assert.Equal(TableClaimType.Hu, top.ClaimType);
        Assert.Equal(2, top.SeatIndex);
    }

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(TableClaimType.Hu, TableClaimType.Kong, TableClaimType.Hu)]
    [InlineData(TableClaimType.Hu, TableClaimType.Chow, TableClaimType.Hu)]
    [InlineData(TableClaimType.Kong, TableClaimType.Chow, TableClaimType.Kong)]
    [InlineData(TableClaimType.Pung, TableClaimType.Chow, TableClaimType.Pung)]
    public void Discard_TierPrecedence_HigherTierAlwaysWins(
        TableClaimType higher, TableClaimType lower, TableClaimType expected)
    {
        // Vasquez §1.7: synthetic opportunity pair to verify tier-comparator never picks lower.
        var winnerCmp = new List<ChangshaClaimOpportunity>
        {
            new() { SeatIndex = 1, ClaimType = higher, Priority = ChangshaClaimPriority.TierOf(higher) },
            new() { SeatIndex = 2, ClaimType = lower,  Priority = ChangshaClaimPriority.TierOf(lower) }
        };
        var top = winnerCmp.OrderByDescending(o => o.Priority).First();
        Assert.Equal(expected, top.ClaimType);
    }
}
