namespace Mahjong.Autotable.Api.Changsha.Scoring;

/// <summary>
/// Variant gate for fans. Pure Changsha (108 tiles, suits only, no honors / dragons /
/// flowers / jokers) emits only <see cref="Changsha"/>-tagged fans. Future variant
/// switchers can flip this to <see cref="ExpandedChinese"/> to enable honor- and
/// dragon-bearing fans (混一色, 大三元, etc.) when a different deck is in play.
///
/// <para>Numeric ordering matters: a caller running with
/// <c>ctx.Variant = ExpandedChinese</c> sees every fan whose
/// <see cref="FanInfo.Variant"/> is <c>&lt;= ExpandedChinese</c> — i.e. both the
/// Changsha base set AND the expanded-Chinese additions. A caller in pure-Changsha
/// mode sees only the base set.</para>
/// </summary>
public enum FanVariant
{
    /// <summary>Pure Changsha — 108 tiles, no honors, no dragons. Default.</summary>
    Changsha = 0,

    /// <summary>
    /// Expanded Chinese ruleset — full 144-tile deck with honors + dragons + flowers.
    /// Unlocks 混一色 (honors + one suit), 大三元 (big three dragons), and future
    /// honor-dependent fans. NOT implemented as a runtime mode yet; consumers wire
    /// this through <see cref="FanContext.Variant"/> only when a future game-options
    /// flag exposes it. See <c>.squad/decisions/inbox/frost-fan-catalog.md</c>.
    /// </summary>
    ExpandedChinese = 1,
}

/// <summary>
/// Canonical fan catalog for Changsha Mahjong scoring per the Baidu Baike
/// 长沙麻将 entry (https://baike.baidu.com/en/item/Changsha%20Mahjong/36618) and
/// the MahjongPros beginner guide. Each member is annotated with its Chinese
/// name + Pinyin + English description + base points + variant flag in
/// <see cref="FanCatalog.Entries"/>.
///
/// <para>This enum is INTENTIONALLY DISTINCT from <see cref="WinPattern"/>. The
/// <see cref="WinPattern"/> enum is the structural shape of the hand (Standard,
/// SevenPairs, AllPungs, FullFlush, NineTerminals, HeavenlyHand, EarthlyHand,
/// LastTileFromWall, LastDiscardCatch, KongReplacementWin); the <see cref="Fan"/>
/// enum is the catalog of countable bonuses that compose a final fan tally. They
/// overlap deliberately (e.g. <see cref="Fan.SevenPairs"/> ↔
/// <see cref="WinPattern.SevenPairs"/>) so that
/// <see cref="FanCalculator.EvaluateHand"/> can be invoked independently of
/// <see cref="WinDetector"/>, but also augment with method-only and concealment
/// bonuses that <see cref="WinPattern"/> doesn't model (<see cref="Fan.SelfDraw"/>,
/// <see cref="Fan.RobbingKong"/>, <see cref="Fan.ConcealedHand"/>).</para>
/// </summary>
public enum Fan
{
    // ── Situational / method fans ───────────────────────────────────

    /// <summary>自摸 (zì mō) — winning on the tile you just drew from the wall.</summary>
    SelfDraw,

    /// <summary>杠上开花 (gàng shàng kāi huā) — winning on the kong-replacement
    /// tile drawn after declaring any kong (concealed, exposed, or added).</summary>
    KongReplacement,

    /// <summary>海底捞月 (hǎi dǐ lāo yuè) — self-draw win on the very last tile of
    /// the wall (no further draws are possible).</summary>
    LastTileFromWall,

    /// <summary>河底捞鱼 (hé dǐ lāo yú) — discard-Hu on a tile thrown when the wall
    /// is already exhausted. Sibling of <see cref="LastTileFromWall"/> for the
    /// discard side.</summary>
    LastDiscardCatch,

    /// <summary>抢杠 (qiǎng gàng) — winning by claiming the 4th tile another seat
    /// just used to upgrade an exposed pung to a kong. Concealed kongs are not
    /// robbable.</summary>
    RobbingKong,

    // ── Suit-purity fans ────────────────────────────────────────────

    /// <summary>清一色 (qīng yī sè) — entire hand (concealed + melded) in a single
    /// suit, no honors, no other suits. Big fan.</summary>
    FullFlush,

    /// <summary>混一色 (hùn yī sè) — entire hand in a single suit plus honor tiles
    /// only. Variant-gated: not reachable in pure Changsha (108-tile deck has no
    /// honors) — enabled when <see cref="FanContext.Variant"/> ==
    /// <see cref="FanVariant.ExpandedChinese"/>.</summary>
    MixedOneSuit,

