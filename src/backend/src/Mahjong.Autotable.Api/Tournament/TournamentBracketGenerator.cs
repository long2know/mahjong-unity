namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 6 — Bishop. Factory that resolves the
/// <see cref="IBracketGenerator"/> implementation for a given
/// <see cref="BracketFormat"/>. Registered as a singleton; the
/// underlying generators are pure functions over the player list
/// so a single shared instance is correct.
///
/// <para>The factory accepts either a typed <see cref="BracketFormat"/>
/// or the persistence string. Unknown formats throw
/// <see cref="ArgumentOutOfRangeException"/> so callers branch on a
/// hard signal rather than a silent fallthrough.</para>
/// </summary>
public sealed class TournamentBracketGenerator
{
    private readonly Dictionary<BracketFormat, IBracketGenerator> _byFormat;

    public TournamentBracketGenerator(IEnumerable<IBracketGenerator> generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        _byFormat = generators.ToDictionary(g => g.Format);
    }

    /// <summary>Convenience ctor that wires up the canonical Wave-6
    /// implementation set. Used by tests + by the DI default when no
    /// explicit set is configured.</summary>
    public static TournamentBracketGenerator CreateDefault() =>
        new(new IBracketGenerator[]
        {
            new SingleEliminationBracket(),
            new SwissBracket(),
            new DoubleEliminationBracket(),
            new RoundRobinBracket(),
        });

    /// <summary>Resolves the generator for the typed
    /// <paramref name="format"/>. Throws on unknown.</summary>
    public IBracketGenerator Resolve(BracketFormat format)
    {
        if (_byFormat.TryGetValue(format, out var gen)) return gen;
        throw new ArgumentOutOfRangeException(nameof(format), format,
            $"No bracket generator registered for format {format}.");
    }

    /// <summary>Resolves the generator for the supplied persistence
    /// string (e.g. <c>"swiss"</c>). Throws on unknown.</summary>
    public IBracketGenerator Resolve(string format)
    {
        if (!BracketFormats.TryParse(format, out var parsed))
            throw new ArgumentOutOfRangeException(nameof(format), format,
                "Unknown tournament format string.");
        return Resolve(parsed);
    }
}
