namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase J Wave 10 — pairing algorithms for the three Wave-10 formats:
/// <c>single-elimination</c>, <c>round-robin</c>, <c>swiss</c>.
///
/// <para>All algorithms are deterministic: given the same seeded
/// ordering of player ids they produce the same pairing list. Seeding
/// is supplied by the caller (the service materialises registrations
/// ordered by their <see cref="Mahjong.Autotable.Api.Data.Entities.TournamentRegistration.Seed"/>
/// column), so tests can pin pairing output for a deterministic player
/// list.</para>
///
/// <para>Wave 10 only generates the first round at start time. For
/// single-elimination + Swiss, follow-on rounds are generated lazily
/// by <see cref="TournamentService.AdvanceMatchAsync"/> once all
/// current-round matches are complete. Round-robin emits every round
/// up-front because the schedule is static.</para>
/// </summary>
public static class TournamentPairing
{
    /// <summary>Pairing record. <c>P3</c>/<c>P4</c> are optional —
    /// 4-player formats fill them, 2-player formats leave them null.</summary>
    public readonly record struct Pairing(string P1, string P2, string? P3, string? P4);

    /// <summary>
    /// Round-robin all-pairs schedule. For <c>n</c> players this
    /// emits <c>n*(n-1)/2</c> 2-player pairings using the circle
    /// method. Returns a flat list of <i>(Round, Pairing)</i> tuples
    /// so the service can persist <see cref="Mahjong.Autotable.Api.Data.Entities.TournamentMatch.Round"/>
    /// directly.
    /// </summary>
    public static IReadOnlyList<(int Round, Pairing Pair)> RoundRobin(IReadOnlyList<string> seededPlayers)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        var n = seededPlayers.Count;
        if (n < 2) return Array.Empty<(int, Pairing)>();

        // Circle method: fix player 0, rotate the rest around it.
        // Even count works directly; odd count gets a "bye" seat which
        // is silently skipped (a real Wave-10 deployment caps at even
        // counts via UI, but the algorithm tolerates odd numbers).
        var players = seededPlayers.ToList();
        if (n % 2 == 1) { players.Add("__bye__"); n++; }

        var results = new List<(int Round, Pairing Pair)>();
        var rounds = n - 1;
        var half = n / 2;
        for (var r = 0; r < rounds; r++)
        {
            for (var i = 0; i < half; i++)
            {
                var a = players[i];
                var b = players[n - 1 - i];
                if (a == "__bye__" || b == "__bye__") continue;
                results.Add((r + 1, new Pairing(a, b, null, null)));
            }
            // Rotate everyone except index 0.
            var last = players[n - 1];
            for (var i = n - 1; i > 1; i--) players[i] = players[i - 1];
            players[1] = last;
        }
        return results;
    }

    /// <summary>
    /// Single-elimination first-round bracket. Seeds 1-vs-N, 2-vs-(N-1),
    /// etc., per the standard tournament-bracket convention. Returns
    /// only the first round; <see cref="TournamentService.AdvanceMatchAsync"/>
    /// schedules subsequent rounds as winners are determined.
    /// </summary>
    public static IReadOnlyList<Pairing> SingleEliminationFirstRound(IReadOnlyList<string> seededPlayers)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        var n = seededPlayers.Count;
        if (n < 2) return Array.Empty<Pairing>();
        // Treat as a complete bracket up to the next power of two; odd
        // counts get a bye round implicitly (a registered player
        // paired against a null opponent). Wave 10 keeps it simple:
        // single-elim assumes power-of-two registrations.
        var half = n / 2;
        var results = new List<Pairing>(half);
        for (var i = 0; i < half; i++)
        {
            var top = seededPlayers[i];
            var bot = seededPlayers[n - 1 - i];
            results.Add(new Pairing(top, bot, null, null));
        }
        return results;
    }

    /// <summary>
    /// Swiss first-round pairing — half-and-half seed match (top half
    /// vs bottom half). Subsequent rounds are paired by current score
    /// (handled in <see cref="TournamentService.PairSwissNextRoundAsync"/>).
    /// Standalone first-round routine kept here so the service body
    /// stays focused on persistence.
    /// </summary>
    public static IReadOnlyList<Pairing> SwissFirstRound(IReadOnlyList<string> seededPlayers)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        var n = seededPlayers.Count;
        if (n < 2) return Array.Empty<Pairing>();
        var half = n / 2;
        var top = seededPlayers.Take(half).ToList();
        var bottom = seededPlayers.Skip(half).Take(half).ToList();
        var results = new List<Pairing>(half);
        for (var i = 0; i < half; i++)
        {
            results.Add(new Pairing(top[i], bottom[i], null, null));
        }
        return results;
    }

    /// <summary>
    /// Buchholz tie-break score: sum of an opponent's match-points.
    /// Used when ranking Swiss-format tournaments where multiple
    /// players share the same win count. The opponent-set is supplied
    /// by the caller (Service iterates the player's match history).
    /// </summary>
    public static int BuchholzScore(IReadOnlyDictionary<string, int> matchPointsByPlayer, IEnumerable<string> opponents)
    {
        ArgumentNullException.ThrowIfNull(matchPointsByPlayer);
        ArgumentNullException.ThrowIfNull(opponents);
        var sum = 0;
        foreach (var o in opponents)
        {
            if (matchPointsByPlayer.TryGetValue(o, out var p)) sum += p;
        }
        return sum;
    }
}
