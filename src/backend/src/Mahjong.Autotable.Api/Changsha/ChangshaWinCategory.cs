namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Single source of truth for classifying a <see cref="WinResult"/> into a
/// <see cref="ScoreCategory"/> (Small Win vs Big Win) per spec §4.2.2 / §5.1.
///
/// <para>Issue #157: <see cref="ScoringService"/> previously classified from
/// <see cref="WinResult.Pattern"/> alone via a switch whose default arm returned
/// <see cref="ScoreCategory.SmallWin"/>. On a Standard (258-pair) shape the detector
/// sets <see cref="WinResult.Pattern"/> to the lone contextual Big Win it detected
/// (天和 / 地和 / 海底捞月 / 河底捞鱼 / 杠上开花), and 抢杠胡 is carried only on
/// <see cref="WinResult.IsRobbedKong"/> — none of which the switch enumerated, so
/// every contextual Big Win fell through to Small Win and underpaid.</para>
///
/// <para>Centralising the Big-Win pattern set here means classification is defined
/// once and cannot drift between the detector, the scoring service, and future
/// callers.</para>
/// </summary>
public static class ChangshaWinCategory
{
    /// <summary>
    /// Every <see cref="WinPattern"/> that constitutes a Big Win — the four structural
    /// shapes (<see cref="WinPattern.SevenPairs"/>, <see cref="WinPattern.AllPungs"/>,
    /// <see cref="WinPattern.FullFlush"/>, <see cref="WinPattern.NineTerminals"/>) plus
    /// the five contextual §4.2.2 bonuses (<see cref="WinPattern.HeavenlyHand"/>,
    /// <see cref="WinPattern.EarthlyHand"/>, <see cref="WinPattern.LastTileFromWall"/>,
    /// <see cref="WinPattern.LastDiscardCatch"/>, <see cref="WinPattern.KongReplacementWin"/>).
    /// <see cref="WinPattern.Standard"/> is the only Small-Win baseline shape. 抢杠胡
    /// (Robbing the Added Kong) is NOT a <see cref="WinPattern"/> — it lives on
    /// <see cref="WinResult.IsRobbedKong"/> and is handled directly by <see cref="Classify"/>.
    /// </summary>
    private static readonly HashSet<WinPattern> BigWinPatterns =
    [
        WinPattern.SevenPairs,
        WinPattern.AllPungs,
        WinPattern.FullFlush,
        WinPattern.NineTerminals,
        WinPattern.HeavenlyHand,
        WinPattern.EarthlyHand,
        WinPattern.LastTileFromWall,
        WinPattern.LastDiscardCatch,
        WinPattern.KongReplacementWin,
    ];

    /// <summary>Returns true if <paramref name="pattern"/> is a Big Win pattern.</summary>
    public static bool IsBigWinPattern(WinPattern pattern) => BigWinPatterns.Contains(pattern);

    /// <summary>
    /// Classifies <paramref name="win"/> as <see cref="ScoreCategory.BigWin"/> when it
    /// is a 抢杠胡 (<see cref="WinResult.IsRobbedKong"/>) OR its headline
    /// <see cref="WinResult.Pattern"/> is a Big Win OR any pattern in
    /// <see cref="WinResult.AllPatterns"/> is a Big Win; otherwise
    /// <see cref="ScoreCategory.SmallWin"/>. <see cref="WinResult.AllPatterns"/> is
    /// consulted (in addition to the headline pattern) so structural Big Wins survive
    /// even for legacy callers that only populate <see cref="WinResult.Pattern"/>.
    /// </summary>
    public static ScoreCategory Classify(WinResult win)
    {
        if (win.IsRobbedKong)
            return ScoreCategory.BigWin;

        if (IsBigWinPattern(win.Pattern))
            return ScoreCategory.BigWin;

        foreach (var pattern in win.AllPatterns)
        {
            if (IsBigWinPattern(pattern))
                return ScoreCategory.BigWin;
        }

        return ScoreCategory.SmallWin;
    }
}
