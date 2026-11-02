using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class BreakPointServiceTests
{
    private readonly BreakPointService _svc = new();

    [Theory]
    [InlineData(2, 0)]
    [InlineData(5, 0)]
    [InlineData(7, 0)]
    [InlineData(12, 0)]
    public void ComputeBreakPoint_ValidDiceSum_ReturnsResult(int diceSum, int dealerSeat)
    {
        var result = _svc.ComputeBreakPoint(diceSum, dealerSeat);
        Assert.InRange(result.WallIndex, 0, 3);
        Assert.True(result.StackIndex >= 0);
        Assert.True(result.TileIndex >= 0);
        Assert.True(result.TileIndex < 108);
    }

    [Fact]
    public void ComputeBreakPoint_DiceSum2_SelectsDealerWall()
    {
        // (2-1) % 4 = 1 → offset 1 from dealer. But per spec:
        // "count: 1=dealer, 2=right..." so sum 2 → count 2 → right
        // With dealer=0: wall offset = (2-1)%4 = 1, wallIndex = (0+1)%4 = 1
        var result = _svc.ComputeBreakPoint(2, 0);
        Assert.Equal(1, result.WallIndex);
    }

    [Fact]
    public void ComputeBreakPoint_DiceSum1_ThrowsForInvalidDiceSum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _svc.ComputeBreakPoint(1, 0));
    }

    [Fact]
    public void ComputeBreakPoint_DiceSum13_ThrowsForInvalidDiceSum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _svc.ComputeBreakPoint(13, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ComputeBreakPoint_AllDealerSeats_ValidResult(int dealerSeat)
    {
        var result = _svc.ComputeBreakPoint(7, dealerSeat);
        Assert.InRange(result.WallIndex, 0, 3);
        Assert.True(result.TileIndex >= 0 && result.TileIndex < 108);
    }
}