    // ── Hand-shape fans ─────────────────────────────────────────────

    /// <summary>七对 (qī duì) — winning hand composed of exactly 7 pairs, no
    /// melds. Bypasses the 258-pair rule.</summary>
    SevenPairs,

    /// <summary>碰碰胡 (pèng pèng hú) — winning hand composed entirely of
    /// pungs/kongs + a pair (no chows).</summary>
    AllPungs,

    // ── Concealment fans ────────────────────────────────────────────

    /// <summary>门清 (mén qīng) — winning without any opponent-claimed melds in
    /// the player's hand. Concealed kongs (暗杠) are allowed because the kong
    /// tiles came from self-drawn tiles, not from an opponent. The win itself
    /// may be by discard or self-draw — the stricter 门前清 variant (concealed
    /// AND self-draw) is composed as <see cref="ConcealedHand"/> + <see cref="SelfDraw"/>.</summary>
    ConcealedHand,

    // ── Expanded-Chinese / variant-gated fans ──────────────────────

    /// <summary>大三元 (dà sān yuán) — pungs or kongs of all three dragon tiles
    /// (中/發/白). Variant-gated: not reachable in pure Changsha (no dragon
    /// tiles in the 108-tile deck) — enabled when <see cref="FanContext.Variant"/>
    /// == <see cref="FanVariant.ExpandedChinese"/>.</summary>
    BigThreeDragons,

    // ── Prestige / contextual structural fans ──────────────────────

    /// <summary>天和 (tiān hé) — dealer self-draws Hu on the initial 14-tile hand
    /// before any discards or claims. Highest contextual fan.</summary>
    HeavenlyHand,

    /// <summary>地和 (dì hé) — non-dealer wins on the dealer's very first discard
    /// with no intervening claims/draws and no opponent-claimed melds.</summary>
    EarthlyHand,

    /// <summary>九幺 (jiǔ yāo) — all 14 tiles in the hand are rank 1 or rank 9 of
    /// any suit, with all six distinct terminals present.</summary>
    NineTerminals,
}

/// <summary>
/// Per-fan metadata: Chinese name, Pinyin, English description, base points, and
/// variant gate. The point values are flat additive bonuses layered on top of the
/// base 258-pair small-win / big-win scoring tier (<see cref="ScoringService"/>).
///
/// <para>Numbers are tuned to match the Baidu Baike 长沙麻将 implied weighting:
/// situational fans (自摸, 杠上开花, 海底捞月, 河底捞鱼, 抢杠) are ×2
/// multipliers in the source rule text, encoded here as a flat 1-2 point bonus
/// since this fan layer is purely additive (it does NOT replace
/// <see cref="ScoringService"/>'s small/big tier base). Big-shape fans
/// (清一色, 七对, 碰碰胡) carry the biggest weights; concealment is light (1).
/// Heavenly/Earthly hands are weighted equal to FullFlush.</para>
/// </summary>
public sealed record FanInfo(
    Fan Fan,
    string Chinese,
    string Pinyin,
    string English,
    int Points,
    string Description,
    FanVariant Variant);

