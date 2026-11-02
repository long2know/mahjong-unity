using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Stacked Big-Win pattern scoring (Phase H Wave 2 §2.3).
///
/// A single hand can satisfy multiple Big Win flags simultaneously
/// (e.g. AllPungs + FullFlush — an all-Wan all-pungs hand). The new
/// 4-arg <c>ScoringService.CalculateScore(WinResult, dealerSeatIndex, isFullFlush,
/// bigWinPatternCount)</c> overload applies a stacking multiplier:
///   1 pattern → ×1 (baseline Big Win payment)
///   2 patterns → ×2
///   3+ patterns → ×3 cap
/// The pattern count comes from <c>WinDetectionResult.AllPatterns.Count</c>,
/// which the detector populates in deterministic enum-declaration order:
/// SevenPairs &lt; AllPungs &lt; FullFlush &lt; NineTerminals.
///
/// Tests reach for the 4-arg overload via reflection so the assembly compiles
/// regardless of when Bishop's contract lands; RED messages name the missing
/// symbol. Bishop owes the wiring from <c>ChangshaGameStateMachine.Score()</c>
/// to the 4-arg overload (passing AllPatterns.Count) for the end-to-end pipeline
/// to honor the multiplier — but that is exercised by the
/// <see cref="EdgeCaseTests.MultipleBigWinPatterns_ScoresStack_DeferredToV2"/>
/// edge-case fact, NOT here.
/// </summary>
public class StackedBigWinScoringTests
{
    private static readonly ChangshaWinDetector Detector = new();

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void AllPungs_Alone_Multiplier_Is_1x()
    {
        // Baseline: AllPungs without FullFlush — 1 Big Win pattern, multiplier ×1.
        // Discard win, non-dealer winner, non-dealer source → BigWinDiscardBase = 6.
        var win = BuildWinResult(WinPattern.AllPungs, WinMethod.Discard,
            winningSeat: 1, sourceSeat: 2, isFullFlush: false);
        var result = InvokeCalculateScore(win, dealerSeatIndex: 0,
            isFullFlush: false, bigWinPatternCount: 1);

        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Equal(6, result.BasePoints);
        var entry = result.Payments.Single();
        Assert.Equal(2, entry.FromSeatIndex);
        Assert.Equal(1, entry.ToSeatIndex);
        Assert.Equal(6, entry.Amount);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void AllPungs_Plus_FullFlush_Multiplier_Is_2x()
    {
        // Stacked: 2 Big Win patterns → multiplier ×2. Same shape as the baseline test,
        // discard win non-dealer/non-dealer, base 6 × 2 = 12.
        var win = BuildWinResult(WinPattern.AllPungs, WinMethod.Discard,
            winningSeat: 1, sourceSeat: 2, isFullFlush: true);
        var result = InvokeCalculateScore(win, dealerSeatIndex: 0,
            isFullFlush: true, bigWinPatternCount: 2);

        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Equal(12, result.BasePoints);
        Assert.Equal(12, result.Payments.Single().Amount);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void SevenPairs_Plus_FullFlush_Multiplier_Is_2x()
    {
        // Different leading pattern (SevenPairs precedence < AllPungs) — same ×2.
        // Self-draw non-dealer + non-dealer involved → 3 × 2 = 6 per opponent (×3 opps).
        var win = BuildWinResult(WinPattern.SevenPairs, WinMethod.SelfDraw,
            winningSeat: 1, sourceSeat: 1, isFullFlush: true);
        var result = InvokeCalculateScore(win, dealerSeatIndex: 0,
            isFullFlush: true, bigWinPatternCount: 2);

        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Equal(3, result.Payments.Count);
        // Per-payment amount: 3 (BigWinSelfDrawBase non-dealer) for seats 2, 3;
        // 4 (BigWinSelfDrawDealer) for seat 0. All ×2 for stacking.
        Assert.Equal(6, result.Payments.Single(p => p.FromSeatIndex == 2).Amount);
        Assert.Equal(6, result.Payments.Single(p => p.FromSeatIndex == 3).Amount);
        Assert.Equal(8, result.Payments.Single(p => p.FromSeatIndex == 0).Amount);
        Assert.Equal(6 + 6 + 8, result.BasePoints);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void Multiplier_ClampedToThree_OnFourPlusPatterns()
    {
        // 4-pattern hands are theoretically possible (e.g. AllPungs + FullFlush +
        // NineTerminals on a Wan-only all-terminals all-pungs hand structure) but
        // multiplier caps at ×3. Discard non-dealer/non-dealer: 6 × 3 = 18.
        var win = BuildWinResult(WinPattern.AllPungs, WinMethod.Discard,
            winningSeat: 1, sourceSeat: 2, isFullFlush: true);
        var result = InvokeCalculateScore(win, dealerSeatIndex: 0,
            isFullFlush: true, bigWinPatternCount: 4);

        Assert.Equal(18, result.BasePoints);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void SmallWin_NeverStacks_RegardlessOfPatternCount()
    {
        // Standard (Small Win) patterns are immune to the multiplier — the field is
        // ignored for Small Wins because no Big Win flag ever fires. Defensive guard
        // to make sure the multiplier doesn't leak into the small-win path.
        var win = BuildWinResult(WinPattern.Standard, WinMethod.Discard,
            winningSeat: 1, sourceSeat: 2, isFullFlush: false);
        var resultDefault = InvokeCalculateScore(win, dealerSeatIndex: 0,
            isFullFlush: false, bigWinPatternCount: 1);
        var resultStacked = InvokeCalculateScore(win, dealerSeatIndex: 0,
            isFullFlush: false, bigWinPatternCount: 3);

        Assert.Equal(ScoreCategory.SmallWin, resultDefault.Category);
        Assert.Equal(ScoreCategory.SmallWin, resultStacked.Category);
        Assert.Equal(resultDefault.BasePoints, resultStacked.BasePoints);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void AllPatterns_Ordering_Is_Deterministic()
    {
        // Detector contract (Phase H Wave 2): WinDetectionResult.AllPatterns is
        // populated in enum-declaration order: SevenPairs(1), AllPungs(2),
        // FullFlush(3), NineTerminals(4) — Standard(0) is NEVER included (it is
        // the baseline, not a stack contributor). ScoringService's multiplier
        // logic relies on this ordering being stable for deterministic scoring.
        //
        // Two hands: AllPungs+FullFlush (Wan only), and SevenPairs+FullFlush
        // (Wan only). Both should produce AllPatterns in the documented order.
        var allPungsFullFlush = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 4), (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 7), (Suit.Wan, 7), (Suit.Wan, 7),
            (Suit.Wan, 2), (Suit.Wan, 2));
        var sevenPairsFullFlush = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 2), (Suit.Wan, 2),
            (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 7));

        var apff = Detector.Detect(allPungsFullFlush);
        var spff = Detector.Detect(sevenPairsFullFlush);

        Assert.True(apff.IsWin);
        Assert.True(spff.IsWin);

        Assert.Equal(new[] { WinPattern.AllPungs, WinPattern.FullFlush },
            apff.AllPatterns.ToArray());
        Assert.Equal(new[] { WinPattern.SevenPairs, WinPattern.FullFlush },
            spff.AllPatterns.ToArray());

        // Standard must never appear in AllPatterns even when the hand is also a
        // valid standard 4+pair (AllPungs is a structural variant of Standard).
        Assert.DoesNotContain(WinPattern.Standard, apff.AllPatterns);
        Assert.DoesNotContain(WinPattern.Standard, spff.AllPatterns);

        // The list must be in strict enum-declaration order (ascending int value).
        Assert.Equal(apff.AllPatterns.OrderBy(p => (int)p).ToArray(),
            apff.AllPatterns.ToArray());
        Assert.Equal(spff.AllPatterns.OrderBy(p => (int)p).ToArray(),
            spff.AllPatterns.ToArray());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static WinResult BuildWinResult(WinPattern pattern, WinMethod method,
        int winningSeat, int sourceSeat, bool isFullFlush)
    {
        return new WinResult
        {
            WinningSeatIndex = winningSeat,
            Method = method,
            Pattern = pattern,
            WinningTileId = 0,
            SourceSeatIndex = sourceSeat,
            IsFullFlush = isFullFlush
        };
    }

    /// <summary>
    /// Invoke <c>ScoringService.CalculateScore(WinResult, int, bool, int)</c> via
    /// reflection so this test file compiles even before Bishop ships the 4-arg
    /// overload. RED-fails with a descriptive message naming the missing symbol.
    /// </summary>
    private static ScoreResult InvokeCalculateScore(WinResult win, int dealerSeatIndex,
        bool isFullFlush, int bigWinPatternCount)
    {
        var service = new ScoringService();
        var method = typeof(ScoringService).GetMethod(
            nameof(ScoringService.CalculateScore),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(WinResult), typeof(int), typeof(bool), typeof(int) },
            modifiers: null);

        if (method is null)
        {
            throw new InvalidOperationException(
                "ScoringService.CalculateScore(WinResult, int, bool, int) overload not found — " +
                "Bishop owes the Phase H Wave 2 contract (stacking multiplier overload).");
        }

        var result = method.Invoke(service,
            new object[] { win, dealerSeatIndex, isFullFlush, bigWinPatternCount });
        return (ScoreResult)result!;
    }
}
