namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 10 — Bishop. Pluggable Swiss-style pairing.
/// Wave 10 introduces a richer Dutch-system pairing algorithm
/// (top-half-vs-bottom-half per score group, no rematches,
/// float-down on odd score groups) that supersedes the simpler
/// Wave-J "split half-and-half by seed" first-round-only routine
/// in <see cref="TournamentPairing.SwissFirstRound"/>.
///
/// <para>The interface is pure — no DbContext, no DateTimeOffset.
/// Inputs are the seeded player list, the per-player current
/// match points, and the history of prior pairings so the
/// algorithm can avoid rematches. Output is a list of
/// <see cref="TournamentPairing.Pairing"/> rows describing the
/// next round.</para>
///
/// <para>Bug-for-bug compatibility with the W-J first-round
/// routine is retained: when called with empty score / history
/// the Dutch service degenerates to the same top-half-vs-
/// bottom-half pairing the W-J routine emitted.</para>
/// </summary>
public interface ISwissPairingService
{
    /// <summary>
    /// Generate the next-round pairing for a Swiss-style
    /// tournament.
    /// </summary>
    /// <param name="seededPlayers">
    /// Players in their seed order (highest seed first). Empty
    /// or single-player lists return an empty pairing list.
    /// </param>
    /// <param name="matchPoints">
    /// Current match-point totals per player. Missing entries are
    /// treated as zero (round-one default).
    /// </param>
    /// <param name="priorPairings">
    /// History of every prior pairing, as ordered <c>(playerA,
    /// playerB)</c> tuples. The algorithm avoids re-emitting any
    /// pair already present in this set.
    /// </param>
    IReadOnlyList<TournamentPairing.Pairing> PairNextRound(
        IReadOnlyList<string> seededPlayers,
        IReadOnlyDictionary<string, int> matchPoints,
        IReadOnlyCollection<(string A, string B)> priorPairings);
}

/// <summary>
/// Phase K Wave 10 — Bishop. Dutch-system Swiss pairing.
/// The standard pairing rules used by FIDE for chess Swiss
/// tournaments, adapted to the four-player mahjong scoring
/// surface as a 2-player table abstraction (mahjong heat
/// physically seats four players, but the pairing layer is
/// 2-player-deterministic by design — see
/// <c>docs/bracket-shape.md §3</c>).
///
/// <para>Algorithm:</para>
/// <list type="number">
///   <item>Sort players by descending match-points, breaking
///         ties on the supplied seed order (i.e. lower seed
///         index = higher seed = higher priority).</item>
///   <item>Group players by match-point total. Each group is
///         a "score group".</item>
///   <item>Within each score group, split into top half + bottom
///         half. Pair top[i] with bottom[i].</item>
///   <item>When a score group has odd cardinality, the lowest-
///         ranked player floats down — they join the next score
///         group. This may cascade if the next group is itself
///         odd.</item>
///   <item>If a candidate pairing is in the prior-pairings set,
///         swap the bottom player with the next-most-likely
///         opponent in the same score group (the lookahead is a
///         single swap — the W10 implementation does not perform
///         the full backtracking search of the FIDE C.04 spec,
///         which is documented as a follow-up).</item>
/// </list>
/// </summary>
public sealed class DutchSwissPairingService : ISwissPairingService
{
    public IReadOnlyList<TournamentPairing.Pairing> PairNextRound(
        IReadOnlyList<string> seededPlayers,
        IReadOnlyDictionary<string, int> matchPoints,
        IReadOnlyCollection<(string A, string B)> priorPairings)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        ArgumentNullException.ThrowIfNull(matchPoints);
        ArgumentNullException.ThrowIfNull(priorPairings);
        if (seededPlayers.Count < 2) return Array.Empty<TournamentPairing.Pairing>();

