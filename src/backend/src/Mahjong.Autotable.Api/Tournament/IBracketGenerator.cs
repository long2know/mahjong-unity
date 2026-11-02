namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 6 — Bishop. Bracket-generation seam used by the
/// tournament service to produce the first-round (or full-schedule)
/// pairings for a given seeded player list. Concrete implementations
/// — <see cref="SingleEliminationBracket"/>,
/// <see cref="SwissBracket"/>, <see cref="DoubleEliminationBracket"/>,
/// <see cref="RoundRobinBracket"/> — own the per-format logic.
///
/// <para>All generators are deterministic: given the same seeded
/// player list they MUST produce the same pairings on every call.
/// Tests pin this contract per generator
/// (<c>Phase_K_W6/Bishop/BracketGeneratorContractTests</c>).</para>
/// </summary>
public interface IBracketGenerator
{
    /// <summary>The bracket format this generator owns. Used by the
    /// factory dispatch to resolve the right implementation for a
    /// configured tournament.</summary>
    BracketFormat Format { get; }

    /// <summary>
    /// Produces the bracket pairings for the supplied seeded player
    /// list. The returned tuples carry a per-pairing round number so
    /// the caller can persist them directly to
    /// <see cref="Data.Entities.TournamentMatch.Round"/>.
    /// <para>The returned <see cref="BracketPairing.Bracket"/> field
    /// distinguishes winners/losers brackets in double-elimination;
    /// single-elim / Swiss / round-robin always return
    /// <see cref="BracketSide.Winners"/>.</para>
    /// </summary>
    IReadOnlyList<BracketPairing> Generate(IReadOnlyList<string> seededPlayers);
}

/// <summary>
/// Phase K Wave 6 — Bishop. Which side of the bracket a pairing
/// belongs to. Only meaningful for double-elimination; the other
/// formats always emit <see cref="Winners"/>.
/// </summary>
public enum BracketSide
{
    Winners = 0,
    Losers = 1,
    GrandFinal = 2,
}

/// <summary>
/// Phase K Wave 6 — Bishop. Single bracket pairing record. Mirrors
/// the existing <see cref="TournamentPairing.Pairing"/> tuple with
/// the addition of <see cref="Round"/> + <see cref="Bracket"/> so
/// the service can persist a full first-round emission without
/// recombining tuples.
/// </summary>
public readonly record struct BracketPairing(
    int Round,
    BracketSide Bracket,
    string P1,
    string P2,
    string? P3 = null,
    string? P4 = null);
