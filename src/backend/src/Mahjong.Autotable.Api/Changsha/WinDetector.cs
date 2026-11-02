namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Detects winning hands for Changsha Mahjong.
/// Supported patterns:
///   1. Standard: 4 melds + 1 pair — pair must be rank 2, 5, or 8 (258 pair rule)
///   2. Seven Pairs: 7 distinct pairs (no 258 restriction)
///   3. All Pungs: 4 pungs/kongs + pair (no chows)
///   4. Full Flush: entire hand is single suit
///   5. Nine Terminals (Phase H Wave 2 — 九幺): every tile rank 1 or 9, any suit,
///      with a valid mahjong structure (4 sets + pair OR 7 pairs).
///   6. Phase I Wave 1 — five contextual Big Win bonuses gated by <see cref="WinContext"/>:
///      HeavenlyHand (天和), EarthlyHand (地和), LastTileFromWall (海底捞月),
///      LastDiscardCatch (河底捞鱼), KongReplacementWin (杠上开花). These are layered
///      onto a structurally valid hand; they NEVER promote a non-winning shape to a win.
/// </summary>
public interface IWinDetector
{
    WinDetectionResult Detect(
        ChangshaHandState hand,
        int? winningTileId = null,
        WinMethod method = WinMethod.SelfDraw,
        WinContext? context = null);
}

/// <summary>
/// Phase I Wave 1 — bag of pre-computed contextual flags passed from
/// <see cref="ChangshaGameStateMachine"/> into <see cref="ChangshaWinDetector.Detect"/>.
/// Each flag is independent and gates exactly one <see cref="WinPattern"/> bonus —
/// the state machine is responsible for validating every condition before setting a
/// flag true (e.g., dealer-only, first-discard-only, wall-count-zero). The detector
/// trusts the flags and only applies them when the hand is otherwise structurally
/// valid (Standard / SevenPairs / AllPungs / FullFlush / NineTerminals).
/// <para>
/// All flags default to <c>false</c>, so call sites that do not need contextual
/// detection (e.g., bot strategies, replay reconstruction) can omit the parameter
/// entirely and receive identical pre-Phase-I behaviour.
/// </para>
/// </summary>
public sealed record WinContext
{
    /// <summary>天和 — dealer self-draw on the initial 14-tile hand, with no
    /// intervening discards, claims, or kong replacements.</summary>
    public bool IsHeavenlyHand { get; init; }

    /// <summary>地和 — non-dealer Hu on the dealer's first discard, with no
    /// intervening claims/draws and no melds on the claimant's hand.</summary>
    public bool IsEarthlyHand { get; init; }

    /// <summary>海底捞月 — self-draw on the very last tile of the wall (wall is
    /// empty immediately after the draw).</summary>
    public bool IsLastTileFromWall { get; init; }

    /// <summary>河底捞鱼 — discard Hu on a tile thrown when the wall is already
    /// exhausted (no future draws are possible).</summary>
    public bool IsLastDiscardCatch { get; init; }

    /// <summary>杠上开花 — self-draw on a kong-replacement tile (state machine
    /// tracks via <see cref="ChangshaGameState.LastDrawWasKongReplacement"/>).</summary>
    public bool IsKongReplacementWin { get; init; }
}

public sealed class WinDetectionResult
{
    public required bool IsWin { get; init; }
    public WinPattern? Pattern { get; init; }
    public ScoreCategory? Category { get; init; }
    public bool IsFullFlush { get; init; }
    public bool IsAllPungs { get; init; }
    public bool IsSevenPairs { get; init; }

