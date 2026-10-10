namespace Mahjong.Autotable.Api.Changsha;

public static class ChangshaBaseUnit
{
    // Sixteen canonical hands, each paying at most twelve units to a winner.
    public const int MaxValue = int.MaxValue / (16 * 12);

    public static void Validate(int baseUnit)
    {
        if (baseUnit is < 1 or > MaxValue)
            throw new ArgumentOutOfRangeException(nameof(baseUnit), baseUnit,
                $"Base unit must be between 1 and {MaxValue}.");
    }
}
