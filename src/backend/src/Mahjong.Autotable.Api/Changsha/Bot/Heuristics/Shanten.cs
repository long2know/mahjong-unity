namespace Mahjong.Autotable.Api.Changsha.Bot.Heuristics;

/// <summary>
/// Public façade for the rigorous shanten counter that lives in
/// <see cref="HandEvaluator.MinShantenToHu"/>. Exists so the bot
/// heuristics layer has a discoverable, documented entry point with
/// the conventional name (the Japanese / mahjong-literature term is
/// "shanten" — number of tile swaps to tenpai).
///
/// <para>The underlying implementation is a backtracking decomposition
/// across both Changsha winning shapes (Standard 4-groups + pair and
/// SevenPairs) and respects declared melds. See
/// <see cref="HandEvaluator.MinShantenToHu"/> for the algorithm
/// rationale and complexity notes.</para>
///
/// <para><b>Conventions.</b>
/// <list type="bullet">
///   <item><b>0 = tenpai</b>: the hand is one tile away from a win (or
///         already winning — the clamp at zero matches the prevailing
///         literature convention and the existing test contract).</item>
///   <item><b>1 = one shanten</b>: two effective draws away (one to
///         tenpai, one to win).</item>
///   <item><b>Monotonicity</b>: <c>Shanten.Calculate(h - t) ≥ Shanten.Calculate(h)</c>
///         for any tile <c>t</c>, with equality when <c>t</c> is a
///         "loose" tile (contributes to no maximising decomposition).
///         The bot's discard pipeline relies on this — a discard that
///         increases shanten is provably worse than one that does not.</item>
/// </list>
/// </para>
/// </summary>
public static class Shanten
{
    /// <summary>
    /// Returns the minimum number of tile swaps the hand needs to reach a
    /// winning configuration across both Changsha winning shapes (Standard
    /// 4-groups + pair and SevenPairs). 0 means tenpai / winning.
    /// </summary>
    public static int Calculate(ChangshaHandState hand)
        => HandEvaluator.MinShantenToHu(hand, Array.Empty<int>());

    /// <summary>
    /// Returns the shanten of <paramref name="hand"/> after discarding one
    /// concealed tile whose logical id equals <paramref name="logicalTile"/>.
    /// Returns <see cref="int.MaxValue"/> if no tile of that logical id
    /// is present in <paramref name="hand"/>'s concealed list (defensive;
    /// the caller is expected to filter).
    /// </summary>
    /// <remarks>
    /// Clones the concealed list so the original hand is never mutated.
    /// Melds are reference-shared because the counter only reads
    /// <c>Melds.Count</c>.
    /// </remarks>
    public static int CalculateAfterDiscardingLogical(ChangshaHandState hand, int logicalTile)
    {
        ArgumentNullException.ThrowIfNull(hand);

        var idx = hand.ConcealedTiles.FindIndex(t => ChangshaDeckBuilder.GetLogicalTile(t) == logicalTile);
        if (idx < 0)
            return int.MaxValue;

        var concealedAfter = new List<int>(hand.ConcealedTiles);
        concealedAfter.RemoveAt(idx);

        var probe = new ChangshaHandState
        {
            SeatIndex = hand.SeatIndex,
            ConcealedTiles = concealedAfter,
            Melds = hand.Melds
        };
        return HandEvaluator.MinShantenToHu(probe, Array.Empty<int>());
    }

    /// <summary>
    /// Returns the shanten of <paramref name="hand"/> after adding one
    /// concealed tile whose logical id equals <paramref name="logicalTile"/>.
    /// Useful for evaluating "would this draw help me?" — a draw that does
    /// not lower shanten is informationally inert.
    /// </summary>
    public static int CalculateAfterAddingLogical(ChangshaHandState hand, int logicalTile)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (logicalTile < 0 || logicalTile >= ChangshaDeckBuilder.LogicalTileCount)
            return int.MaxValue;

        // Pick a representative physical tile id for the logical. Tile id = logical * 4
        // is always a valid encoding regardless of which copy was already drawn — the
        // shanten counter only inspects the LOGICAL id, not the physical copy.
        var concealedAfter = new List<int>(hand.ConcealedTiles)
        {
            logicalTile * ChangshaDeckBuilder.CopiesPerTile
        };

        var probe = new ChangshaHandState
        {
            SeatIndex = hand.SeatIndex,
            ConcealedTiles = concealedAfter,
            Melds = hand.Melds
        };
        return HandEvaluator.MinShantenToHu(probe, Array.Empty<int>());
    }

    /// <summary>
    /// True when the hand is one tile away from a win — i.e.,
    /// <c>Shanten.Calculate(hand) == 0</c>. Convenience wrapper used by
    /// <see cref="TenpaiDetector"/> and the bot test surface.
    /// </summary>
    public static bool IsTenpai(ChangshaHandState hand) => Calculate(hand) == 0;
}
