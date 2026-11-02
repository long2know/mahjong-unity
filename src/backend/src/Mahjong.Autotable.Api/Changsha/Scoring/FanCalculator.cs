namespace Mahjong.Autotable.Api.Changsha.Scoring;

/// <summary>
/// Immutable snapshot of a winning hand for fan detection. Independent of
/// <see cref="ChangshaHandState"/> so the calculator can be invoked from any
/// caller (state machine, future scoring path, replay rewind, frontend audit
/// query). <see cref="ConcealedTileIds"/> MUST already include the winning
/// tile (mirrors how <see cref="ChangshaWinDetector"/> takes the hand). The
/// <see cref="WinningTileId"/> field is informational only — used to identify
/// whether a particular fan check is sensitive to which tile completed the
/// hand (e.g. robbing-kong identification by caller).
/// </summary>
public sealed record WinningHand
{
    public required IReadOnlyList<int> ConcealedTileIds { get; init; }
    public required IReadOnlyList<Meld> Melds { get; init; }
    public int? WinningTileId { get; init; }
}

/// <summary>
/// Per-win context flags consumed by <see cref="FanCalculator.EvaluateHand"/>.
/// Each flag is independent and gates exactly one <see cref="Fan"/>. The caller
/// is responsible for validating each condition before setting a flag true
/// (e.g. dealer-only, first-discard-only, wall-count-zero). The calculator
/// trusts the flags.
///
/// <para>This is deliberately a SIBLING of the existing
/// <see cref="WinContext"/> (in <see cref="ChangshaWinDetector"/>) rather than
/// a reuse: <see cref="WinContext"/> drives structural pattern gating inside
/// the detector; this record drives the additive fan-bonus layer in
/// <see cref="FanCalculator"/>. Keeping them separate prevents accidental
/// coupling — a future ruleset can change the fan bonus payload without
/// touching the detector contract.</para>
/// </summary>
public sealed record FanContext
{
    /// <summary>自摸 — winning tile was drawn from the wall (not claimed).</summary>
    public bool IsSelfDraw { get; init; }

    /// <summary>杠上开花 — winning tile is the replacement drawn after a kong
    /// declaration. Implies <see cref="IsSelfDraw"/>.</summary>
    public bool IsKongReplacement { get; init; }

    /// <summary>海底捞月 — self-draw won on the very last tile of the wall.</summary>
    public bool IsLastTileFromWall { get; init; }

    /// <summary>河底捞鱼 — discard Hu on a tile thrown after the wall was exhausted.</summary>
    public bool IsLastDiscardCatch { get; init; }

    /// <summary>抢杠 — winning tile was robbed from another seat's added-kong
    /// upgrade. Always paired with <see cref="MeldKind.AddedKong"/> on the
    /// loser's side (concealed kongs are not robbable per spec §3.4.3).</summary>
    public bool IsRobbingKong { get; init; }

    /// <summary>天和 — dealer self-draw on the initial 14-tile hand. Caller is
    /// responsible for verifying no intervening claims/discards/kong-replacements.</summary>
    public bool IsHeavenlyHand { get; init; }

    /// <summary>地和 — non-dealer wins on the dealer's very first discard.</summary>
    public bool IsEarthlyHand { get; init; }

    /// <summary>Winning seat's wind. Reserved for future seat-wind / round-wind
    /// fans (东南西北圈风/门风); not consumed by any currently-emitted fan but
    /// reserved on the wire so callers can populate it now.</summary>
    public Wind SeatWind { get; init; } = Wind.East;

    /// <summary>Round wind. See <see cref="SeatWind"/>.</summary>
    public Wind RoundWind { get; init; } = Wind.East;

    /// <summary>Variant gate. Default <see cref="FanVariant.Changsha"/> emits
    /// only Changsha-tagged fans; <see cref="FanVariant.ExpandedChinese"/>
    /// also unlocks 混一色 and 大三元.</summary>
    public FanVariant Variant { get; init; } = FanVariant.Changsha;
}

/// <summary>
/// A single detected fan with its point value. Constructed by
/// <see cref="FanCalculator.EvaluateHand"/>.
/// </summary>
public sealed record DetectedFan(Fan Fan, int Points);

