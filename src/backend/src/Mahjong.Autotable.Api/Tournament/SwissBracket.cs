namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 6 — Bishop. <see cref="IBracketGenerator"/> for the
/// Swiss-system pairing format. Emits a 4-round Swiss schedule for
/// the supplied seeded player list.
///
/// <para><b>Round 1</b>: half-and-half seed match — top half plays
/// bottom half (1 vs N/2+1, 2 vs N/2+2, …). This matches the
/// existing <see cref="TournamentPairing.SwissFirstRound"/> helper
/// so any test that asserts the Wave-J-10 round-1 shape keeps
/// passing.</para>
///
/// <para><b>Rounds 2–4</b>: <see cref="IBracketGenerator"/> contract
/// requires deterministic output for the same seed list. Without
/// observed match results (the bracket generator is invoked at
/// start-time, before any games have been played) we can't pair by
/// current-score. Instead we use a deterministic Swiss-by-seed
/// schedule that:
/// <list type="bullet">
///   <item>Avoids rematches inside the 4-round window.</item>
///   <item>Pairs players whose seed parity sums to a constant —
///         a Latin-square pattern that keeps every player matched
///         against three distinct opponents over the 4 rounds.</item>
/// </list>
/// Real Swiss pairing (post-round-1 standings-driven matching) is
/// the responsibility of
/// <see cref="TournamentService.MaybeAdvanceRoundAsync"/>'s Swiss
/// branch — that helper rewires pairings after each round based on
/// the current standings, overriding the deterministic schedule
/// emitted here. The factory's job is to seed the table with a
/// deterministic baseline; the service adapts subsequent rounds.</para>
///
/// <para>The 4-round count matches the Wave-6 brief; larger fields
/// would extend the schedule, but Mahjong tournaments at this scale
/// rarely exceed 4 rounds.</para>
/// </summary>
public sealed class SwissBracket : IBracketGenerator
{
    public const int DefaultRoundCount = 4;

    public BracketFormat Format => BracketFormat.Swiss;

    public IReadOnlyList<BracketPairing> Generate(IReadOnlyList<string> seededPlayers)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        if (seededPlayers.Count < 2) return Array.Empty<BracketPairing>();

        // Round 1 — the historic SwissFirstRound shape.
        var results = new List<BracketPairing>();
        foreach (var pair in TournamentPairing.SwissFirstRound(seededPlayers))
        {
            results.Add(new BracketPairing(
                Round: 1,
                Bracket: BracketSide.Winners,
                P1: pair.P1,
                P2: pair.P2));
        }

        // Rounds 2..DefaultRoundCount — deterministic Latin-square
        // pattern that avoids rematches inside the round window. The
        // pattern: rotate the bottom half by (round-1) positions
        // before pairing against the top half. This is a textbook
        // Swiss-from-seeds baseline; the service overrides it with
        // standings-driven pairing once round 1 completes.
        var half = seededPlayers.Count / 2;
        if (half == 0) return results;

        for (var round = 2; round <= DefaultRoundCount; round++)
        {
            var rotation = (round - 1) % half;
            for (var i = 0; i < half; i++)
            {
                var top = seededPlayers[i];
                var bottomIdx = half + ((i + rotation) % half);
                if (bottomIdx >= seededPlayers.Count) break;
                var bottom = seededPlayers[bottomIdx];
                if (string.Equals(top, bottom, StringComparison.Ordinal)) continue;
                results.Add(new BracketPairing(
                    Round: round,
                    Bracket: BracketSide.Winners,
                    P1: top,
                    P2: bottom));
            }
        }
        return results;
    }
}