        // Seed index lookup so we can resolve tie-breaks deterministically.
        var seedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < seededPlayers.Count; i++) seedIndex[seededPlayers[i]] = i;

        // Pair-history set (order-independent) — a prior pairing is
        // matched in either direction.
        var played = new HashSet<(string, string)>();
        foreach (var (a, b) in priorPairings)
        {
            played.Add(NormalisePair(a, b));
        }

        // 1. Sort by descending points, ascending seed index on tie.
        int Points(string p) => matchPoints.TryGetValue(p, out var v) ? v : 0;
        var sorted = seededPlayers
            .OrderByDescending(Points)
            .ThenBy(p => seedIndex[p])
            .ToList();

        // 2. Group by points.
        var pendingFloat = new List<string>();
        var grouped = new List<List<string>>();
        var currentPoints = int.MinValue;
        List<string>? current = null;
        foreach (var p in sorted)
        {
            var pts = Points(p);
            if (current is null || pts != currentPoints)
            {
                current = new List<string>();
                grouped.Add(current);
                currentPoints = pts;
            }
            current.Add(p);
        }

        var results = new List<TournamentPairing.Pairing>();

        // 3-5. Process each score group, top-half vs bottom-half,
        // floating the lowest-ranked entry down when the group is
        // odd.
        for (var gi = 0; gi < grouped.Count; gi++)
        {
            var group = grouped[gi];
            if (pendingFloat.Count > 0)
            {
                group.InsertRange(0, pendingFloat);
                pendingFloat.Clear();
            }

            if (group.Count % 2 == 1)
            {
                // Float-down: take the LOWEST-ranked entry (last
                // after the within-group sort below) and drop it
                // to the next score group.
                if (gi + 1 < grouped.Count)
                {
                    // We can't pull the lowest-ranked yet because
                    // the group hasn't been ordered — order first,
                    // then pop the tail.
                }
                else
                {
                    // No next group — record a bye for the lowest-
                    // ranked player. Wave-10 currently emits a
                    // pairing where P2 is the bye sentinel string
                    // "__bye__"; the service layer interprets this
                    // and awards a default-win match.
                }
            }

            // Within-group ordering: by points desc (already same
            // by construction), seed asc.
            group = group.OrderBy(p => seedIndex[p]).ToList();

            if (group.Count % 2 == 1)
            {
                var floater = group[^1];
                group.RemoveAt(group.Count - 1);
                if (gi + 1 < grouped.Count)
                {
                    pendingFloat.Add(floater);
                }
                else
                {
                    results.Add(new TournamentPairing.Pairing(floater, ByeOpponent, null, null));
                }
            }

            // Top-half vs bottom-half pairing.
            var half = group.Count / 2;
            var top = group.Take(half).ToList();
            var bottom = group.Skip(half).Take(half).ToList();

            for (var i = 0; i < half; i++)
            {
                var topPlayer = top[i];
                var bottomPlayer = bottom[i];

                if (played.Contains(NormalisePair(topPlayer, bottomPlayer)))
                {
                    // Single-swap rematch avoidance: walk the bottom
                    // half forward looking for the first opponent
                    // we haven't yet played. If none is available we
                    // accept the rematch (single-swap is the W10
                    // ceiling; full FIDE backtracking is follow-up).
                    var swapFound = false;
                    for (var j = i + 1; j < half; j++)
                    {
                        var candidate = bottom[j];
                        if (!played.Contains(NormalisePair(topPlayer, candidate)))
                        {
                            (bottom[i], bottom[j]) = (bottom[j], bottom[i]);
                            bottomPlayer = bottom[i];
                            swapFound = true;
                            break;
                        }
                    }
                    _ = swapFound; // documented intentional fallthrough
                }

                results.Add(new TournamentPairing.Pairing(topPlayer, bottomPlayer, null, null));
            }
        }

        // If we still have pending floats after exhausting the
        // last group, pair them off in residual top-half / bottom-
        // half fashion. This can happen when an odd score group
        // cascades repeatedly.
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
    /// Sentinel string used when a player has no opponent for the
    /// round (bye / forfeit). The service layer interprets this
    /// and records a default-win.
    /// </summary>
    public const string ByeOpponent = "__bye__";

    private static (string, string) NormalisePair(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