/// <summary>
/// Result of <see cref="FanCalculator.EvaluateHand"/>. Detected fans are listed
/// in deterministic enum-declaration order; <see cref="TotalPoints"/> is the
/// sum of every detected fan's points.
/// </summary>
public sealed record FanResult
{
    public required IReadOnlyList<DetectedFan> Detected { get; init; }
    public int TotalPoints => Detected.Sum(d => d.Points);

    public static FanResult Empty { get; } = new() { Detected = Array.Empty<DetectedFan>() };

    public bool Has(Fan fan) => Detected.Any(d => d.Fan == fan);
}

/// <summary>
/// Pure-function fan catalog evaluator for Changsha (and variant) Mahjong scoring.
/// Layered ADDITIVELY on top of <see cref="ScoringService"/>'s small-/big-win
/// tier base. Stateless; safe to call from any thread.
///
/// <para><b>Detection sources:</b>
/// <list type="bullet">
///   <item>Structural fans (FullFlush / SevenPairs / AllPungs / NineTerminals /
///         MixedOneSuit) — delegated to <see cref="ChangshaWinDetector"/> where
///         possible, re-derived locally for honor-bearing variants.</item>
///   <item>Concealment fans (ConcealedHand) — meld-shape analysis: every meld
///         must be either <see cref="MeldKind.ConcealedKong"/> or absent.</item>
///   <item>Situational fans (SelfDraw / KongReplacement / LastTileFromWall /
///         LastDiscardCatch / RobbingKong / Heavenly / Earthly) — read straight
///         from <see cref="FanContext"/> flags.</item>
///   <item>Variant-gated fans (MixedOneSuit / BigThreeDragons) — emitted only
///         when <see cref="FanContext.Variant"/> is
///         <see cref="FanVariant.ExpandedChinese"/>.</item>
/// </list></para>
///
/// <para><b>Integration status (issue #117):</b> with respect to the authoritative
/// <b>payment amounts</b> this calculator is <b>query-only by default</b>. The live
/// Changsha scoring path (<see cref="ChangshaGameStateMachine.Score"/> →
/// <see cref="ScoringService.CalculateScore(WinResult,int,bool)"/>) evaluates the
/// catalog and surfaces the breakdown on <see cref="ScoreResult.Fans"/> /
/// <see cref="ScoreResult.FanPoints"/> for display/audit, but does NOT fold fan
/// points into the payments — the money stays at the binding spec §5.1 magnitude
/// (<see cref="ChangshaScoringOptions.SpecPure"/>, the default). A future
/// tournament/house-rule mode (<see cref="ChangshaScoringOptions.HouseRules"/>) can
/// opt in to the additive fan layer; frontend / replay / future-variant callers can
/// always invoke <see cref="EvaluateHand"/> directly to render a fan breakdown.</para>
/// </summary>
public static class FanCalculator
{
    /// <summary>
    /// Evaluates the winning hand and emits every detected fan that the
    /// <paramref name="ctx"/> variant allows. Fans are returned in
    /// deterministic <see cref="Fan"/>-enum-declaration order. Returns
    /// <see cref="FanResult.Empty"/> when no fan applies (e.g. a plain
    /// 258-pair Standard win claimed off a discard).
    /// </summary>
    public static FanResult EvaluateHand(WinningHand hand, FanContext ctx)
    {
        var detected = new List<DetectedFan>();

        // Run the structural detector FIRST so all win-only fans (situational,
        // ConcealedHand, prestige) can gate on detection.IsWin. Per the class-
        // level contract, EvaluateHand must return FanResult.Empty when no fan
        // applies — including the entire "non-winning hand" case. Previously,
        // SelfDraw / KongReplacement / LastTileFromWall / LastDiscardCatch /
        // RobbingKong / ConcealedHand emitted from ctx flags or trivially-true
        // meld checks on hands that were not structurally winning (e.g. empty
        // hand, 13-tile partial, 14-tile non-Hu shape). Production was masked
        // because the state machine only invokes Score on a detector-confirmed
        // win, but the XML doc markets query-only use (replay, frontend audit)
        // and any future caller would see phantom fans on non-wins.
        var detection = RunDetector(hand);

        // ── Situational / method fans (from ctx flags, gated on IsWin) ─────
        if (detection.IsWin)
        {
            if (ctx.IsSelfDraw)
                Add(detected, Fan.SelfDraw);

            if (ctx.IsKongReplacement)
                Add(detected, Fan.KongReplacement);

            if (ctx.IsLastTileFromWall)
                Add(detected, Fan.LastTileFromWall);

            if (ctx.IsLastDiscardCatch)
                Add(detected, Fan.LastDiscardCatch);

            if (ctx.IsRobbingKong)
                Add(detected, Fan.RobbingKong);
        }

        // ── Structural fans (delegated to ChangshaWinDetector) ─────
        if (detection.IsFullFlush)
            Add(detected, Fan.FullFlush);

        // Variant-gated fans (MixedOneSuit / BigThreeDragons) are intentionally
        // NOT gated by detection.IsWin so the v1 forward-compat tests (which
        // use phantom expanded-deck logical IDs the detector doesn't recognise)
        // keep working in advance of an expanded-deck WinDetector.
        if (IsMixedOneSuit(hand, ctx))
            Add(detected, Fan.MixedOneSuit);

        if (detection.IsSevenPairs)
            Add(detected, Fan.SevenPairs);

        if (detection.IsAllPungs)
            Add(detected, Fan.AllPungs);

        if (detection.IsWin && IsConcealedHand(hand.Melds))
            Add(detected, Fan.ConcealedHand);

        if (IsBigThreeDragons(hand, ctx))
            Add(detected, Fan.BigThreeDragons);

        // ── Prestige / contextual structural fans ──────────────────
        if (ctx.IsHeavenlyHand && detection.IsWin)
            Add(detected, Fan.HeavenlyHand);

        if (ctx.IsEarthlyHand && detection.IsWin)
            Add(detected, Fan.EarthlyHand);

        if (detection.Pattern == WinPattern.NineTerminals
            || detection.AllPatterns.Contains(WinPattern.NineTerminals))
        {
            Add(detected, Fan.NineTerminals);
        }

        // Variant-gated emission — drop any fan whose declared FanInfo.Variant
        // exceeds the caller's ctx.Variant. This is the SOLE filter point so
        // a future variant flip needs no detector edits.
        var filtered = detected
            .Where(d => (int)FanCatalog.Get(d.Fan).Variant <= (int)ctx.Variant)
            .OrderBy(d => (int)d.Fan)
            .ToList();

        return new FanResult { Detected = filtered };
    }

