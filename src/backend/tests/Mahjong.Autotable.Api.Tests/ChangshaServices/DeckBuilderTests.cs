using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class DeckBuilderTests
{
    [Fact]
    public void Build_Returns108Tiles()
    {
        var tiles = ChangshaDeckBuilder.Build();
        Assert.Equal(108, tiles.Count);
    }

    [Fact]
    public void Build_ContainsTileIds0Through107()
    {
        var tiles = ChangshaDeckBuilder.Build();
        Assert.Equal(Enumerable.Range(0, 108).ToList(), tiles);
    }

    [Fact]
    public void GetSuit_ReturnsCorrectSuit()
    {
        // Tiles 0-35 = Wan (logical 0-8), 36-71 = Tong (logical 9-17), 72-107 = Tiao (logical 18-26)
        Assert.Equal(Suit.Wan, ChangshaDeckBuilder.GetSuit(0));
        Assert.Equal(Suit.Wan, ChangshaDeckBuilder.GetSuit(35));
        Assert.Equal(Suit.Tong, ChangshaDeckBuilder.GetSuit(36));
        Assert.Equal(Suit.Tong, ChangshaDeckBuilder.GetSuit(71));
        Assert.Equal(Suit.Tiao, ChangshaDeckBuilder.GetSuit(72));
        Assert.Equal(Suit.Tiao, ChangshaDeckBuilder.GetSuit(107));
    }

    [Fact]
    public void GetRank_ReturnsCorrectRank()
    {
        // Tile 0-3 = Wan 1, Tile 4-7 = Wan 2, ..., Tile 32-35 = Wan 9
        Assert.Equal(1, ChangshaDeckBuilder.GetRank(0));
        Assert.Equal(1, ChangshaDeckBuilder.GetRank(3));
        Assert.Equal(2, ChangshaDeckBuilder.GetRank(4));
        Assert.Equal(9, ChangshaDeckBuilder.GetRank(35));
    }

    [Fact]
    public void EachSuit_Has36Tiles()
    {
        var tiles = ChangshaDeckBuilder.Build();
        var wan = tiles.Count(t => ChangshaDeckBuilder.GetSuit(t) == Suit.Wan);
        var tong = tiles.Count(t => ChangshaDeckBuilder.GetSuit(t) == Suit.Tong);
        var tiao = tiles.Count(t => ChangshaDeckBuilder.GetSuit(t) == Suit.Tiao);

        Assert.Equal(36, wan);
        Assert.Equal(36, tong);
        Assert.Equal(36, tiao);
    }

    [Fact]
    public void EachRank_Has4CopiesPerSuit()
    {
        var tiles = ChangshaDeckBuilder.Build();
        foreach (var suit in new[] { Suit.Wan, Suit.Tong, Suit.Tiao })
        {
            for (var rank = 1; rank <= 9; rank++)
            {
                var count = tiles.Count(t =>
                    ChangshaDeckBuilder.GetSuit(t) == suit &&
                    ChangshaDeckBuilder.GetRank(t) == rank);
                Assert.Equal(4, count);
            }
        }
    }

    [Fact]
    public void ToTile_ReturnsCorrectTile()
    {
        var tile = ChangshaDeckBuilder.ToTile(40); // logical 10, suit=1(Tong), rank=2
        Assert.Equal(Suit.Tong, tile.Suit);
        Assert.Equal(2, tile.Rank);
    }
}
