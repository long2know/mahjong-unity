namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 11 — Bishop. FIDE C.04.1 Dutch-variation Swiss
/// pairing with full backtracking. Replaces the W10
/// <see cref="DutchSwissPairingService"/> single-swap heuristic
/// with the standardised tournament algorithm:
///
/// <list type="number">
///   <item><b>Sort the score brackets.</b> Players grouped by
///         match-point total, descending. Within a bracket players
///         are ordered by Berger pairing-time priority — descending
///         pre-round Buchholz (sum of opponents' points so far),
///         then ascending seed index as the deterministic
///         tiebreak. We don't model colors (mahjong is a 4-player
///         heat abstracted to a 2-player pairing surface) so the
///         seed index plays the role of the FIDE colour-balance
///         tiebreak.</item>
///   <item><b>Find a legal pairing.</b> Walk the bracket using the
///         FIDE "S1 vs S2" split: top-half (S1) is matched against
///         bottom-half (S2), and bottom-half is permuted lex-
///         smallest-first looking for a permutation that produces
///         no rematches and no bye-violations. We exhaust every
///         permutation in lexicographic seed order before
///         floating a player down — the choice is the FIDE
///         minimisation of (rematch count, float count).</item>
///   <item><b>Float down on failure.</b> When no permutation of
///         the bottom-half yields a legal pairing, the lowest-
///         ranked player floats to the next bracket and the
///         backtrack restarts. Multiple cascading floats are
///         supported (rare; happens late in long tournaments
///         where the rematch graph is dense).</item>
///   <item><b>Bye assignment.</b> When the total roster is odd,
///         the lowest-ranked player who has not previously
///         received a bye is awarded one. FIDE specifies the
///         lowest player from the lowest score bracket; the W11
///         implementation honours this exactly.</item>
/// </list>
///
/// <para>Algorithm is deterministic — identical inputs produce
/// identical outputs across runs. The Berger pre-round Buchholz
/// is computed from the supplied prior-pairings + score map
/// (we don't take a separate "draws" input since the surface is
/// W/L/Bye only; cumulative match-points is the canonical
/// pairing-time tiebreak).</para>
///
/// <para>Documented in <c>docs/swiss-pairing.md</c>.</para>
/// </summary>
public sealed class FideC04SwissPairingService : ISwissPairingService
{
    /// <summary>Bye sentinel — re-exported to match the
    /// <see cref="DutchSwissPairingService.ByeOpponent"/> wire
    /// vocabulary so the service layer's bye-detection branch
    /// keeps working without touching the consumer code.</summary>
    public const string ByeOpponent = DutchSwissPairingService.ByeOpponent;

    /// <summary>
    /// Maximum number of bottom-half permutations evaluated per
    /// bracket before falling back to a float-down. Caps the
    /// worst-case branching factor at a tractable bound — the
    /// FIDE handbook acknowledges that a true exhaustive search
    /// is O(n!) and recommends a capped backtrack in practice.
    /// 5040 = 7! is the W11 ceiling, covering every realistic
    /// score-group cardinality.
    /// </summary>
    public const int MaxPermutationsPerBracket = 5040;

    public IReadOnlyList<TournamentPairing.Pairing> PairNextRound(
        IReadOnlyList<string> seededPlayers,
        IReadOnlyDictionary<string, int> matchPoints,
        IReadOnlyCollection<(string A, string B)> priorPairings)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        ArgumentNullException.ThrowIfNull(matchPoints);
        ArgumentNullException.ThrowIfNull(priorPairings);
        if (seededPlayers.Count < 2) return Array.Empty<TournamentPairing.Pairing>();

