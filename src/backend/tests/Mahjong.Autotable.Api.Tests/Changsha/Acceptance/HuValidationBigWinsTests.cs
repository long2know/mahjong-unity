using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Big-Win patterns relax the 258 pair rule per Vasquez §1.11 +
/// MahjongPros §"Big Wins" + Reddit §"big hands". Each of Seven Pairs (七对), All Pungs
/// (碰碰胡), and Full Flush (清一色) is a single-tier Big Win in v1 — any pair allowed,
/// scoring uses the BigWin payment table.
/// </summary>
public class HuValidationBigWinsTests
{
    private static readonly ChangshaWinDetector Detector = new();

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(9)]
    public void Hu_SevenPairs_NotSubjectTo258(int pairRankNon258)
    {
        // MahjongPros §"Seven Pairs": any 7 distinct pairs — no 258 requirement.
        // We pick 7 distinct pair ranks; one of them is a non-258 rank to prove the exemption.
        var ranks = new[] { 1, 3, 4, 6, 7, 9, pairRankNon258 }.Distinct().Take(7).ToList();
        // Pad to 7 distinct ranks if a duplicate snuck in.
        var fallback = new[] { 1, 3, 4, 6, 7, 9, 2 };
        for (var i = 0; ranks.Count < 7 && i < fallback.Length; i++)
            if (!ranks.Contains(fallback[i])) ranks.Add(fallback[i]);

        var tiles = new List<(Suit, int)>();
        var suits = new[] { Suit.Wan, Suit.Tong, Suit.Tiao };
        for (var i = 0; i < 7; i++)
        {
            var s = suits[i % 3];
            tiles.Add((s, ranks[i]));
            tiles.Add((s, ranks[i]));
        }

        var hand = HandOf(0, tiles.ToArray());
        var result = Detector.Detect(hand);

        Assert.True(result.IsWin,
            $"Seven Pairs (any-pair) must be a valid Hu — ranks used: [{string.Join(",", ranks)}]");
        Assert.Equal(WinPattern.SevenPairs, result.Pattern);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Hu_AllPungs_NotSubjectTo258()
    {
        // MahjongPros §"All Pungs": 4 pungs/kongs + any pair. Vasquez §1.11 confirms 258 exemption.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 3), (Suit.Tiao, 3));  // non-258 pair (rank 3)

        var result = Detector.Detect(hand);

        Assert.True(result.IsWin);
        Assert.Equal(WinPattern.AllPungs, result.Pattern);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Hu_FullFlush_NotSubjectTo258()
    {
        // MahjongPros §"Full Flush" (清一色): every tile single suit. Pair rank unconstrained.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 4), (Suit.Wan, 4),                          // non-258 pair
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9));

        var result = Detector.Detect(hand);

        Assert.True(result.IsWin);
        Assert.True(result.IsFullFlush);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Hu_FullFlush_OverlapsSevenPairs_PatternStillDetected()
    {
        // Edge case: a single-suit seven-pairs hand is BOTH FullFlush AND SevenPairs. The detector
        // must surface SOME big-win pattern (either is acceptable) and classify as BigWin.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 2), (Suit.Wan, 2),
            (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 6),
            (Suit.Wan, 8), (Suit.Wan, 8),
            (Suit.Wan, 9), (Suit.Wan, 9));

        var result = Detector.Detect(hand);

        Assert.True(result.IsWin);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.True(result.IsSevenPairs || result.IsFullFlush,
            $"Single-suit seven-pairs hand must be classified as a Big Win pattern. " +
            $"Got Pattern={result.Pattern}, IsFullFlush={result.IsFullFlush}, IsSevenPairs={result.IsSevenPairs}.");
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void Hu_NineTerminals_BigWin_V2()
    {
        // Phase H Wave 2 §2.1: Changsha's 108-tile deck contains no honor tiles, so the
        // classical 13-Orphans pattern is impossible. The V2 analog is the "9-Terminals"
        // (九幺) Big Win — 14 tiles all of rank 1 or rank 9, containing all six distinct
        // terminal tiles (1/9 of each suit) at least once. Replaces the
        // Hu_ThirteenOrphans_SpecGap_Skipped placeholder per Ripley's design memo §2.1.
        var nineTerminals = ResolveNineTerminalsEnum();

        // Concrete 14-tile NineTerminals hand:
        //   1万×3, 9万×3, 1筒×3, 9筒×2, 1条×2, 9条×1 = 14 tiles, all rank 1/9, six distinct.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 1), (Suit.Tong, 1), (Suit.Tong, 1),
            (Suit.Tong, 9), (Suit.Tong, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 9));

        var result = Detector.Detect(hand);

        Assert.True(result.IsWin,
            "NineTerminals (Changsha 九幺) Big Win must be a valid Hu — Bishop owes the V2 detection branch.");
        Assert.Equal(ScoreCategory.BigWin, result.Category);

        var allPatterns = ResolveAllPatterns(result);
        var nineTerminalsReported = result.Pattern == nineTerminals
            || (allPatterns is not null && allPatterns.Contains(nineTerminals));
        Assert.True(nineTerminalsReported,
            $"NineTerminals must be reported as the (or one of the) detected pattern(s). " +
            $"Got Pattern={result.Pattern}, AllPatterns=[{(allPatterns is null ? "<missing>" : string.Join(",", allPatterns))}].");
    }

    // ── Phase H Wave 2 — reflective helpers ──────────────────────────────────────

    private static WinPattern ResolveNineTerminalsEnum()
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

    private static IReadOnlyList<WinPattern>? ResolveAllPatterns(WinDetectionResult result)
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
}
