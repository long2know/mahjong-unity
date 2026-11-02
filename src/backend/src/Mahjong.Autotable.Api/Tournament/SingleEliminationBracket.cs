namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 6 — Bishop. <see cref="IBracketGenerator"/> for
/// single-elimination tournaments. Wraps the existing Wave-J-10
/// <see cref="TournamentPairing.SingleEliminationFirstRound"/>
/// helper so the new factory dispatch keeps the historic bracket
/// shape unchanged.
///
/// <para>Behaviour matches the upstream helper: seeds 1-vs-N,
/// 2-vs-(N-1), …, half-way pairing per the standard tournament-
/// bracket convention. Subsequent rounds are emitted by the
/// service (the factory only owns the first-round shape).</para>
/// </summary>
public sealed class SingleEliminationBracket : IBracketGenerator
{
    public BracketFormat Format => BracketFormat.SingleElimination;

    public IReadOnlyList<BracketPairing> Generate(IReadOnlyList<string> seededPlayers)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        var pairings = TournamentPairing.SingleEliminationFirstRound(seededPlayers);
        var results = new List<BracketPairing>(pairings.Count);
        foreach (var pair in pairings)
        {
            results.Add(new BracketPairing(
                Round: 1,
                Bracket: BracketSide.Winners,
                P1: pair.P1,
                P2: pair.P2,
                P3: pair.P3,
                P4: pair.P4));
        }
        return results;
    }
}

/// <summary>
/// Phase K Wave 6 — Bishop. <see cref="IBracketGenerator"/> for the
/// round-robin schedule. Wraps the existing Wave-J-10 helper so the
/// factory dispatch can compose the same all-pairs schedule.
/// </summary>
public sealed class RoundRobinBracket : IBracketGenerator
{
    public BracketFormat Format => BracketFormat.RoundRobin;

    public IReadOnlyList<BracketPairing> Generate(IReadOnlyList<string> seededPlayers)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        var schedule = TournamentPairing.RoundRobin(seededPlayers);
        var results = new List<BracketPairing>(schedule.Count);
        foreach (var (round, pair) in schedule)
        {
            results.Add(new BracketPairing(
                Round: round,
                Bracket: BracketSide.Winners,
                P1: pair.P1,
                P2: pair.P2,
                P3: pair.P3,
                P4: pair.P4));
        }
        return results;
    }
}