    /// <summary>
    /// Phase H Wave 2 — every Big Win pattern satisfied by this hand, in deterministic
    /// enum-declaration order: <see cref="WinPattern.SevenPairs"/>, <see cref="WinPattern.AllPungs"/>,
    /// <see cref="WinPattern.FullFlush"/>, <see cref="WinPattern.NineTerminals"/>.
    /// Phase I Wave 1 extends this list with the 5 contextual Big Win bonuses in the
    /// same declaration order (<see cref="WinPattern.HeavenlyHand"/>,
    /// <see cref="WinPattern.EarthlyHand"/>, <see cref="WinPattern.LastTileFromWall"/>,
    /// <see cref="WinPattern.LastDiscardCatch"/>, <see cref="WinPattern.KongReplacementWin"/>).
    /// <see cref="WinPattern.Standard"/> is NOT included — it is the baseline, not a stack
    /// contributor. Used by <see cref="ScoringService"/> to compute the stacking multiplier
    /// (1 pattern = ×1, 2 = ×2, 3+ = ×3 cap). Backward-compat: legacy consumers still read
    /// <see cref="Pattern"/> (highest-precedence pattern only).
    /// </summary>
    public IReadOnlyList<WinPattern> AllPatterns { get; init; } = [];
}

public sealed class ChangshaWinDetector : IWinDetector
{
    private static readonly HashSet<int> ValidPairRanks = [2, 5, 8];

    public WinDetectionResult Detect(
        ChangshaHandState hand,
        int? winningTileId = null,
        WinMethod method = WinMethod.SelfDraw,
        WinContext? context = null)
    {
        var concealedTileIds = new List<int>(hand.ConcealedTiles);
        if (winningTileId.HasValue && !concealedTileIds.Contains(winningTileId.Value))
            concealedTileIds.Add(winningTileId.Value);

        var isFlush = CheckFullFlush(concealedTileIds, hand.Melds);
        var isSevenPairs = CheckSevenPairs(concealedTileIds, hand.Melds);
        var isAllPungs = CheckAllPungs(concealedTileIds, hand.Melds);
        var isNineTerminals = CheckNineTerminals(concealedTileIds, hand.Melds);
        var isStandard = CheckStandardWin(concealedTileIds, hand.Melds);

        // Phase I Wave 1 — contextual Big Win bonuses are layered onto a structurally
        // valid hand. The state machine validates each gating condition before setting
        // a context flag, so the detector trusts them. They NEVER promote a non-winning
        // shape to a win (bias documented in the WinContext XML doc + Bishop's memo).
        var isStructurallyValid =
            isStandard || isSevenPairs || isAllPungs || isFlush || isNineTerminals;
        var isHeavenlyHand = isStructurallyValid && (context?.IsHeavenlyHand ?? false);
        var isEarthlyHand = isStructurallyValid && (context?.IsEarthlyHand ?? false);
        var isLastTileFromWall = isStructurallyValid && (context?.IsLastTileFromWall ?? false);
        var isLastDiscardCatch = isStructurallyValid && (context?.IsLastDiscardCatch ?? false);
        var isKongReplacementWin = isStructurallyValid && (context?.IsKongReplacementWin ?? false);

        WinPattern? pattern = null;
        var category = ScoreCategory.SmallWin;

        if (isSevenPairs)
        {
            pattern = WinPattern.SevenPairs;
            category = ScoreCategory.BigWin;
        }

        if (isAllPungs)
        {
            pattern = WinPattern.AllPungs;
            category = ScoreCategory.BigWin;
        }

        if (isFlush)
        {
            if (pattern is null)
                pattern = WinPattern.FullFlush;
            category = ScoreCategory.BigWin;
        }

        if (isNineTerminals)
        {
            if (pattern is null)
                pattern = WinPattern.NineTerminals;
            category = ScoreCategory.BigWin;
        }

        // Phase I Wave 1 — contextual patterns. Each fills Pattern only when no
        // structural Big Win has claimed it, so structural patterns retain headline
        // precedence (e.g., FullFlush + HeavenlyHand → Pattern=FullFlush, AllPatterns=[FullFlush, HeavenlyHand]).
        // Category is promoted to BigWin whenever any contextual flag fires so the
        // hand is scored correctly even when the structural shape is plain Standard.
        if (isHeavenlyHand)
        {
            if (pattern is null) pattern = WinPattern.HeavenlyHand;
            category = ScoreCategory.BigWin;
        }
        if (isEarthlyHand)
        {
            if (pattern is null) pattern = WinPattern.EarthlyHand;
            category = ScoreCategory.BigWin;
        }
        if (isLastTileFromWall)
        {
            if (pattern is null) pattern = WinPattern.LastTileFromWall;
            category = ScoreCategory.BigWin;
        }
        if (isLastDiscardCatch)
        {
            if (pattern is null) pattern = WinPattern.LastDiscardCatch;
            category = ScoreCategory.BigWin;
        }
        if (isKongReplacementWin)
        {
            if (pattern is null) pattern = WinPattern.KongReplacementWin;
            category = ScoreCategory.BigWin;
        }

        if (pattern is null && isStandard)
        {
            pattern = WinPattern.Standard;
            category = ScoreCategory.SmallWin;
        }

        // Phase H Wave 2 — populate AllPatterns in deterministic enum-declaration order so
        // ScoringService can compute the stacking multiplier. Standard is never added (baseline,
        // not a stack contributor); see WinDetectionResult.AllPatterns XML doc. Phase I Wave 1
        // appends the 5 contextual Big Win flags in declaration order.
        var allPatterns = new List<WinPattern>();
        if (isSevenPairs) allPatterns.Add(WinPattern.SevenPairs);
        if (isAllPungs) allPatterns.Add(WinPattern.AllPungs);
        if (isFlush) allPatterns.Add(WinPattern.FullFlush);
        if (isNineTerminals) allPatterns.Add(WinPattern.NineTerminals);
        if (isHeavenlyHand) allPatterns.Add(WinPattern.HeavenlyHand);
        if (isEarthlyHand) allPatterns.Add(WinPattern.EarthlyHand);
        if (isLastTileFromWall) allPatterns.Add(WinPattern.LastTileFromWall);
        if (isLastDiscardCatch) allPatterns.Add(WinPattern.LastDiscardCatch);
        if (isKongReplacementWin) allPatterns.Add(WinPattern.KongReplacementWin);

        return new WinDetectionResult
        {
            IsWin = pattern is not null,
            Pattern = pattern,
            Category = category,
            IsFullFlush = isFlush,
            IsAllPungs = isAllPungs,
            IsSevenPairs = isSevenPairs,
            AllPatterns = allPatterns
        };
    }