    private static void Add(List<DetectedFan> list, Fan fan)
    {
        if (list.Any(d => d.Fan == fan)) return;
        var info = FanCatalog.Get(fan);
        list.Add(new DetectedFan(fan, info.Points));
    }

    private static WinDetectionResult RunDetector(WinningHand hand)
    {
        // The detector takes a ChangshaHandState; mirror the inputs. SeatIndex
        // is irrelevant to the detector's structural checks (it only consults
        // ConcealedTiles + Melds), so 0 is fine.
        var snapshot = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = hand.ConcealedTileIds.ToList(),
            Melds = hand.Melds.ToList(),
        };
        return new ChangshaWinDetector().Detect(snapshot);
    }

    /// <summary>
    /// 门清 — true iff every meld is a concealed kong (or there are no melds at
    /// all). Pungs, chows, exposed kongs, and added kongs all break concealment
    /// because the claimed tile came from an opponent. Concealed kongs are
    /// self-drawn so they retain concealment per classical scoring.
    /// </summary>
    private static bool IsConcealedHand(IReadOnlyList<Meld> melds)
    {
        foreach (var m in melds)
        {
            if (m.Kind != MeldKind.ConcealedKong)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 混一色 — variant-gated. Returns false in pure Changsha (no honors in
    /// the 108-tile deck, so the fan is unreachable). Under the expanded
    /// variant: every tile in the hand belongs to ONE suit OR is an honor
    /// tile, with at least one honor present (otherwise the hand is FullFlush).
    /// </summary>
    private static bool IsMixedOneSuit(WinningHand hand, FanContext ctx)
    {
        if (ctx.Variant != FanVariant.ExpandedChinese) return false;

        var allTileIds = new List<int>(hand.ConcealedTileIds);
        foreach (var meld in hand.Melds)
            allTileIds.AddRange(meld.TileIds);

        if (allTileIds.Count == 0) return false;

        // In the pure Changsha 108-tile deck no honor tiles exist, so all tile
        // ids map to one of the three suits. The expanded variant would carry
        // a separate honors-suit; until that deck arrives, MixedOneSuit can
        // never fire — we return false on a no-honors hand.
        // The "honor tile" hook is intentionally a strict false here so the
        // logic is forward-compatible: a future expanded-deck builder that
        // emits honor tile ids outside the 0..107 range will flip this branch
        // without further changes.
        var nonHonorCount = 0;
        Suit? firstSuit = null;
        var hasHonor = false;
        foreach (var t in allTileIds)
        {
            if (IsHonorTile(t))
            {
                hasHonor = true;
                continue;
            }
            nonHonorCount++;
            var suit = ChangshaDeckBuilder.GetSuit(t);
            if (firstSuit is null) firstSuit = suit;
            else if (suit != firstSuit.Value) return false;
        }
        return hasHonor && nonHonorCount > 0;
    }

    /// <summary>
    /// 大三元 — variant-gated. Returns true when the hand contains a pung or
    /// kong of EACH of the three dragon tiles (红中 / 發 / 白板). Pure
    /// Changsha never satisfies this because the 108-tile deck has no
    /// dragons; the check shape is reserved here so a future
    /// expanded-Chinese deck builder can flip the gate without touching the
    /// detector.
    /// </summary>
    private static bool IsBigThreeDragons(WinningHand hand, FanContext ctx)
    {
        if (ctx.Variant != FanVariant.ExpandedChinese) return false;

        // Future expanded-deck tile-id encoding will reserve a contiguous
        // range for dragons (中/發/白). Until that arrives, the helper
        // returns false on any 0..107-id hand because none of those map to
        // dragon tiles.
        var seenDragons = new HashSet<DragonKind>();
        foreach (var meld in hand.Melds)
        {
            if (meld.Kind is not (MeldKind.Pung or MeldKind.ExposedKong
                or MeldKind.ConcealedKong or MeldKind.AddedKong))
                continue;
            if (meld.TileIds.Count == 0) continue;
            var dragon = TryGetDragon(meld.TileIds[0]);
            if (dragon is not null) seenDragons.Add(dragon.Value);
        }

        // Concealed pungs aren't melded; if all three dragons are concealed in
        // the hand they still satisfy 大三元. Detect concealed pungs by
        // counting honor tile ids in ConcealedTileIds.
        var concealedHonorCounts = new Dictionary<DragonKind, int>();
        foreach (var t in hand.ConcealedTileIds)
        {
            var d = TryGetDragon(t);
            if (d is null) continue;
            concealedHonorCounts[d.Value] = concealedHonorCounts.GetValueOrDefault(d.Value) + 1;
        }
        foreach (var (kind, count) in concealedHonorCounts)
        {
            if (count >= 3) seenDragons.Add(kind);
        }

        return seenDragons.Count == 3;
    }

    /// <summary>
    /// Honor-tile predicate. Pure Changsha has none (108 tiles 0..107 all
    /// suits), so this returns false for every tile id in the current deck.
    /// Reserved hook for future expanded-deck builders.
    /// </summary>
    private static bool IsHonorTile(int tileId)
    {
        // Expanded-deck encoding placeholder: any tile id outside the pure
        // Changsha range is considered an honor / wind / dragon. The pure
        // Changsha deck builder only emits 0..107.
        return tileId < 0 || tileId >= ChangshaDeckBuilder.TotalTiles;
    }

    private enum DragonKind { Red = 0, Green = 1, White = 2 }

    /// <summary>
    /// Maps a tile id to a <see cref="DragonKind"/> if the id belongs to the
    /// future expanded-deck dragon range. Pure Changsha returns null for
    /// every input. The numeric range below is the convention we will adopt
    /// when the expanded deck lands (Changsha tail = 108, dragons 108..119
    /// in groups of 4); kept here so the future deck builder doesn't need to
    /// re-jig this branch.
    /// </summary>
    private static DragonKind? TryGetDragon(int tileId)
    {
        // Reserved range for dragons in the future expanded deck:
        //   108..111 = 中 (Red)
        //   112..115 = 發 (Green)
        //   116..119 = 白 (White)
        // No pure-Changsha tile id falls in this range, so the helper
        // returns null for every current input.
        return tileId switch
        {
            >= 108 and <= 111 => DragonKind.Red,
            >= 112 and <= 115 => DragonKind.Green,
            >= 116 and <= 119 => DragonKind.White,
            _ => null,
        };
    }
}
