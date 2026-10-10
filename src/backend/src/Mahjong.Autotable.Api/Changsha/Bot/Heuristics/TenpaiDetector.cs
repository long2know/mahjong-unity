namespace Mahjong.Autotable.Api.Changsha.Bot.Heuristics;

/// <summary>
/// Detects when an opponent is likely in tenpai (one tile from winning)
/// using only public information: their declared melds and the discard
/// pile. The bot can't see opponent concealed tiles, so the heuristic is
/// necessarily probabilistic, but mahjong's open-meld structure leaks
/// enough information to make a useful estimate.
///
/// <para><b>Why this matters.</b> Mahjong's classic defensive principle:
/// when an opponent is dangerous, discard <i>genbutsu</i> (現物 — "the
/// real thing"), i.e., tiles that opponent has already discarded
/// themselves. The reasoning: a tile they discarded is one they couldn't
/// use, so passing it back is provably safe against that opponent.
/// Without tenpai detection the bot has no way to know when to switch
/// from offense (efficient hand-building) to defense (safe discards).</para>
///
/// <para><b>Heuristic.</b> An opponent is flagged as likely-tenpai when
/// they have <see cref="LikelyTenpaiMeldThreshold"/> or more declared
/// melds (default 3). A 14-tile winning shape is 4 sets + 1 pair, so a
/// seat with 3 open melds has committed 9–12 tiles to declared structure
/// and has at most 4 concealed tiles, which structurally allows tenpai.
/// This is the same threshold real Changsha players use — "watch out
/// when someone has three melds on the table".</para>
///
/// <para><b>What we deliberately do NOT do.</b> Full opponent-hand
/// shanten inference (probing every possible concealed configuration
/// across the wall) is exponential and useless given the bot turn budget
/// (2000 ms). The meld-count heuristic is a fast, conservative proxy.
/// False positives (flagging a non-tenpai opponent) cost the bot some
/// offensive tempo; false negatives (missing a tenpai opponent) cost a
/// deal-in. The directive prioritises "feels like real mahjong" so we
/// optimise for false-positive bias — be cautious when an opponent has
/// 3+ melds even if they're not literally tenpai.</para>
/// </summary>
public static class TenpaiDetector
{
    /// <summary>
    /// Default 3-meld threshold. A seat with three declared melds has at
    /// most 4 concealed tiles and is one effective draw from a winning
    /// 4-sets-plus-pair shape. Matches the practical "be afraid of three
    /// melds" rule among Changsha players.
    /// </summary>
    public const int LikelyTenpaiMeldThreshold = 3;

    /// <summary>
    /// True when <paramref name="hand"/> has at least
    /// <see cref="LikelyTenpaiMeldThreshold"/> declared melds. Pure /
    /// stateless / O(1) check.
    /// </summary>
    public static bool IsLikelyTenpai(ChangshaHandState hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        return hand.Melds.Count >= LikelyTenpaiMeldThreshold;
    }

    /// <summary>
    /// Returns the seat indices of opponents (i.e. seats other than
    /// <paramref name="botSeatIndex"/>) whose hands look like likely
    /// tenpai per <see cref="IsLikelyTenpai"/>. The list preserves
    /// ascending seat order. Empty when no opponent is dangerous.
    /// </summary>
    public static IReadOnlyList<int> CollectDangerousOpponents(ChangshaGameState state, int botSeatIndex)
    {
        ArgumentNullException.ThrowIfNull(state);

        var result = new List<int>();
        foreach (var hand in state.Hands.OrderBy(h => h.SeatIndex))
        {
            if (hand.SeatIndex == botSeatIndex) continue;
            if (IsLikelyTenpai(hand))
                result.Add(hand.SeatIndex);
        }
        return result;
    }

    /// <summary>
    /// Collects the set of logical tile ids that the supplied opponents
    /// have themselves discarded over the course of the current hand.
    /// These are the <i>genbutsu</i> — proven safe against each listed
    /// opponent because they discarded the tile rather than claiming it.
    ///
    /// <para>Returns an empty set when the discard pile holds no entries
    /// from the supplied opponents (e.g. very early in a hand or all
    /// dangerous opponents are dealer-only seats).</para>
    /// </summary>
    public static HashSet<int> CollectGenbutsuLogicals(ChangshaGameState state, IReadOnlyList<int> opponentSeats)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(opponentSeats);

        var result = new HashSet<int>();
        if (opponentSeats.Count == 0) return result;

        var seatLookup = new HashSet<int>(opponentSeats);
        foreach (var discard in state.DiscardPile)
        {
            if (!seatLookup.Contains(discard.SeatIndex)) continue;
            result.Add(ChangshaDeckBuilder.GetLogicalTile(discard.TileId));
        }
        return result;
    }

    /// <summary>
    /// Returns the safety bias for <paramref name="tileId"/>: −1 when at
    /// least one opponent is likely tenpai AND that opponent has already
    /// discarded a tile with the same logical id (genbutsu against a
    /// dangerous opponent); 0 otherwise.
    /// </summary>
    /// <remarks>
    /// <para>This is a stronger signal than the generic "anyone has
    /// discarded this tile" bias used by Hard/Master: a generic discard
    /// only tells us the table once didn't need the tile, but a discard
    /// <i>from a dangerous opponent</i> tells us that specific dangerous
    /// opponent can't capitalise on its return. We treat it as a tiny
    /// negative integer (−1) suitable for tier-breakers; callers layer
    /// it after shanten and keep-score so it never overrides shape.</para>
    ///
    /// <para>Returns 0 when no opponent is dangerous — falling back
    /// silently keeps the bot's offensive tempo intact during the
    /// early hand when there's no defensive pressure yet.</para>
    /// </remarks>
    public static int SafetyBias(int tileId, ChangshaGameState state, int botSeatIndex)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dangerous = CollectDangerousOpponents(state, botSeatIndex);
        if (dangerous.Count == 0) return 0;

        var genbutsu = CollectGenbutsuLogicals(state, dangerous);
        var logical = ChangshaDeckBuilder.GetLogicalTile(tileId);
        return genbutsu.Contains(logical) ? -1 : 0;
    }
}