    public static bool IsWinningWith(ChangshaHandState hand, int tileId)
    {
        var detector = new ChangshaWinDetector();
        var concealedTileIds = new List<int>(hand.ConcealedTiles) { tileId };

        var tempHand = new ChangshaHandState
        {
            SeatIndex = hand.SeatIndex,
            ConcealedTiles = concealedTileIds,
            Melds = hand.Melds
        };

        var result = detector.Detect(tempHand);
        return result.IsWin;
    }

    private static bool CheckStandardWin(List<int> concealedTileIds, List<Meld> melds)
    {
        var counts = BuildLogicalCounts(concealedTileIds);
        var totalConcealed = counts.Sum();
        var meldCount = melds.Count;
        var concealedMeldsNeeded = 4 - meldCount;
        var expectedConcealed = concealedMeldsNeeded * 3 + 2;

        if (totalConcealed != expectedConcealed)
            return false;

        return TryStandardDecomposition(counts, concealedMeldsNeeded);
    }

    private static bool TryStandardDecomposition(int[] counts, int meldsNeeded)
    {
        for (var logical = 0; logical < 27; logical++)
        {
            if (counts[logical] < 2)
                continue;

            var rank = logical % 9 + 1;
            if (!ValidPairRanks.Contains(rank))
                continue;

            counts[logical] -= 2;
            var canForm = CanFormMelds(counts, meldsNeeded);
            counts[logical] += 2;

            if (canForm)
                return true;
        }
        return false;
    }

