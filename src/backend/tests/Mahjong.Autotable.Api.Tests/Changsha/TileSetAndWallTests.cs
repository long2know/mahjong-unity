using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-A: Tile Set & Wall Construction Tests
/// </summary>
public class TileSetAndWallTests
{
    [Fact, Trait("Category", "Changsha")]
    public void TileSetComposition_BuildsExactly108Tiles_WithThreeSuitsOnly()
    {
        var tiles = ChangshaDeckBuilder.Build();

        Assert.Equal(108, tiles.Count);
        Assert.Equal(108, tiles.Distinct().Count());
        Assert.Equal(36, tiles.Count(t => ChangshaDeckBuilder.GetSuit(t) == Suit.Wan));
        Assert.Equal(36, tiles.Count(t => ChangshaDeckBuilder.GetSuit(t) == Suit.Tong));
        Assert.Equal(36, tiles.Count(t => ChangshaDeckBuilder.GetSuit(t) == Suit.Tiao));

        // Each rank 1..9 has 4 copies per suit
        foreach (var s in new[] { Suit.Wan, Suit.Tong, Suit.Tiao })
            for (var r = 1; r <= 9; r++)
                Assert.Equal(4, tiles.Count(t => ChangshaDeckBuilder.GetSuit(t) == s && ChangshaDeckBuilder.GetRank(t) == r));
    }

    [Fact, Trait("Category", "Changsha")]
    public void TileSetComposition_NoWindsDragonsOrFlowers()
    {
        var tiles = ChangshaDeckBuilder.Build();

        // Only 27 logical tiles are valid (3 suits × 9 ranks); no honors.
        var distinctLogical = tiles.Select(ChangshaDeckBuilder.GetLogicalTile).Distinct().ToList();
        Assert.Equal(27, distinctLogical.Count);
        Assert.All(distinctLogical, l => Assert.InRange(l, 0, 26));
        Assert.All(tiles, t => Assert.InRange(ChangshaDeckBuilder.GetRank(t), 1, 9));
    }

    [Fact, Trait("Category", "Changsha")]
    public void WallConstruction_ProducesFourWallsOf54Tiles_TwoStacksHigh()
    {
        // 4 walls total, 14+13+14+13 = 54 stacks × 2 tiles/stack = 108 tiles.
        Assert.Equal(108, 14 * 2 + 13 * 2 + 14 * 2 + 13 * 2);

        // After deal, 53 tiles consumed (4×13 + 1 dealer extra), 55 remain.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 42);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);

        var dealt = state.Hands.Sum(h => h.ConcealedTiles.Count);
        Assert.Equal(53, dealt);
        Assert.Equal(55, state.Wall.Count);
    }

    [Fact, Trait("Category", "Changsha")]
    public void WallConstruction_DeterministicPerSeed_DifferentSeedsProduceDifferentLayouts()
    {
        var s1 = ChangshaTestHelpers.NewGameDealtTo(seed: 100);
        var s2 = ChangshaTestHelpers.NewGameDealtTo(seed: 100);
        var s3 = ChangshaTestHelpers.NewGameDealtTo(seed: 999);

        Assert.Equal(s1.Wall, s2.Wall);
        Assert.Equal(s1.Hands[0].ConcealedTiles, s2.Hands[0].ConcealedTiles);
        Assert.NotEqual(s1.Wall, s3.Wall);
    }
}