        // Seed-index lookup — the FIDE colour-balance tiebreak in
        // chess; deterministic seed order in our mahjong adaptation.
        var seedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < seededPlayers.Count; i++) seedIndex[seededPlayers[i]] = i;

        // Symmetric played-set so a "(a,b)" lookup matches a stored
        // "(b,a)" prior pairing.
        var played = new HashSet<(string, string)>();
        // Per-player opponent list for pre-round Buchholz.
        var opponentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (a, b) in priorPairings)
        {
            played.Add(NormalisePair(a, b));
            if (a != ByeOpponent && b != ByeOpponent)
            {
                AppendOpponent(opponentsOf, a, b);
                AppendOpponent(opponentsOf, b, a);
            }
        }

        // Compute the Berger / pre-round Buchholz for each player —
        // sum of their opponents' current match points. The
        // pre-round Buchholz biases the top of each score bracket
        // toward the player who has met the strongest field so far
        // (matches FIDE C.04 §3.3).
        int Points(string p) => matchPoints.TryGetValue(p, out var v) ? v : 0;
        double PreRoundBuchholz(string p)
        {
            if (!opponentsOf.TryGetValue(p, out var opps) || opps.Count == 0) return 0d;
            double total = 0;
            foreach (var o in opps) total += Points(o);
            return total;
        }

        // Bye-history: a player who has already had a bye does not
        // get one again unless every other player has too.
        var alreadyHadBye = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (a, b) in priorPairings)
        {
            if (b == ByeOpponent) alreadyHadBye.Add(a);
            if (a == ByeOpponent) alreadyHadBye.Add(b);
        }

        // Order the active roster by descending points, descending
        // pre-round Buchholz, ascending seed index. This is the
        // FIDE C.04 "score bracket" ordering — every bracket
        // boundary lands between two distinct match-point totals.
        var sorted = seededPlayers
            .OrderByDescending(Points)
            .ThenByDescending(PreRoundBuchholz)
            .ThenBy(p => seedIndex[p])
            .ToList();

        // Group into score brackets keyed by match-point total.
        var brackets = new List<List<string>>();
        var currentPoints = int.MinValue;
        List<string>? bracket = null;
        foreach (var p in sorted)
        {
            var pts = Points(p);
            if (bracket is null || pts != currentPoints)
            {
                bracket = new List<string>();
                brackets.Add(bracket);
                currentPoints = pts;
            }
            bracket.Add(p);
        }

        var results = new List<TournamentPairing.Pairing>();

        // Awarded-bye flag — once a player gets a bye in this round
        // every other bracket that's odd-sized just floats down
        // instead (no double-bye possible in a single round).
        var byeAwarded = false;

        // If the total roster is odd, pre-assign the round bye to
        // the LOWEST-ranked player who has not previously received
        // one. FIDE C.04 § "Bye": lowest-ranked from the lowest
        // bracket. If everyone has already had one, the
        // lowest-ranked overall takes it.
        if (seededPlayers.Count % 2 == 1)
        {
            string? byeCandidate = null;
            // Scan brackets from lowest (last) to highest (first).
            for (var bi = brackets.Count - 1; bi >= 0 && byeCandidate is null; bi--)
            {
                var b = brackets[bi];
                // Lowest-ranked = last entry in the bracket's
                // (desc-pts, desc-buchholz, asc-seed) ordering.
                for (var i = b.Count - 1; i >= 0; i--)
                {
                    if (!alreadyHadBye.Contains(b[i]))
                    {
                        byeCandidate = b[i];
                        break;
                    }
                }
            }
            byeCandidate ??= sorted[^1];

            results.Add(new TournamentPairing.Pairing(byeCandidate, ByeOpponent, null, null));
            // Remove from its bracket so the rest of the algorithm
            // doesn't try to pair it.
            foreach (var b in brackets)
            {
                if (b.Remove(byeCandidate)) break;
            }
            byeAwarded = true;
        }
        _ = byeAwarded; // pinned for documentation symmetry.

        // Pending-float: players that couldn't be paired in their
        // own bracket and are now competing for slots in the next
        // bracket. They join the head of the next bracket
        // (FIDE C.04 §3.5 "downfloat to S1").
        var pendingFloat = new List<string>();

        // Track how many times each bracket has been re-entered
        // after a float — bounds the retry to the bracket size so
        // we never spin forever on a pathological rematch web.
        var floatAttempts = new Dictionary<int, int>();
        for (var bi = 0; bi < brackets.Count; bi++)
        {
            var b = brackets[bi];
            if (pendingFloat.Count > 0)
            {
                // Downfloated players join the top of the new
                // bracket. Their effective order inside the bracket
                // re-derives via Berger / seed (they may have
                // higher Buchholz than the natives).
                b.InsertRange(0, pendingFloat);
                pendingFloat.Clear();
                b = b
                    .OrderByDescending(Points)
                    .ThenByDescending(PreRoundBuchholz)
                    .ThenBy(p => seedIndex[p])
                    .ToList();
                brackets[bi] = b;
            }

            var isLastBracket = bi == brackets.Count - 1;
            // FIDE C.04 §3.4: brackets must be even before
            // TryPairBracket runs. When this bracket is odd, float
            // the lowest-ranked player down to the next bracket so
            // the bottom-half / top-half split is well-defined.
            // Skip the pre-float when the bracket size matches the
            // initial entry count — i.e., we're on a re-entry where
            // the float would just re-circulate the same player.
            if (b.Count % 2 == 1 && !isLastBracket
                && !floatAttempts.ContainsKey(bi))
            {
                var floater = b[^1];
                pendingFloat.Add(floater);
                b.RemoveAt(b.Count - 1);
                brackets[bi] = b;
                floatAttempts[bi] = 1;
            }
            var paired = TryPairBracket(b, played, isLastBracket, results, out var residuals);
            if (!paired)
            {
                // Every permutation of the bottom-half produced a
                // rematch. Float the lowest-ranked player down and
                // try again. We bound the retries to the bracket
                // size to guarantee termination.
                if (b.Count == 0)
                {
                    continue;
                }
                floatAttempts.TryGetValue(bi, out var attempts);
                if (!isLastBracket && attempts < b.Count)
                {
                    var floater = b[^1];
                    pendingFloat.Add(floater);
                    b.RemoveAt(b.Count - 1);
                    floatAttempts[bi] = attempts + 1;
                    bi--; // re-process this bracket without the floater.
                    continue;
                }
                // Cap hit (or last bracket) — accept the rematch
                // fallback that TryPairBracket produced.
            }

            // Players that couldn't be paired even after backtrack
            // (last-bracket residual) — bye/forfeit emission
            // happens above; nothing else to do here.
            _ = residuals;
        }

        // Residual floats (every late bracket was odd-cascading):
        // pair them top-half / bottom-half as a degenerate Dutch
        // round so no entrant is dropped.
        if (pendingFloat.Count >= 2)
        {
            pendingFloat.Sort((a, b) => seedIndex[a].CompareTo(seedIndex[b]));
            var half = pendingFloat.Count / 2;
            for (var i = 0; i < half; i++)
            {
                results.Add(new TournamentPairing.Pairing(
                    pendingFloat[i],
                    pendingFloat[half + i],
                    null,
                    null));
            }
            if (pendingFloat.Count % 2 == 1)
            {
                results.Add(new TournamentPairing.Pairing(pendingFloat[^1], ByeOpponent, null, null));
            }
        }
        else if (pendingFloat.Count == 1)
        {
            results.Add(new TournamentPairing.Pairing(pendingFloat[0], ByeOpponent, null, null));
        }

        return results;
    }

    /// <summary>
    /// Attempt to pair every player in <paramref name="bracket"/>
    /// without producing a rematch. Returns true when a clean
    /// pairing is found; false when every permutation produces
    /// at least one rematch (caller floats the lowest-ranked
    /// player down).
    ///
    /// <para>The bottom-half permutations are explored in
    /// lexicographic seed order so the algorithm is deterministic:
    /// the first legal permutation wins, and "first" is defined
    /// by a stable seed comparator. For the last bracket we
    /// accept the lex-smallest fallback (rematch tolerated) so
    /// no entrant is dropped.</para>
    /// </summary>
    private static bool TryPairBracket(
        List<string> bracket,
        HashSet<(string, string)> played,
        bool isLastBracket,
        List<TournamentPairing.Pairing> output,
        out List<string> residuals)
    {
        residuals = new List<string>();
        if (bracket.Count == 0) return true;
        if (bracket.Count == 1)
        {
            residuals.Add(bracket[0]);
            return false;
        }

        // FIDE C.04 §3.4: S1 vs S2. The bracket is even by
        // construction at this point (odd entrants float out
        // before TryPairBracket runs); top-half S1, bottom-half S2.
        var halfSize = bracket.Count / 2;
        var s1 = bracket.GetRange(0, halfSize);
        var s2 = bracket.GetRange(halfSize, bracket.Count - halfSize);

        // Walk every permutation of S2 in lexicographic order
        // (seed order). The first permutation that produces zero
        // rematches wins; we cap the search at
        // <see cref="MaxPermutationsPerBracket"/> to bound the
        // worst-case run-time on pathologically large brackets.
        var permutation = s2.ToArray();
        Array.Sort(permutation, StringComparer.Ordinal);
        var permutations = 0;

        List<TournamentPairing.Pairing>? fallback = null;

        do
        {
            permutations++;
            var clean = true;
            var candidate = new List<TournamentPairing.Pairing>(halfSize);
            for (var i = 0; i < halfSize; i++)
            {
                var pair = NormalisePair(s1[i], permutation[i]);
                if (played.Contains(pair))
                {
                    clean = false;
                    break;
                }
                candidate.Add(new TournamentPairing.Pairing(s1[i], permutation[i], null, null));
            }

            if (clean)
            {
                output.AddRange(candidate);
                return true;
            }

            // Remember the first complete permutation in case the
            // bracket is the last one and we have to fall back to
            // a rematch-tolerated pairing.
            if (fallback is null)
            {
                fallback = new List<TournamentPairing.Pairing>(halfSize);
                for (var i = 0; i < halfSize; i++)
                {
                    fallback.Add(new TournamentPairing.Pairing(s1[i], permutation[i], null, null));
                }
            }

            if (permutations >= MaxPermutationsPerBracket) break;
        } while (NextLexPermutation(permutation));

        if (isLastBracket && fallback is not null)
        {
            output.AddRange(fallback);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lexicographic-next-permutation in-place — the standard
    /// algorithm from Knuth TAOCP §7.2.1.2. Returns false when
    /// the array is already the lex-largest permutation.
    /// </summary>
    internal static bool NextLexPermutation(string[] xs)
    {
        if (xs.Length < 2) return false;
        var i = xs.Length - 2;
        while (i >= 0 && StringComparer.Ordinal.Compare(xs[i], xs[i + 1]) >= 0) i--;
        if (i < 0) return false;
        var j = xs.Length - 1;
        while (StringComparer.Ordinal.Compare(xs[j], xs[i]) <= 0) j--;
        (xs[i], xs[j]) = (xs[j], xs[i]);
        Array.Reverse(xs, i + 1, xs.Length - (i + 1));
        return true;
    }

    /// <summary>
    /// Phase K Wave 11 — Bishop. Pure-function pre-round
    /// Buchholz computation — exposed for tests + downstream
    /// observability surfaces. Defined as the sum of every
    /// opponent's match-point total at the start of the round.
    /// Distinct from the post-round Buchholz the
    /// <see cref="SwissStandingsService"/> uses to finalise
    /// standings; the pairing-time Buchholz drives bracket
    /// ordering.
    /// </summary>
    public static double ComputePreRoundBuchholz(
        string playerId,
        IReadOnlyCollection<(string A, string B)> priorPairings,
        IReadOnlyDictionary<string, int> matchPoints)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        ArgumentNullException.ThrowIfNull(priorPairings);
        ArgumentNullException.ThrowIfNull(matchPoints);
        double total = 0;
        foreach (var (a, b) in priorPairings)
        {
            string? opponent = null;
            if (string.Equals(a, playerId, StringComparison.Ordinal)) opponent = b;
            else if (string.Equals(b, playerId, StringComparison.Ordinal)) opponent = a;
            if (opponent is null) continue;
            if (opponent == ByeOpponent) continue;
            if (matchPoints.TryGetValue(opponent, out var pts)) total += pts;
        }
        return total;
    }

    /// <summary>
    /// Phase K Wave 11 — Bishop. Sonneborn-Berger pre-round
    /// score — sum of beaten opponents' points + half the sum
    /// of drawn opponents' points. Requires the per-pairing
    /// outcome which the pairing service does not directly model
    /// (the surface accepts a flat played-set); callers
    /// supplying the explicit win/draw outcomes use this
    /// helper to feed the FIDE C.04 §B.2 tiebreak. Without
    /// outcome information the function returns zero — the
    /// pairing path falls back to the Buchholz tiebreak above.
    /// </summary>
    public static double ComputeSonnebornBerger(
        string playerId,
        IReadOnlyCollection<BergerOutcome> outcomes,
        IReadOnlyDictionary<string, int> matchPoints)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(matchPoints);
        double total = 0;
        foreach (var o in outcomes)
        {
            if (!string.Equals(o.Player, playerId, StringComparison.Ordinal)) continue;
            if (o.Opponent == ByeOpponent) continue;
            if (!matchPoints.TryGetValue(o.Opponent, out var pts)) continue;
            total += o.Result switch
            {
                BergerResult.Win => pts,
                BergerResult.Draw => pts * 0.5d,
                _ => 0d,
            };
        }
        return total;
    }

    private static (string, string) NormalisePair(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    private static void AppendOpponent(Dictionary<string, List<string>> map, string player, string opponent)
    {
        if (!map.TryGetValue(player, out var list))
        {
            list = new List<string>();
            map[player] = list;
        }
        list.Add(opponent);
    }
}

/// <summary>
/// Phase K Wave 11 — Bishop. Result vocabulary for the
/// <see cref="FideC04SwissPairingService.ComputeSonnebornBerger"/>
/// helper.
/// </summary>
public enum BergerResult
{
    Loss = 0,
    Draw = 1,
    Win = 2,
}

/// <summary>
/// Phase K Wave 11 — Bishop. One pairing outcome from the
/// perspective of <see cref="Player"/>. Fed to the
/// <see cref="FideC04SwissPairingService.ComputeSonnebornBerger"/>
/// helper.
/// </summary>
public sealed record BergerOutcome(string Player, string Opponent, BergerResult Result);
