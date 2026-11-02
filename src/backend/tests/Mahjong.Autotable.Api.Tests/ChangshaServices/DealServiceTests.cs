using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class DealServiceTests
{
    private readonly DealService _svc = new();

    [Fact]
    public void Deal_DealerGets14Tiles()
    {
        var wall = ChangshaDeckBuilder.Build();
        var result = _svc.Deal(wall, dealerSeatIndex: 0);
        Assert.Equal(14, result.Hands[0].Count);
    }

    [Fact]
    public void Deal_NonDealersGet13Tiles()
    {
        var wall = ChangshaDeckBuilder.Build();
        var result = _svc.Deal(wall, dealerSeatIndex: 0);
        Assert.Equal(13, result.Hands[1].Count);
        Assert.Equal(13, result.Hands[2].Count);
        Assert.Equal(13, result.Hands[3].Count);
    }

    [Fact]
    public void Deal_Remaining55Tiles()
    {
        var wall = ChangshaDeckBuilder.Build();
        var result = _svc.Deal(wall, dealerSeatIndex: 0);
        Assert.Equal(55, result.RemainingWall.Count);
    }

    [Fact]
    public void Deal_TotalTilesConserved()
    {
        var wall = ChangshaDeckBuilder.Build();
        var result = _svc.Deal(wall, dealerSeatIndex: 0);
        var total = result.Hands.Sum(h => h.Count) + result.RemainingWall.Count;
        Assert.Equal(108, total);
    }

    [Fact]
    public void Deal_AllTilesUnique()
    {
        var wall = ChangshaDeckBuilder.Build();
        var result = _svc.Deal(wall, dealerSeatIndex: 0);
        var allTiles = result.Hands.SelectMany(h => h).Concat(result.RemainingWall).ToList();
        Assert.Equal(108, allTiles.Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Deal_AnyDealerSeat_DealerGets14(int dealerSeat)
    {
        var wall = ChangshaDeckBuilder.Build();
        var result = _svc.Deal(wall, dealerSeat);
        Assert.Equal(14, result.Hands[dealerSeat].Count);
        for (var i = 0; i < 4; i++)
        {
            if (i != dealerSeat)
                Assert.Equal(13, result.Hands[i].Count);
        }
    }

    [Fact]
    public void Deal_WrongWallSize_Throws()
    {
        var wall = Enumerable.Range(0, 100).ToList();
        Assert.Throws<ArgumentException>(() => _svc.Deal(wall, 0));
    }
}
