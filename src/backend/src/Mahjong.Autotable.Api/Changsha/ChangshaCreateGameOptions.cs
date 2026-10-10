namespace Mahjong.Autotable.Api.Changsha;

public sealed class ChangshaCreateGameOptions
{
    public string RuleSet { get; init; } = "changsha-v1";
    public int[]? BotSeatIndexes { get; init; }
    public int? Seed { get; init; }
    /// <summary>Creation-only multiplier in [1, ChangshaBaseUnit.MaxValue].</summary>
    public int BaseUnit { get; init; } = 1;
}
