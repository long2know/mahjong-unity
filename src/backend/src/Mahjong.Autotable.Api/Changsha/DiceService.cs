namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Dice service for Changsha Mahjong. Rolls 2d6 deterministically via seeded RNG.
/// </summary>
public interface IDiceService
{
    DiceRoll Roll();
}

public sealed class DiceService : IDiceService
{
    private readonly Random _rng;

    public DiceService(int seed)
    {
        _rng = new Random(seed);
    }

    public DiceService(Random rng)
    {
        _rng = rng;
    }

    public DiceRoll Roll()
    {
        var die1 = _rng.Next(1, 7);
        var die2 = _rng.Next(1, 7);
        return new DiceRoll(die1, die2);
    }
}
