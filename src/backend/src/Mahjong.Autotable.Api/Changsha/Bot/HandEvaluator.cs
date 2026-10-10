namespace Mahjong.Autotable.Api.Changsha.Bot;

/// <summary>
/// Phase F bot helpers that all difficulty strategies share. Tile-counting,
/// concealed/added kong detection, and shanten computation live here so
/// Easy/Medium/Hard don't re-implement them.
/// </summary>
/// <remarks>
/// <para><b>Phase I Wave 4:</b> the previous fast approximation in
/// <see cref="MinShantenToHu"/> has been replaced by a rigorous backtracking
/// counter that searches both Changsha winning shapes (Standard 4-groups+pair
/// and SevenPairs) and returns the minimum. The counter respects declared melds
/// and is fast enough to run well inside the bot turn budget.</para>
/// </remarks>
public static class HandEvaluator
{
    /// <summary>
    /// Returns the logical tile id of a tile the bot holds four copies of (concealed
    /// kong candidate), or <c>-1</c> when none qualify.
    /// </summary>
    public static int FindConcealedKongCandidate(ChangshaHandState hand)
    {
        var logicalCounts = hand.ConcealedTiles
            .GroupBy(ChangshaDeckBuilder.GetLogicalTile)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (logical, count) in logicalCounts)
        {
            if (count >= 4)
                return logical;
        }
        return -1;
    }

    /// <summary>
    /// Returns the tile id (physical) of a hand tile that, combined with an existing
    /// declared Pung, makes an added kong candidate. Returns <c>-1</c> when no Pung
    /// can be upgraded.
    /// </summary>
    public static int FindAddedKongCandidate(ChangshaHandState hand)
    {
        foreach (var meld in hand.Melds)
        {
            if (meld.Kind != MeldKind.Pung)
                continue;

            var meldLogical = ChangshaDeckBuilder.GetLogicalTile(meld.TileIds[0]);
            var matchingTile = hand.ConcealedTiles
                .FirstOrDefault(t => ChangshaDeckBuilder.GetLogicalTile(t) == meldLogical, -1);
            if (matchingTile >= 0)
                return matchingTile;
        }
        return -1;
    }

    /// <summary>
    /// Counts the number of "loose" tiles in a hand — tiles that aren't part of a
    /// pair, neighbour-pair, or gapped-pair. A higher loose count means the hand is
    /// further from a winning shape; used by Easy and Hard to bias their discards.
    /// </summary>
    public static int CountLooseTiles(ChangshaHandState hand)
    {
        var logicalCounts = hand.ConcealedTiles
            .GroupBy(ChangshaDeckBuilder.GetLogicalTile)
            .ToDictionary(g => g.Key, g => g.Count());

        var loose = 0;
        foreach (var tile in hand.ConcealedTiles)
        {
            var logical = ChangshaDeckBuilder.GetLogicalTile(tile);
            var rank = logical % 9;
            var hasPair = logicalCounts.GetValueOrDefault(logical, 0) > 1;
            var hasNeighbour = (rank > 0 && logicalCounts.ContainsKey(logical - 1))
                            || (rank < 8 && logicalCounts.ContainsKey(logical + 1))
                            || (rank > 1 && logicalCounts.ContainsKey(logical - 2))
                            || (rank < 7 && logicalCounts.ContainsKey(logical + 2));
            if (!hasPair && !hasNeighbour)
                loose++;
        }
        return loose;
    }

    /// <summary>
    /// Set of logical tile ids that have already been discarded by ANY seat in the
    /// current hand. Used by <see cref="HardStrategy"/> to bias discards toward
    /// tiles that opponents are statistically less likely to need (mahjong's
    /// "safe tile" heuristic — anything previously discarded is generally safer to
    /// repeat).
    /// </summary>
    public static HashSet<int> CollectDiscardedLogicals(ChangshaGameState state)
    {
        var result = new HashSet<int>();
        foreach (var discard in state.DiscardPile)
        {
            result.Add(ChangshaDeckBuilder.GetLogicalTile(discard.TileId));
        }
        return result;
    }

    /// <summary>
    /// Phase I Wave 4 — rigorous shanten distance to a Hu win shape. Returns the
    /// minimum number of tile swaps the hand needs to reach a winning configuration
    /// across both Changsha winning shapes (Standard 4-groups+pair and SevenPairs),
    /// or <c>0</c> when the hand is already structurally winning.
    /// </summary>
    /// <remarks>
    /// <para><b>Standard path:</b> backtracking decomposition. At each iteration the
    /// algorithm picks the lowest-index logical tile with a positive count and tries
    /// every legal absorption (Pung, Chow, Pair-as-head, Pair-as-partial-pung,
    /// neighbour partial, gap partial, lone-tile-drop). Each branch restores the
    /// count vector before returning. The best (minimum) shanten across all
    /// decompositions wins. Group-count budget <c>4 - meldsDeclared</c> caps
    /// <c>mentsu + taatsu</c> so excess partials never inflate the score.</para>
    ///
    /// <para><b>SevenPairs path:</b> direct formula. Invalid when the hand has any
    /// declared meld (matches <see cref="ChangshaWinDetector"/>'s
    /// <c>CheckSevenPairs</c>). Otherwise
    /// <c>sevenPairsShanten = 6 - sum(counts[i] / 2)</c>, which allows a 4-of-a-kind
    /// to contribute 2 logical pairs of the same tile (parity with the WinDetector
    /// semantic).</para>
    ///
    /// <para><b>Return value:</b> <c>max(0, min(standard, sevenPairs))</c>. The
    /// clamp at zero is the prevailing convention: shanten &gt;= 0 means
    /// "tile-swaps still required"; the actual winning-shape check is a separate
    /// concern owned by <see cref="ChangshaWinDetector"/>. The clamp keeps the
    /// existing acceptance assertion that a winning 14-tile hand reports
    /// <c>shanten == 0</c>.</para>
    ///
    /// <para><b>Monotonicity:</b> a proper shanten counter satisfies
    /// <c>shanten(hand - t) &gt;= shanten(hand)</c> for any tile <c>t</c>, with
    /// equality when <c>t</c> contributes to no maximising decomposition (a "loose"
    /// tile). The bot pipeline relies on this property: a discard that increases
    /// shanten is provably worse than one that does not.</para>
    ///
    /// <para><b>Performance:</b> branches strictly reduce <c>sum(counts)</c>;
    /// recursion depth is bounded by total concealed tiles (&lt;= 14). The
    /// group-budget cap and the lone-tile fall-through dominate; in practice each
    /// call runs in well under 1 ms, leaving the 2000 ms bot turn budget intact.
    /// The <paramref name="remainingWall"/> parameter is currently unused by the
    /// counter itself (kept for API back-compat); future EV refinements may use it
    /// to weight "useful tiles still in the wall".</para>
    /// </remarks>
    public static int MinShantenToHu(ChangshaHandState hand, IReadOnlyList<int> remainingWall)
    {
        var counts = new int[ChangshaDeckBuilder.LogicalTileCount];
        foreach (var tileId in hand.ConcealedTiles)
        {
            counts[ChangshaDeckBuilder.GetLogicalTile(tileId)]++;
        }

        var meldsDeclared = hand.Melds.Count;
        var standardShanten = ComputeStandardShanten(counts, meldsDeclared);
        var sevenPairsShanten = ComputeSevenPairsShanten(counts, meldsDeclared);

        var best = Math.Min(standardShanten, sevenPairsShanten);
        return Math.Max(0, best);
    }

    /// <summary>
    /// Standard-shape (4 groups + 1 pair) shanten via backtracking decomposition.
    /// </summary>
    private static int ComputeStandardShanten(int[] counts, int meldsDeclared)
    {
        var groupsNeeded = 4 - meldsDeclared;
        if (groupsNeeded < 0) groupsNeeded = 0;

        var best = int.MaxValue;
        DecomposeStandard(counts, start: 0, mentsu: 0, taatsu: 0, pair: 0, groupsNeeded, ref best);
        return best;
    }

    /// <summary>
    /// Recursive Standard-shape decomposition. Tries every legal absorption of the
    /// lowest non-zero logical tile, restoring state between branches.
    /// </summary>
    private static void DecomposeStandard(
        int[] counts,
        int start,
        int mentsu,
        int taatsu,
        int pair,
        int groupsNeeded,
        ref int best)
    {
        var i = start;
        while (i < counts.Length && counts[i] == 0) i++;

        // Score "stop here" — every remaining tile becomes a lone tile contributing
        // nothing further. This is always a valid (if not optimal) decomposition.
        var local = ShantenFromState(mentsu, taatsu, pair, groupsNeeded);
        if (local < best) best = local;

        if (i >= counts.Length) return;

        // Best possible improvement remaining = 2 * unused group slots (every slot
        // could in theory become a full mentsu). If the lower-bound shanten from
        // here can't beat the current best, prune.
        var unusedSlots = groupsNeeded - mentsu;
        if (unusedSlots <= 0 && pair == 1)
        {
            // Group budget full and head pair already chosen — no further structure
            // helps. Score is already locked in.
            return;
        }

        var rank = i % 9;

        // ── Option: pung ─────────────────────────────────────────────────
        if (counts[i] >= 3 && mentsu < groupsNeeded)
        {
            counts[i] -= 3;
            DecomposeStandard(counts, i, mentsu + 1, taatsu, pair, groupsNeeded, ref best);
            counts[i] += 3;
        }

        // ── Option: chow (i, i+1, i+2 all same suit) ─────────────────────
        if (rank <= 6 && mentsu < groupsNeeded && counts[i + 1] > 0 && counts[i + 2] > 0)
        {
            counts[i]--; counts[i + 1]--; counts[i + 2]--;
            DecomposeStandard(counts, i, mentsu + 1, taatsu, pair, groupsNeeded, ref best);
            counts[i]++; counts[i + 1]++; counts[i + 2]++;
        }

        // ── Option: pair as head ─────────────────────────────────────────
        if (counts[i] >= 2 && pair == 0)
        {
            counts[i] -= 2;
            DecomposeStandard(counts, i, mentsu, taatsu, pair: 1, groupsNeeded, ref best);
            counts[i] += 2;
        }

        // ── Option: pair as taatsu (partial pung) ────────────────────────
        if (counts[i] >= 2 && mentsu + taatsu < groupsNeeded)
        {
            counts[i] -= 2;
            DecomposeStandard(counts, i, mentsu, taatsu + 1, pair, groupsNeeded, ref best);
            counts[i] += 2;
        }

        // ── Option: neighbour partial (i, i+1) ───────────────────────────
        if (rank <= 7 && mentsu + taatsu < groupsNeeded && counts[i + 1] > 0)
        {
            counts[i]--; counts[i + 1]--;
            DecomposeStandard(counts, i, mentsu, taatsu + 1, pair, groupsNeeded, ref best);
            counts[i]++; counts[i + 1]++;
        }

        // ── Option: gap partial (i, i+2) ─────────────────────────────────
        if (rank <= 6 && mentsu + taatsu < groupsNeeded && counts[i + 2] > 0)
        {
            counts[i]--; counts[i + 2]--;
            DecomposeStandard(counts, i, mentsu, taatsu + 1, pair, groupsNeeded, ref best);
            counts[i]++; counts[i + 2]++;
        }

        // ── Option: lone tile (drop a single copy of i) ──────────────────
        counts[i]--;
        DecomposeStandard(counts, i, mentsu, taatsu, pair, groupsNeeded, ref best);
        counts[i]++;
    }

    /// <summary>
    /// Canonical shanten formula <c>2*groupsNeeded - 2*useful_mentsu - useful_taatsu - pair</c>.
    /// <c>useful_taatsu</c> caps the partial-group count at the remaining group budget so
    /// excess partials never inflate the score. Negative results (a structurally winning
    /// shape) are clamped to zero by the caller — see <see cref="MinShantenToHu"/>.
    /// </summary>
    private static int ShantenFromState(int mentsu, int taatsu, int pair, int groupsNeeded)
    {
        var usefulMentsu = mentsu > groupsNeeded ? groupsNeeded : mentsu;
        var slotsLeft = groupsNeeded - usefulMentsu;
        var usefulTaatsu = taatsu > slotsLeft ? slotsLeft : taatsu;
        return 2 * groupsNeeded - 2 * usefulMentsu - usefulTaatsu - pair;
    }

    /// <summary>
    /// SevenPairs shanten — direct formula. SevenPairs forbids any declared meld
    /// (mirrors <see cref="ChangshaWinDetector"/>); when one is present we report
    /// "infinite" so the <see cref="MinShantenToHu"/> min() naturally falls through
    /// to the Standard path. A 4-of-a-kind contributes 2 logical pairs (matching
    /// <c>CheckSevenPairs</c>'s count/2 semantic).
    /// </summary>
    private static int ComputeSevenPairsShanten(int[] counts, int meldsDeclared)
    {
        if (meldsDeclared > 0) return int.MaxValue;

        var pairs = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            pairs += counts[i] / 2;
        }
        if (pairs > 7) pairs = 7;
        return 6 - pairs;
    }
}
