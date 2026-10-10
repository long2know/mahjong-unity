using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-B: Dice Roll & Break-Point Tests
/// </summary>
public class DiceAndBreakPointTests
{
    [Fact, Trait("Category", "Changsha")]
    public void DiceRoll_DeterministicWithSeed_ProducesSameResults()
    {
        var d1 = new DiceService(seed: 42);
        var d2 = new DiceService(seed: 42);

        for (var i = 0; i < 20; i++)
        {
            var r1 = d1.Roll();
            var r2 = d2.Roll();
            Assert.Equal(r1.Die1, r2.Die1);
            Assert.Equal(r1.Die2, r2.Die2);
            Assert.InRange(r1.Die1, 1, 6);
            Assert.InRange(r1.Die2, 1, 6);
            Assert.InRange(r1.Sum, 2, 12);
        }
    }

    [Fact, Trait("Category", "Changsha")]
    public void BreakPoint_DiceSum2_BreaksAtDealerWall_RightEndCount2()
    {
        var bp = new BreakPointService().ComputeBreakPoint(diceSum: 2, dealerSeatIndex: 0);

        // sum=2 → wallOffset (2-1)%4 = 1 → wall index = (0+1)%4 = 1 (right wall)
        Assert.Equal(1, bp.WallIndex);
        // F1 absolute frame [14,14,13,13]: seat 1 owns a 14-stack wall. Counts 2
        // stacks from its right end → stackIndex = 14 - 2 = 12.
        Assert.Equal(12, bp.StackIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BreakPoint_DiceSum7_BreaksAtOppositeWall()
    {
        var bp = new BreakPointService().ComputeBreakPoint(diceSum: 7, dealerSeatIndex: 0);

        // (7-1)%4 = 2 → opposite wall (index 2)
        Assert.Equal(2, bp.WallIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BreakPoint_AllDiceSums_StayWithinTheirWall()
    {
        var svc = new BreakPointService();
        for (var sum = 2; sum <= 12; sum++)
        {
            var bp = svc.ComputeBreakPoint(sum, dealerSeatIndex: 0);
            Assert.InRange(bp.WallIndex, 0, 3);
            Assert.True(bp.StackIndex >= 0, $"sum={sum} StackIndex={bp.StackIndex}");
            Assert.InRange(bp.TileIndex, 0, 107);
        }
    }

    [Fact, Trait("Category", "Changsha")]
    public void BreakPoint_RotatesWithDealer_DealerSeat2GetsDifferentLayout()
    {
        var bp0 = new BreakPointService().ComputeBreakPoint(diceSum: 5, dealerSeatIndex: 0);
        var bp2 = new BreakPointService().ComputeBreakPoint(diceSum: 5, dealerSeatIndex: 2);

        // Same dice sum but different dealer → different absolute wall.
        Assert.NotEqual(bp0.WallIndex, bp2.WallIndex);
    }
}