/// <summary>
/// Static lookup keyed by <see cref="Fan"/>. Used by
/// <see cref="FanCalculator.EvaluateHand"/> to convert a detected fan to a
/// <see cref="DetectedFan"/> with points, and by any caller that wants to render
/// localised names without taking a dependency on the i18n controller.
/// </summary>
public static class FanCatalog
{
    public static readonly IReadOnlyDictionary<Fan, FanInfo> Entries = new Dictionary<Fan, FanInfo>
    {
        [Fan.SelfDraw] = new(
            Fan.SelfDraw, "自摸", "zì mō", "Self-draw",
            Points: 1,
            Description: "Winning on the tile you just drew from the wall (vs. claiming a discard). Adds a flat bonus on top of the base small/big-win tier.",
            Variant: FanVariant.Changsha),

        [Fan.KongReplacement] = new(
            Fan.KongReplacement, "杠上开花", "gàng shàng kāi huā", "Win on Kong Replacement",
            Points: 2,
            Description: "Self-draw on the replacement tile drawn after declaring a kong (concealed, exposed, or added).",
            Variant: FanVariant.Changsha),

        [Fan.LastTileFromWall] = new(
            Fan.LastTileFromWall, "海底捞月", "hǎi dǐ lāo yuè", "Last Tile from the Wall",
            Points: 2,
            Description: "Self-draw on the very last tile of the wall — no more draws are possible.",
            Variant: FanVariant.Changsha),

        [Fan.LastDiscardCatch] = new(
            Fan.LastDiscardCatch, "河底捞鱼", "hé dǐ lāo yú", "Last Discard Catch",
            Points: 2,
            Description: "Discard-Hu on a tile thrown when the wall is already exhausted.",
            Variant: FanVariant.Changsha),

        [Fan.RobbingKong] = new(
            Fan.RobbingKong, "抢杠", "qiǎng gàng", "Robbing the Kong",
            Points: 2,
            Description: "Winning by claiming the 4th tile that another seat is adding to an existing exposed pung. Concealed kongs are not robbable.",
            Variant: FanVariant.Changsha),

        [Fan.FullFlush] = new(
            Fan.FullFlush, "清一色", "qīng yī sè", "Pure Suit",
            Points: 6,
            Description: "Every tile in the winning hand (concealed + melded) belongs to a single suit and there are no honors.",
            Variant: FanVariant.Changsha),

        [Fan.MixedOneSuit] = new(
            Fan.MixedOneSuit, "混一色", "hùn yī sè", "Mixed One Suit",
            Points: 3,
            Description: "Entire hand in one suit plus honor tiles only. Variant-gated — pure Changsha has no honor tiles in its 108-tile deck, so this fan is reachable only under the expanded-Chinese variant.",
            Variant: FanVariant.ExpandedChinese),

        [Fan.SevenPairs] = new(
            Fan.SevenPairs, "七对", "qī duì", "Seven Pairs",
            Points: 4,
            Description: "Hand composed of exactly 7 pairs (14 tiles, no melds). Bypasses the 258-pair restriction on the standard 4-sets-plus-pair shape.",
            Variant: FanVariant.Changsha),

        [Fan.AllPungs] = new(
            Fan.AllPungs, "碰碰胡", "pèng pèng hú", "All Pungs",
            Points: 4,
            Description: "Hand composed entirely of pungs (triplets) or kongs plus the pair — no chows.",
            Variant: FanVariant.Changsha),

        [Fan.ConcealedHand] = new(
            Fan.ConcealedHand, "门清", "mén qīng", "Concealed Hand",
            Points: 1,
            Description: "Winning without any opponent-claimed melds. Concealed kongs (暗杠) drawn entirely from one's own tiles are still considered concealed. The strict 门前清 variant (concealed AND self-drawn) is composed as ConcealedHand + SelfDraw.",
            Variant: FanVariant.Changsha),

        [Fan.BigThreeDragons] = new(
            Fan.BigThreeDragons, "大三元", "dà sān yuán", "Big Three Dragons",
            Points: 8,
            Description: "Pungs (or kongs) of all three dragon tiles — 中 (red), 發 (green), 白 (white). Variant-gated — pure Changsha has no dragons in its 108-tile deck.",
            Variant: FanVariant.ExpandedChinese),

        [Fan.HeavenlyHand] = new(
            Fan.HeavenlyHand, "天和", "tiān hé", "Heavenly Hand",
            Points: 8,
            Description: "Dealer wins by self-draw on the initial 14-tile hand before any discards, claims, or kong replacements have occurred.",
            Variant: FanVariant.Changsha),

        [Fan.EarthlyHand] = new(
            Fan.EarthlyHand, "地和", "dì hé", "Earthly Hand",
            Points: 8,
            Description: "Non-dealer wins on the dealer's very first discard with no intervening actions and no opponent-claimed melds.",
            Variant: FanVariant.Changsha),

        [Fan.NineTerminals] = new(
            Fan.NineTerminals, "九幺", "jiǔ yāo", "Nine Terminals",
            Points: 6,
            Description: "All 14 tiles in the hand are rank 1 or rank 9 of any suit, and all six distinct terminals (1万 9万 1筒 9筒 1条 9条) appear at least once.",
            Variant: FanVariant.Changsha),
    };

    /// <summary>
    /// Returns the metadata entry for <paramref name="fan"/>. Throws
    /// <see cref="KeyNotFoundException"/> if a new enum member is added without a
    /// matching dictionary entry — the unit tests in
    /// <c>FanCalculatorTests.Catalog_HasEntryForEveryFan</c> guard this.
    /// </summary>
    public static FanInfo Get(Fan fan) => Entries[fan];
}
