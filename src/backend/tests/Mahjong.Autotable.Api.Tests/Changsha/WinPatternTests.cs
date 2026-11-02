using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-F: Win Pattern detection.
/// </summary>
public class WinPatternTests
{
    private static readonly ChangshaWinDetector Detector = new();

    [Fact, Trait("Category", "Changsha")]
    public void Standard_FourMeldsPlusValidPair_2Pair_Detected()
    {
        // 4 chows + a pair of 2s.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2));

        var result = Detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.Equal(WinPattern.Standard, result.Pattern);
        Assert.Equal(ScoreCategory.SmallWin, result.Category);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Standard_Pair3_RejectedBy258Rule()
    {
        // 4 melds + pair of 3 (invalid pair rank for Standard).
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 3), (Suit.Tong, 3));

        var result = Detector.Detect(hand);
        Assert.False(result.IsWin);
    }

    [Fact, Trait("Category", "Changsha")]
    public void SevenPairs_ExemptFrom258_Detected()
    {
        // 7 distinct pairs, none of which are 258.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Tong, 6), (Suit.Tong, 6),
            (Suit.Tong, 7), (Suit.Tong, 7),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 9), (Suit.Tiao, 9));

        var result = Detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.Equal(WinPattern.SevenPairs, result.Pattern);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact, Trait("Category", "Changsha")]
    public void AllPungs_FourPungsPlusPair_Detected()
    {
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 3), (Suit.Tiao, 3));

        var result = Detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.Equal(WinPattern.AllPungs, result.Pattern);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.True(result.IsAllPungs);
    }

    [Fact, Trait("Category", "Changsha")]
    public void FullFlush_AllSingleSuit_Detected()
    {
        // 4 chows + pair of 5, all in Wan.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9));

        var result = Detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.True(result.IsFullFlush);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact, Trait("Category", "Changsha")]
    public void NonWin_RandomHand_NotDetected()
    {
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 4), (Suit.Wan, 7),
            (Suit.Tong, 2), (Suit.Tong, 5), (Suit.Tong, 8),
            (Suit.Tiao, 3), (Suit.Tiao, 6), (Suit.Tiao, 9),
            (Suit.Wan, 2), (Suit.Tong, 1), (Suit.Tiao, 1),
            (Suit.Wan, 9), (Suit.Tong, 9));

        var result = Detector.Detect(hand);
        Assert.False(result.IsWin);
    }

    // ── Phase H Wave 2 — V2 rules: NineTerminals / RobbingKong / Stacked Big Wins ──
    //
    // Bishop's Wave 2 contract (per Ripley's Phase H design memo §2):
    //   - WinPattern.NineTerminals — Changsha-adapted "9-Terminals" (九幺) Big Win
    //     for an all rank-1/rank-9 hand containing all six terminal tiles. Replaces
    //     ThirteenOrphans (impossible in Changsha — no honor tiles).
    //   - WinDetectionResult.AllPatterns — IReadOnlyList<WinPattern> populated with
    //     every Big Win pattern the hand satisfies (enables stacked multipliers).
    //   - WinDetector.Detect(hand, winningTileId, WinMethod.RobbingKong) accepts
    //     a robbing-kong claimer hand and validates as a discard-win.
    //
    // Each test below uses reflection (Enum.TryParse for WinPattern.NineTerminals;
    // PropertyInfo for AllPatterns) so the test assembly compiles whether Bishop has
    // pushed his contract yet or not. RED-fail messages name the missing symbol.

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "2")]
    public void NineTerminals_RankBoundsOnly()
    {
        // Phase H Wave 2 §2.1: Changsha's 108-tile deck has no honors, so the classical
        // 13-Orphans is structurally impossible. The V2 analog is the all-1/9 "9-Terminals"
        // (九幺) hand — 14 tiles all of rank 1 or rank 9, containing each of the six
        // terminal tiles (1/9 of each suit) at least once.
        var nineTerminals = ResolveNineTerminalsEnum();

        // 14-tile all-terminals hand containing all 6 distinct terminals:
        //   1万×3, 9万×3, 1筒×3, 9筒×2, 1条×2, 9条×1 = 14 tiles, all rank 1/9, six distinct.
        // Deliberately NOT a SevenPairs / AllPungs / Standard win on its own — only the
        // NineTerminals branch should fire.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 1), (Suit.Tong, 1), (Suit.Tong, 1),
            (Suit.Tong, 9), (Suit.Tong, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 9));

        var result = Detector.Detect(hand);
        Assert.True(result.IsWin,
            $"NineTerminals hand must be a valid Hu (V2 §2.1). " +
            $"Detector returned IsWin=false; Bishop owes the WinPattern.NineTerminals detection branch.");

        // The hand should classify as a Big Win (every published 9-terminals variant is Big).
        Assert.Equal(ScoreCategory.BigWin, result.Category);

        // The detected pattern (or AllPatterns) must include NineTerminals.
        var allPatterns = ResolveAllPatterns(result);
        var patternMatch = result.Pattern == nineTerminals
            || (allPatterns is not null && allPatterns.Contains(nineTerminals));
        Assert.True(patternMatch,
            $"WinDetector must flag NineTerminals for an all-rank-1/9 hand with all six distinct " +
            $"terminals. Got Pattern={result.Pattern}, AllPatterns=[{(allPatterns is null ? "<missing>" : string.Join(",", allPatterns))}].");
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "2")]
    public void RobbingKong_Win_DetectorAcceptsKongTileAsWinningTile()
    {
        // Phase H Wave 2 §2.2: Robbing-the-added-kong (抢杠胡) win — the claimer's hand
        // completes on the tile being added to an existing exposed pung. The detector
        // already accepts a (hand, winningTileId, WinMethod) triple; this test pins the
        // RobbingKong method-tag contract: the detector treats the kong-target tile
        // exactly like a discard-win candidate.
        //
        // Hand structure: 13 concealed tiles waiting on 5万 to complete
        //   chow 1-2-3, chow 4-?-6 (needs 5万), chow 7-8-9, pung Tong-1, pair Tong-5 (258).
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Tong, 1), (Suit.Tong, 1), (Suit.Tong, 1),
            (Suit.Tong, 5), (Suit.Tong, 5));

        var winningTileId = Tid(Suit.Wan, 5, 0);
        var result = Detector.Detect(hand, winningTileId, WinMethod.RobbingKong);

        Assert.True(result.IsWin,
            $"WinDetector must accept the kong-target tile as a valid winning tile when invoked " +
            $"with WinMethod.RobbingKong. Hand should complete as 4 melds + pair (258) on Wan-5. " +
            $"Bishop owes the RobbingKong-method-aware detection path.");
        Assert.Equal(WinPattern.Standard, result.Pattern);
        Assert.Equal(ScoreCategory.SmallWin, result.Category);
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "2")]
    public void StackedBigWinPatterns_AllPungsPlusFullFlush_PopulatesAllPatterns()
    {
        // Phase H Wave 2 §2.3: WinDetectionResult.AllPatterns enumerates every Big Win
        // pattern the hand satisfies, enabling score-multiplier stacking. For an all-Wan
        // all-pungs hand, both AllPungs AND FullFlush must appear in AllPatterns.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 4), (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 7), (Suit.Wan, 7), (Suit.Wan, 7),
            (Suit.Wan, 2), (Suit.Wan, 2));

        var result = Detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.True(result.IsAllPungs);
        Assert.True(result.IsFullFlush);

        var allPatterns = ResolveAllPatterns(result)
            ?? throw new InvalidOperationException(
                "WinDetectionResult.AllPatterns property not found — Bishop owes the Phase H Wave 2 " +
                "contract (IReadOnlyList<WinPattern> AllPatterns on WinDetectionResult).");

        Assert.True(allPatterns.Count >= 2,
            $"AllPungs+FullFlush hand must surface 2+ patterns in AllPatterns. Got [{string.Join(",", allPatterns)}].");
        Assert.Contains(WinPattern.AllPungs, allPatterns);
        Assert.Contains(WinPattern.FullFlush, allPatterns);
    }

    // ── Phase H Wave 2 — reflective helpers ──────────────────────────────────────

    /// <summary>
    /// Resolve <c>WinPattern.NineTerminals</c> by name so the test assembly compiles
    /// even before Bishop adds the enum value. Fails RED with a descriptive message.
    /// </summary>
    internal static WinPattern ResolveNineTerminalsEnum()
    {
        var names = Enum.GetNames(typeof(WinPattern));
        if (!names.Contains("NineTerminals"))
        {
            throw new InvalidOperationException(
                "WinPattern.NineTerminals enum value not defined — Bishop owes the Phase H Wave 2 " +
                "contract (see Ripley's design memo §2.1). Current values: [" +
                string.Join(",", names) + "].");
        }
        return (WinPattern)Enum.Parse(typeof(WinPattern), "NineTerminals");
    }

    /// <summary>
    /// Read <c>WinDetectionResult.AllPatterns</c> by reflection so the test assembly
    /// compiles before Bishop ships the property. Returns <c>null</c> when the property
    /// is missing — callers decide whether absence is fatal or acceptable.
    /// </summary>
    internal static IReadOnlyList<WinPattern>? ResolveAllPatterns(WinDetectionResult result)
    {
        var prop = typeof(WinDetectionResult).GetProperty("AllPatterns",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return null;
        var value = prop.GetValue(result);
        return value switch
        {
            IReadOnlyList<WinPattern> readOnlyList => readOnlyList,
            IEnumerable<WinPattern> enumerable => enumerable.ToList(),
            _ => null
        };
    }

    // ── Phase I Wave 1 — contextual Big Win patterns (天和/地和/海底/河底/杠上开花) ──
    //
    // Bishop's Phase I Wave 1 contract (per the Phase I directive + Vasquez spec §4.3):
    //   - 5 new WinPattern enum values: HeavenlyHand, EarthlyHand, LastTileFromWall,
    //     LastDiscardCatch, KongReplacementWin (spec §4.3 draw-based big wins).
    //   - A WinContext (sealed record) carrying 5 boolean flags
    //     (Is{HeavenlyHand,EarthlyHand,LastTileFromWall,LastDiscardCatch,KongReplacementWin}),
    //     threaded through IWinDetector.Detect as an optional final parameter.
    //   - ChangshaGameState.LastDrawWasKongReplacement : bool — transient flag set by
    //     every kong-replacement draw and cleared on the next DrawTile / Discard.
    //
    // Tests below use reflection (Enum.GetNames for the 5 new patterns; Type / PropertyInfo
    // probes for WinContext + LastDrawWasKongReplacement) so the test assembly compiles
    // whether Bishop has pushed his contract yet or not. RED-fail messages name the
    // missing symbol so Bishop has a build-time grep target for the contract owed.

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void ContextualWinPatterns_AllFiveEnumValuesDefined()
    {
        // Phase I Wave 1 §1: 5 new contextual Big Win patterns must be defined on the
        // WinPattern enum. Names are canonical per the Phase I directive.
        var names = Enum.GetNames(typeof(WinPattern));
        var expected = new[]
        {
            "HeavenlyHand", "EarthlyHand", "LastTileFromWall",
            "LastDiscardCatch", "KongReplacementWin"
        };
        var missing = expected.Where(n => !names.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            $"WinPattern is missing Phase I Wave 1 contextual values: [{string.Join(",", missing)}]. " +
            $"Current values: [{string.Join(",", names)}]. Bishop owes the Phase I Wave 1 contract.");
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void ChangshaGameState_HasLastDrawWasKongReplacement_BooleanProperty()
    {
        // Phase I Wave 1 §1 — KongReplacementWin (杠上开花) gating signal. The state
        // machine sets this true on every kong-replacement draw and clears it on the
        // next regular DrawTile or Discard, so a fresh self-draw "knows" whether the
        // tile in question was a kong replacement.
        var prop = typeof(ChangshaGameState).GetProperty("LastDrawWasKongReplacement",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
        Assert.True(prop.CanRead && prop.CanWrite,
            "LastDrawWasKongReplacement must be a public read/write bool property (state machine mutates it).");

        // Default must be false so a fresh ChangshaGameState (created via the parameterless
        // initializer used everywhere) does not accidentally trigger KongReplacementWin
        // on the dealer's initial 14-tile hand.
        var fresh = new ChangshaGameState();
        Assert.False((bool)prop.GetValue(fresh)!,
            "ChangshaGameState.LastDrawWasKongReplacement must default to false " +
            "(a fresh state must not retroactively flag HeavenlyHand wins as KongReplacementWin).");
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void WinDetector_AcceptsContextualWinContext_OptionalParameter()
    {
        // Phase I Wave 1 §1 — the detector must accept the contextual flags through a
        // dedicated WinContext type passed as an OPTIONAL trailing parameter on
        // IWinDetector.Detect (default null preserves identical pre-Phase-I behaviour
        // for every existing caller — bots, replay reconstruction, xUnit fixtures).
        //
        // This test pins the shape of Bishop's contract so future refactors don't
        // silently change the parameter name / nullability / default.
        var contextType = typeof(WinDetectionResult).Assembly.GetType(
            "Mahjong.Autotable.Api.Changsha.WinContext");
        Assert.NotNull(contextType);

        var expectedFlags = new[]
        {
            "IsHeavenlyHand", "IsEarthlyHand", "IsLastTileFromWall",
            "IsLastDiscardCatch", "IsKongReplacementWin"
        };
        var missing = expectedFlags
            .Where(f => contextType!.GetProperty(f, BindingFlags.Public | BindingFlags.Instance) is null)
            .ToList();
        Assert.True(missing.Count == 0,
            $"WinContext is missing Phase I Wave 1 flag properties: [{string.Join(",", missing)}]. " +
            $"Bishop owes the Phase I Wave 1 contract (sealed record WinContext with 5 bool init-only flags).");

        // The IWinDetector.Detect overload must accept WinContext as a trailing
        // optional parameter — defaulting to null so legacy callers compile unchanged.
        var detectMethod = typeof(IWinDetector).GetMethod("Detect",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(detectMethod);
        var contextParam = detectMethod!.GetParameters()
            .FirstOrDefault(p => p.ParameterType == contextType
                                  || (Nullable.GetUnderlyingType(p.ParameterType) == contextType)
                                  || p.ParameterType.FullName == contextType!.FullName + "&"
                                  || p.ParameterType.FullName?.Contains("WinContext") == true);
        Assert.NotNull(contextParam);
        Assert.True(contextParam!.IsOptional,
            "WinContext parameter on IWinDetector.Detect must be optional " +
            "(default null preserves pre-Phase-I behaviour for every legacy call site).");
    }

    [Theory, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    [InlineData("HeavenlyHand", "IsHeavenlyHand", "天和")]
    [InlineData("EarthlyHand", "IsEarthlyHand", "地和")]
    [InlineData("LastTileFromWall", "IsLastTileFromWall", "海底捞月")]
    [InlineData("LastDiscardCatch", "IsLastDiscardCatch", "河底捞鱼")]
    [InlineData("KongReplacementWin", "IsKongReplacementWin", "杠上开花")]
    public void ContextualPattern_PopulatesAllPatterns_WhenContextFlagSetOnValidHand(
        string patternName, string contextFlag, string chinese)
    {
        // Phase I Wave 1 §1 — for each contextual pattern, when the WinContext flag is
        // true AND the hand is otherwise a structurally valid mahjong win (Standard 4+1
        // here — the most basic baseline), the detector must:
        //   (a) report IsWin == true,
        //   (b) classify Category == BigWin (contextual patterns promote Small Wins),
        //   (c) include the new pattern in AllPatterns.
        //
        // The hand below is a 4-chow + 258-pair Standard win (NOT FullFlush, NOT AllPungs,
        // NOT SevenPairs, NOT NineTerminals) so the only firing pattern is the contextual
        // one under test — proving the contextual gating works in isolation.
        var names = Enum.GetNames(typeof(WinPattern));
        Assert.Contains(patternName, names);
        var pattern = (WinPattern)Enum.Parse(typeof(WinPattern), patternName);

        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));

        var context = BuildWinContextWithFlag(contextFlag, chinese);
        var result = InvokeDetect(hand, winningTileId: null, WinMethod.SelfDraw, context);

        Assert.True(result.IsWin,
            $"Detector must accept a structurally valid Standard hand + WinContext.{contextFlag} ({chinese}). " +
            $"Got IsWin=false.");
        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Contains(pattern, result.AllPatterns);
    }

    // ── Phase I Wave 1 — reflection probe helpers ──────────────────────────────

    /// <summary>
    /// Build a <c>WinContext</c> instance with the given boolean flag set to true,
    /// via reflection so the test assembly compiles before Bishop ships the type.
    /// Throws a RED-message <c>InvalidOperationException</c> when the type or flag
    /// is missing so Bishop has a grep target for the contract owed.
    /// </summary>
    private static object BuildWinContextWithFlag(string flagName, string chinese)
    {
        var contextType = typeof(WinDetectionResult).Assembly.GetType(
            "Mahjong.Autotable.Api.Changsha.WinContext")
            ?? throw new InvalidOperationException(
                $"WinContext type not found in Mahjong.Autotable.Api.Changsha namespace — " +
                $"Bishop owes the Phase I Wave 1 contract (sealed record WinContext with 5 init-only bools).");

        var instance = Activator.CreateInstance(contextType)
            ?? throw new InvalidOperationException(
                $"WinContext could not be instantiated via parameterless ctor — " +
                $"Bishop's record must support 'new WinContext() {{ /* init-set */ }}'.");

        var prop = contextType.GetProperty(flagName,
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"WinContext.{flagName} ({chinese}) property not found — " +
                $"Bishop owes the Phase I Wave 1 contract. " +
                $"Existing flags: [{string.Join(",", contextType.GetProperties().Select(p => p.Name))}].");

        // For init-only properties, the setter is technically present (init = SetMethod
        // tagged with IsExternalInit) — reflection's SetValue still works for tests.
        prop.SetValue(instance, true);
        return instance;
    }

    /// <summary>
    /// Invoke <see cref="IWinDetector.Detect"/> with a reflection-built
    /// <c>WinContext</c> so we don't take a direct compile-time dependency on
    /// Bishop's not-yet-merged shape. Falls through to the legacy 3-arg overload
    /// (which RED-fails Pattern-not-set on the contextual asserts) if the new
    /// 4-arg overload is missing.
    /// </summary>
    private static WinDetectionResult InvokeDetect(
        ChangshaHandState hand, int? winningTileId, WinMethod method, object context)
    {
        var detector = new ChangshaWinDetector();
        var detectMethod = typeof(IWinDetector).GetMethod("Detect",
            BindingFlags.Public | BindingFlags.Instance);
        if (detectMethod is null)
            throw new InvalidOperationException("IWinDetector.Detect method not found.");

        var parameters = detectMethod.GetParameters();
        // Pad parameters with Type.Missing for any optional ones we don't supply.
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            args[i] = parameters[i].Name switch
            {
                "hand" => hand,
                "winningTileId" => winningTileId,
                "method" => method,
                "context" => context,
                _ => Type.Missing
            };
        }
        var result = detectMethod.Invoke(detector, args);
        return (WinDetectionResult)result!;
    }
}
