namespace Mahjong.Autotable.Api.Changsha.Bot.Heuristics;

/// <summary>
/// Detects when a bot is committed to a single suit (清一色 / FullFlush
/// territory) and computes a discard-bias signal that lifts tiles in
/// non-dominant suits.
///
/// <para>清一色 (qīng yī sè) is a 4-fan bonus in Frost's Changsha fan
/// catalog: every tile in the hand belongs to a single suit. Even a
/// near-flush hand commands a meaningful score lift on a regular Hu
/// (the directive's prior fan catalog lists FullFlush at 4 fan, which
/// roughly doubles the score multiplier vs a plain hand). The
/// <see cref="MasterStrategy"/> docstring promised "suit-purity awareness"
/// — this is the module that delivers it.</para>
///
/// <para><b>Algorithm</b>
/// <list type="number">
///   <item>Count concealed-tile copies per suit (Wan / Tong / Tiao).</item>
///   <item>The <i>dominant</i> suit is the suit with the maximum count.</item>
///   <item>If the dominant suit holds at least
///         <see cref="DefaultCommitmentThreshold"/> tiles (default 8)
///         the bot is "committed" — non-dominant suit tiles are biased
///         toward discard.</item>
///   <item>The bias is returned as a small negative integer (−1 by
///         default) suitable for tier-breakers that prefer lower scores
///         for discard candidates. Tiles in the dominant suit return 0.</item>
/// </list>
/// </para>
///
/// <para><b>Threshold rationale.</b> A 13-tile hand needs at least one
/// pair to win — committing to 清一色 requires that pair to live in the
/// dominant suit, which empirically happens when the bot already has
/// 8 / 13 tiles in one suit. Below 8 the cost of breaking up partial
/// sequences in the minority suits outweighs the speculative 4-fan
/// bonus. The 8-tile threshold matches the directive spec literally
/// ("once a bot has 8+ tiles of one suit, it should prefer to discard
/// from other suits to drive toward 清一色").</para>
///
/// <para><b>What this is NOT.</b> A primary discard key. The bias is a
/// small integer that fires as a <i>tie-breaker</i> behind shanten and
/// safety so the bot never breaks shape just to chase a flush. The
/// existing acceptance tests (BotStrengthTests, MasterBotTests) keep
/// the shanten-primary contract intact.</para>
/// </summary>
public static class SuitCommitment
{
    /// <summary>The default 8-tile threshold from the Wave-24 directive.</summary>
    public const int DefaultCommitmentThreshold = 8;

    /// <summary>
    /// Identifies the bot's dominant suit and its count. Returns
    /// (Suit.Wan, 0) when the hand is empty. Ties broken by suit
    /// enum order (Wan &lt; Tong &lt; Tiao) so the helper is
    /// deterministic.
    /// </summary>
    public static (Suit Dominant, int Count) DominantSuit(ChangshaHandState hand)
    {
        ArgumentNullException.ThrowIfNull(hand);

        Span<int> perSuit = stackalloc int[3];
        foreach (var tileId in hand.ConcealedTiles)
        {
            perSuit[(int)ChangshaDeckBuilder.GetSuit(tileId)]++;
        }
        // Also fold declared melds into the count — a bot with two pungs in
        // Tong is just as committed as one with eight concealed tong tiles,
        // and the bias should still apply.
        foreach (var meld in hand.Melds)
        {
            foreach (var tileId in meld.TileIds)
            {
                perSuit[(int)ChangshaDeckBuilder.GetSuit(tileId)]++;
            }
        }

        var bestSuit = Suit.Wan;
        var bestCount = perSuit[0];
        for (var s = 1; s < 3; s++)
        {
            if (perSuit[s] > bestCount)
            {
                bestSuit = (Suit)s;
                bestCount = perSuit[s];
            }
        }
        return (bestSuit, bestCount);
    }

    /// <summary>
    /// True when the bot has committed enough tiles to one suit that the
    /// flush-shoot heuristic should fire. Default threshold is 8 (see
    /// <see cref="DefaultCommitmentThreshold"/>).
    /// </summary>
    public static bool IsCommitted(ChangshaHandState hand, int threshold = DefaultCommitmentThreshold)
    {
        var (_, count) = DominantSuit(hand);
        return count >= threshold;
    }

    /// <summary>
    /// Computes the discard bias for <paramref name="tileId"/>: −1 when
    /// the hand is committed and <paramref name="tileId"/> lives in a
    /// non-dominant suit (so a sort-by-ascending tie-breaker discards
    /// it first); 0 otherwise.
    /// </summary>
    /// <remarks>
    /// Lower = more attractive to discard, matching the convention used by
    /// <see cref="HardStrategy"/>'s ComputeDiscardScore and
    /// <see cref="MasterStrategy"/>'s OpponentSafetyTieBreaker.
    /// </remarks>
    public static int Bias(int tileId, ChangshaHandState hand, int threshold = DefaultCommitmentThreshold)
    {
        ArgumentNullException.ThrowIfNull(hand);

        var (dominant, count) = DominantSuit(hand);
        if (count < threshold) return 0;
        return ChangshaDeckBuilder.GetSuit(tileId) == dominant ? 0 : -1;
    }
}
