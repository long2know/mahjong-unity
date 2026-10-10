namespace Mahjong.Autotable.Api.Changsha.Bot.Heuristics;

/// <summary>
/// Pure scorer for the "tile efficiency on discard" heuristic from the
/// Wave-24 bot-strategy directive (Frost). Implements the spec formula
/// verbatim so a regression in the tuning is detectable:
///
/// <code>
/// efficiency(t, hand) = SameSuitNeighbourCount(t, hand) + 2 * SameLogicalMatchCount(t, hand)
/// </code>
///
/// <para>Where:
/// <list type="bullet">
///   <item><b>SameSuitNeighbourCount</b> — number of concealed tiles in the
///         same suit as <c>t</c> whose rank lies within ±2 of <c>t</c>'s
///         rank (excluding <c>t</c> itself). This is the "would this tile
///         participate in a chow / partial chow" signal. Out-of-suit and
///         far-away neighbours don't contribute — only meaningful shape
///         partners.</item>
///   <item><b>SameLogicalMatchCount</b> — number of concealed copies of the
///         same logical tile as <c>t</c> minus one (i.e., excluding
///         <c>t</c>). A pair contributes 1; a triplet contributes 2.
///         Multiplied by 2 because committed pungs / pairs are
///         structurally more valuable than partial chows.</item>
/// </list>
/// </para>
///
/// <para><b>Discard convention.</b> <i>Higher</i> efficiency means the
/// tile contributes more to the hand and should be <i>kept</i>. To pick a
/// discard, callers should choose the tile with the <i>lowest</i>
/// efficiency. <see cref="SelectDiscardByEfficiency"/> implements that
/// selection (lowest efficiency; tile-id descending tie-breaker for
/// determinism).</para>
///
/// <para><b>Why this exists.</b> The Phase F <see cref="MediumStrategy"/>
/// keep-score is functionally similar but tuned with the 2/5/8 bias and
/// gap-partial credit. This scorer is the unbiased reference: it has no
/// 2/5/8 bias and no gap credit, so it makes a clean comparison surface
/// for new heuristics layered on top (suit commitment, tenpai defense)
/// without the keep-score's tuning noise getting in the way. Used by
/// <see cref="BotStrategyTests"/> to pin the math.</para>
/// </summary>
public static class DiscardEfficiency
{
    /// <summary>
    /// Counts concealed tiles in the same suit as <paramref name="tileId"/>
    /// whose rank is within ±2 of <paramref name="tileId"/>'s rank,
    /// excluding the tile itself. Returns 0 when no such neighbours exist
    /// (the tile is isolated / orphaned in its suit).
    /// </summary>
    public static int CountSameSuitNeighbours(int tileId, ChangshaHandState hand)
    {
        ArgumentNullException.ThrowIfNull(hand);

        var suit = ChangshaDeckBuilder.GetSuit(tileId);
        var rank = ChangshaDeckBuilder.GetRank(tileId);
        var matchedSelf = false;

        var count = 0;
        foreach (var other in hand.ConcealedTiles)
        {
            if (!matchedSelf && other == tileId)
            {
                matchedSelf = true;
                continue;
            }
            if (ChangshaDeckBuilder.GetSuit(other) != suit) continue;
            var otherRank = ChangshaDeckBuilder.GetRank(other);
            var delta = Math.Abs(otherRank - rank);
            if (delta is >= 1 and <= 2)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Counts how many <i>other</i> concealed copies of the same logical
    /// tile <paramref name="tileId"/> belongs to. A lone tile returns 0,
    /// a pair returns 1, a triplet returns 2.
    /// </summary>
    public static int CountSameLogicalMatches(int tileId, ChangshaHandState hand)
    {
        ArgumentNullException.ThrowIfNull(hand);

        var logical = ChangshaDeckBuilder.GetLogicalTile(tileId);
        var matchedSelf = false;
        var matches = 0;

        foreach (var other in hand.ConcealedTiles)
        {
            if (ChangshaDeckBuilder.GetLogicalTile(other) != logical) continue;
            if (!matchedSelf && other == tileId)
            {
                matchedSelf = true;
                continue;
            }
            matches++;
        }
        return matches;
    }

    /// <summary>
    /// Computes the directive's efficiency formula:
    /// <c>SameSuitNeighbourCount(t) + 2 * SameLogicalMatchCount(t)</c>.
    /// Higher = more useful to keep, lower = better to discard.
    /// </summary>
    public static int Score(int tileId, ChangshaHandState hand)
        => CountSameSuitNeighbours(tileId, hand)
           + 2 * CountSameLogicalMatches(tileId, hand);

    /// <summary>
    /// Selects the concealed tile with the lowest efficiency score
    /// (= best to discard). Ties broken by descending tile id for
    /// determinism, mirroring the existing strategies. Throws if the
    /// hand has no concealed tiles.
    /// </summary>
    public static int SelectDiscardByEfficiency(ChangshaHandState hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (hand.ConcealedTiles.Count == 0)
            throw new InvalidOperationException("Cannot discard from empty hand.");

        return hand.ConcealedTiles
            .OrderBy(t => Score(t, hand))
            .ThenByDescending(t => t)
            .First();
    }
}
