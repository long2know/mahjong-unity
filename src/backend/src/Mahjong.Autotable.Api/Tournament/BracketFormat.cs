namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 6 — Bishop. Tournament bracket format enumeration.
/// Promoted to a typed enum from the Wave-J-10 string column so the
/// new factory + bracket-generator surface can dispatch via a typed
/// contract.
///
/// <para>The string column on <see cref="Data.Entities.Tournament.Format"/>
/// remains the persistence shape — operators and existing schemas
/// stay on the canonical lowercase-hyphen values (<c>"single-elimination"</c>,
/// <c>"round-robin"</c>, <c>"swiss"</c>, <c>"double-elimination"</c>).
/// This enum is the API-side type used by the bracket factory.</para>
/// </summary>
public enum BracketFormat
{
    /// <summary>Single-elimination bracket. Loser of any match drops
    /// out immediately; one round halves the field until one player
    /// remains. Matches the Wave-J-10 <c>"single-elimination"</c>
    /// persistence value.</summary>
    SingleElimination = 0,

    /// <summary>Round-robin all-pairs schedule. Every player plays
    /// every other player exactly once. Matches the Wave-J-10
    /// <c>"round-robin"</c> persistence value.</summary>
    RoundRobin = 1,

    /// <summary>Swiss-system pairing — fixed number of rounds, no
    /// player eliminations, opponents balanced by current score
    /// (no rematches). Matches the Wave-J-10 <c>"swiss"</c>
    /// persistence value.</summary>
    Swiss = 2,

    /// <summary>Double-elimination bracket — winners + losers
    /// brackets feeding a grand final. Players are eliminated only
    /// after losing twice. New in Wave 6; persists as
    /// <c>"double-elimination"</c>.</summary>
    DoubleElimination = 3,
}

/// <summary>
/// Phase K Wave 6 — Bishop. Mapping helpers between
/// <see cref="BracketFormat"/> and the canonical lowercase-hyphen
/// persistence value used by
/// <see cref="Data.Entities.Tournament.Format"/>. Keeping the
/// translation in one place means the rest of the surface (factory,
/// generator, controller) never has to deal with raw strings.
/// </summary>
public static class BracketFormats
{
    public const string SingleEliminationKey = "single-elimination";
    public const string RoundRobinKey = "round-robin";
    public const string SwissKey = "swiss";
    public const string DoubleEliminationKey = "double-elimination";

    /// <summary>Try-parse the canonical persistence value into the
    /// typed enum. Returns false for unknown / null inputs so the
    /// caller can decide whether to raise (controller) or fall
    /// through (test).</summary>
    public static bool TryParse(string? format, out BracketFormat parsed)
    {
        switch (format)
        {
            case SingleEliminationKey:
                parsed = BracketFormat.SingleElimination;
                return true;
            case RoundRobinKey:
                parsed = BracketFormat.RoundRobin;
                return true;
            case SwissKey:
                parsed = BracketFormat.Swiss;
                return true;
            case DoubleEliminationKey:
                parsed = BracketFormat.DoubleElimination;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    public static string ToWire(BracketFormat format) => format switch
    {
        BracketFormat.SingleElimination => SingleEliminationKey,
        BracketFormat.RoundRobin => RoundRobinKey,
        BracketFormat.Swiss => SwissKey,
        BracketFormat.DoubleElimination => DoubleEliminationKey,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown bracket format."),
    };
}