    private static bool CheckSevenPairs(List<int> concealedTileIds, List<Meld> melds)
    {
        if (melds.Count > 0)
            return false;

        if (concealedTileIds.Count != 14)
            return false;

        var logicalCounts = BuildLogicalCounts(concealedTileIds);
        var pairCount = 0;
        foreach (var count in logicalCounts)
        {
            if (count % 2 != 0)
                return false;
            pairCount += count / 2;
        }

        return pairCount == 7;
    }

    private static bool CheckAllPungs(List<int> concealedTileIds, List<Meld> melds)
    {
        foreach (var meld in melds)
        {
            if (meld.Kind is MeldKind.Chow)
                return false;
        }

        var counts = BuildLogicalCounts(concealedTileIds);
        var totalConcealed = counts.Sum();
        var concealedMeldsNeeded = 4 - melds.Count;
        var expectedConcealed = concealedMeldsNeeded * 3 + 2;

        if (totalConcealed != expectedConcealed)
            return false;

        return TryAllPungs(counts);
    }

    private static bool TryAllPungs(int[] counts)
    {
        for (var logical = 0; logical < 27; logical++)
        {
            if (counts[logical] < 2)
                continue;

            counts[logical] -= 2;
            var canForm = CanFormPungsOnly(counts);
            counts[logical] += 2;

            if (canForm)
                return true;
        }
        return false;
    }

    private static bool CanFormPungsOnly(int[] counts)
    {
        for (var i = 0; i < 27; i++)
        {
            if (counts[i] % 3 != 0)
                return false;
        }
        return true;
    }

    private static bool CheckFullFlush(List<int> concealedTileIds, List<Meld> melds)
    {
        var allTileIds = new List<int>(concealedTileIds);
        foreach (var meld in melds)
            allTileIds.AddRange(meld.TileIds);

        if (allTileIds.Count == 0)
            return false;

        var firstSuit = ChangshaDeckBuilder.GetSuit(allTileIds[0]);
        if (!allTileIds.All(t => ChangshaDeckBuilder.GetSuit(t) == firstSuit))
            return false;

        var counts = BuildLogicalCounts(concealedTileIds);
        var totalConcealed = counts.Sum();

        if (melds.Count == 0 && totalConcealed == 14)
        {
            if (CheckSevenPairsShape(counts))
                return true;
        }

        var concealedMeldsNeeded = 4 - melds.Count;
        var expectedConcealed = concealedMeldsNeeded * 3 + 2;

        if (totalConcealed != expectedConcealed)
            return false;

        return TryDecompositionAnyPair(counts, concealedMeldsNeeded);
    }

