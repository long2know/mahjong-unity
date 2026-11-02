using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Deal &amp; dice phase per Vasquez rules-diff manifest §1.5 (deal pattern) +
/// MahjongPros §"How to Build the Wall" + Baidu §"摸牌顺序".
///
/// The hand is "playable" only if every Changsha-shape invariant of the opening setup
/// holds: 14/13/13/13 hand distribution, 55 tiles left in the wall, dice-driven break
/// point applied to the deal, no honors/dragons/flowers in the catalog, and every tile
/// accounted for exactly once.
/// </summary>
public class DealAndDicePhaseTests
{
    [Fact, Trait("Category", "Acceptance")]
    public void Deal_DistributesTiles_14_13_13_13()
    {
        // MahjongPros §Deal: dealer ends with 14 tiles (the +1 first-draw), others get 13.
        var state = AcceptanceFixture.NewDealtGame(seed: 11, dealerSeat: 0);

        Assert.Equal(14, state.Hands[state.DealerSeatIndex].ConcealedTiles.Count);
        for (var seat = 0; seat < 4; seat++)
        {
            if (seat == state.DealerSeatIndex) continue;
            Assert.Equal(13, state.Hands[seat].ConcealedTiles.Count);
        }
    }

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Deal_DealerPosition_Receives14_RegardlessOfSeat(int dealerSeat)
    {
        // Vasquez §1.5: the +1 follows the dealer's seat, not a hard-coded seat 0.
        var state = AcceptanceFixture.NewDealtGame(seed: 17, dealerSeat: dealerSeat);

        Assert.Equal(14, state.Hands[dealerSeat].ConcealedTiles.Count);
        var nonDealerCounts = state.Hands
            .Where(h => h.SeatIndex != dealerSeat)
            .Select(h => h.ConcealedTiles.Count)
            .ToList();
        Assert.Equal(new[] { 13, 13, 13 }, nonDealerCounts);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Deal_RemovesDealtTilesFromWall()
    {
        // 108 tiles total − (3 × 13 + 14) = 55 in wall after deal.
        var state = AcceptanceFixture.NewDealtGame(seed: 23);

        Assert.Equal(55, state.Wall.Count);
        var dealtCount = state.Hands.Sum(h => h.ConcealedTiles.Count);
        Assert.Equal(53, dealtCount);
        Assert.Equal(108, state.Wall.Count + dealtCount);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Deal_UsesDiceRollForBreakPoint()
    {
        // MahjongPros §"Breaking the Wall": dealer rolls 2d6 → break point. Replaying the same
        // (seed, dealer) must produce the same break-point — proves the dice roll is actually
        // wired into Deal (not silently bypassed).
        var s1 = AcceptanceFixture.NewDealtGame(seed: 5150, dealerSeat: 0);
        var s2 = AcceptanceFixture.NewDealtGame(seed: 5150, dealerSeat: 0);

        Assert.NotNull(s1.LastDiceRoll);
        Assert.NotNull(s1.BreakPoint);
        Assert.Equal(s1.LastDiceRoll!.Value, s2.LastDiceRoll!.Value);
        Assert.Equal(s1.BreakPoint!.Value.TileIndex, s2.BreakPoint!.Value.TileIndex);
        // Different dealers must produce different break-point seat-wall (sum maps off dealer).
        var s3 = AcceptanceFixture.NewDealtGame(seed: 5150, dealerSeat: 2);
        Assert.NotEqual(s1.BreakPoint.Value.WallIndex, s3.BreakPoint!.Value.WallIndex);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Deal_AllTilesUnique_Across_Wall_And_Hands()
    {
        // Reddit §Tiles: 108 tiles total, every tile id 0..107 exactly once.
        var state = AcceptanceFixture.NewDealtGame(seed: 31415);

        var all = new List<int>();
        foreach (var h in state.Hands) all.AddRange(h.ConcealedTiles);
        all.AddRange(state.Wall);

        Assert.Equal(108, all.Count);
        Assert.Equal(108, all.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 108).ToHashSet(), all.ToHashSet());
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Deal_NoHonorsDragonsFlowers_InCatalog()
    {
        // Baidu §"长沙麻将的牌": 3 suits × 9 ranks × 4 copies. No winds, no dragons, no flowers.
        var catalog = ChangshaDeckBuilder.Build();

        Assert.Equal(108, catalog.Count);
        Assert.All(catalog, t =>
        {
            var suit = ChangshaDeckBuilder.GetSuit(t);
            var rank = ChangshaDeckBuilder.GetRank(t);
            Assert.InRange((int)suit, 0, 2);
            Assert.InRange(rank, 1, 9);
        });
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Deal_DealerIsActiveAndAwaitingDiscard_AfterDeal()
    {
        // Vasquez §1.5 + MahjongPros: dealer holds 14 and must discard first (no draw round).
        var state = AcceptanceFixture.NewDealtGame(seed: 7, dealerSeat: 1);

        Assert.Equal(1, state.ActiveSeatIndex);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(14, state.Hands[1].ConcealedTiles.Count);
    }
}
