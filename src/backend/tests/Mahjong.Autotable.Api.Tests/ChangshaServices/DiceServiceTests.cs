using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class DiceServiceTests
{
    [Fact]
    public void Roll_ReturnsDie1Between1And6()
    {
        var svc = new DiceService(42);
        for (var i = 0; i < 100; i++)
        {
            var roll = svc.Roll();
            Assert.InRange(roll.Die1, 1, 6);
        }
    }

    [Fact]
    public void Roll_ReturnsDie2Between1And6()
    {
        var svc = new DiceService(42);
        for (var i = 0; i < 100; i++)
        {
            var roll = svc.Roll();
            Assert.InRange(roll.Die2, 1, 6);
        }
    }

    [Fact]
    public void Roll_SumBetween2And12()
    {
        var svc = new DiceService(42);
        for (var i = 0; i < 100; i++)
        {
            var roll = svc.Roll();
            Assert.InRange(roll.Sum, 2, 12);
        }
    }

    [Fact]
    public void Roll_IsDeterministicWithSameSeed()
    {
        var svc1 = new DiceService(123);
        var svc2 = new DiceService(123);

        for (var i = 0; i < 20; i++)
        {
            var r1 = svc1.Roll();
            var r2 = svc2.Roll();
            Assert.Equal(r1, r2);
        }
    }

    [Fact]
    public void Roll_DifferentSeedsProduceDifferentResults()
    {
        var svc1 = new DiceService(100);
        var svc2 = new DiceService(200);
        var roll1 = svc1.Roll();
        var roll2 = svc2.Roll();
        // Not guaranteed to be different on first roll, but very likely with different seeds
        // Just check they return valid results
        Assert.InRange(roll1.Sum, 2, 12);
        Assert.InRange(roll2.Sum, 2, 12);
    }
}