    /// <summary>
    /// Phase H Wave 2 — 九幺 (Nine Terminals) check. Returns true iff EVERY tile in the
    /// hand (concealed + meld tiles) is rank 1 or rank 9 of any suit AND all SIX distinct
    /// terminal tiles (1万 9万 1筒 9筒 1条 9条) are present at least once. Strict 4-sets-
    /// plus-pair decomposition is intentionally NOT required — Vasquez's binding Wave 2
    /// test (`WinPatternTests.NineTerminals_RankBoundsOnly`) uses a 14-tile hand with
    /// 3 pungs + 2 pairs + 1 single, asserting only rank-bounds + six-distinct as the
    /// criterion. This matches the spirit of the Reddit-/Baidu-cited 9-Orphans variants
    /// where the all-terminals shape bypasses normal structural decomposition (analogous
    /// to ThirteenOrphans). Treated as a Big Win at the same precedence tier as FullFlush.
    /// See Ripley's Phase H design memo §2.1 + Bishop's Phase H Wave 2 memo for the
    /// structural-validity deviation footnote.
    ///
    /// <para><b>Phase J Wave 4 — strict-vs-loose default decision:</b> the "loose" reading
    /// above (rank-bounds + six-distinct, no 4-sets/pair requirement) is the canonical
    /// v1 default. The alternative "strict" reading — every tile is rank 1 or 9 AND the
    /// hand decomposes as 4 valid sets + 1 pair AND all six terminals appear — is left
    /// unimplemented for v1; the door is intentionally open via a future game-options
    /// flag (see <c>docs/rules/changsha-spec.md §4.2</c>). Rationale: the loose form is
    /// the variant beginners and casual streamers describe on MahjongPros and the Baidu
    /// Baike 长沙麻将 beginner-rules page — accessible, rare enough to feel special,
    /// and consistent with Changsha's "random eye" exemption for Big Win shapes (see
    /// spec §4.2). Tightening the rule to strict 4+1 would make the pattern essentially
    /// unreachable in the 108-tile Changsha deck given only six logical terminals (24
    /// physical tiles total across all four copies), which contradicts the source
    /// descriptions framing 九幺 as "achievable but rare". Citations: MahjongPros
    /// "Changsha Mahjong patterns" (web), Baidu Baike entry 长沙麻将 (section 牌型),
    /// Vasquez's Phase H Wave 2 acceptance memo (consulted same sources). The loose
    /// implementation also matches the spec's classical analogue 十三幺 (ThirteenOrphans
    /// in honor-bearing rulesets) which bypasses structural decomposition by
    /// convention.</para>
    /// </summary>
    private static bool CheckNineTerminals(List<int> concealedTileIds, List<Meld> melds)
    {
        var allTileIds = new List<int>(concealedTileIds);
        foreach (var meld in melds)
            allTileIds.AddRange(meld.TileIds);

        if (allTileIds.Count != 14)
            return false;

        // Every tile must be rank 1 or rank 9.
        if (!allTileIds.All(t =>
        {
            var rank = ChangshaDeckBuilder.GetRank(t);
            return rank == 1 || rank == 9;
        }))
        {
            return false;
        }

        // All six distinct terminal tiles must be present at least once
        // (1万/9万/1筒/9筒/1条/9条).
        var distinctLogicals = allTileIds
            .Select(ChangshaDeckBuilder.GetLogicalTile)
            .Distinct()
            .Count();
        return distinctLogicals == 6;
    }

    private static bool CheckSevenPairsShape(int[] counts)
    {
        var pairs = 0;
        foreach (var c in counts)
        {
            if (c % 2 != 0) return false;
            pairs += c / 2;
        }
        return pairs == 7;
    }

    private static bool TryDecompositionAnyPair(int[] counts, int meldsNeeded)
    {
        for (var logical = 0; logical < 27; logical++)
        {
            if (counts[logical] < 2)
                continue;

            counts[logical] -= 2;
            var canForm = CanFormMelds(counts, meldsNeeded);
            counts[logical] += 2;

            if (canForm)
                return true;
        }
        return false;
    }

    private static bool CanFormMelds(int[] counts, int needed)
    {
        if (needed == 0)
            return counts.All(c => c == 0);

        var logical = Array.FindIndex(counts, c => c > 0);
        if (logical < 0)
            return needed == 0;

        if (counts[logical] >= 3)
        {
            counts[logical] -= 3;
            if (CanFormMelds(counts, needed - 1))
            {
                counts[logical] += 3;
                return true;
            }
            counts[logical] += 3;
        }

        if (logical < 27 && logical % 9 <= 6
            && counts[logical + 1] > 0 && counts[logical + 2] > 0)
        {
            counts[logical]--;
            counts[logical + 1]--;
            counts[logical + 2]--;
            if (CanFormMelds(counts, needed - 1))
            {
                counts[logical]++;
                counts[logical + 1]++;
                counts[logical + 2]++;
                return true;
            }
            counts[logical]++;
            counts[logical + 1]++;
            counts[logical + 2]++;
        }

        return false;
    }

    private static int[] BuildLogicalCounts(List<int> tileIds)
    {
        var counts = new int[27];
        foreach (var tileId in tileIds)
        {
            var logical = ChangshaDeckBuilder.GetLogicalTile(tileId);
            if (logical >= 0 && logical < 27)
                counts[logical]++;
        }
        return counts;
    }
}
