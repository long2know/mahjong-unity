namespace Mahjong.Autotable.Api.Changsha.Patterns;

/// <summary>
/// Phase J Wave 3 — canonical display ordering for <see cref="WinPattern"/> values.
///
/// <para>The runtime fills <see cref="WinResult.AllPatterns"/> in enum-declaration
/// order (driven by the order of <see cref="ChangshaWinDetector"/>'s structural →
/// contextual flag checks), which is convenient for the detector but does not
/// match the order players expect to see in the result-modal chip strip. The
/// expected display order is Big Wins first (天和 / 地和 / 海底 / 河底 / 杠上 / 抢杠 /
/// 九莲 / 九幺), then bonus structural patterns (碰碰胡 / 门前清 / 七对子 / 自摸 /
/// 独张), then the remaining baseline patterns in alphabetical order.</para>
///
/// <para><b>Why a static table and not <c>[Display(Order=N)]</c> on the enum.</b>
/// The enum lives next to the detector and is read by hot paths that don't care
/// about display order; an attribute would require reflection at every render and
/// would couple the detector's source file to a presentation concern. A dedicated
/// metadata class keeps the responsibility lifted out of the domain core.</para>
///
/// <para><b>Wire surface for Hicks.</b> The map is exposed via
/// <c>GET /api/changsha/pattern-ordering</c> (Minimal API in <c>Program.cs</c>),
/// which returns a JSON object keyed by camelCased pattern wire names (the same
/// strings the SignalR <c>winResult.allPatterns</c> array uses), mapped to their
/// canonical integer order. Lower = display first. The frontend sorts
/// <c>allPatterns</c> by this map before rendering.</para>
/// </summary>
public static class ChangshaPatternOrdering
{
    /// <summary>
    /// Canonical display order per pattern. Lower values render first.
    /// Source ordering (per Stephen's Phase J Wave 3 brief — patterns not present
    /// in the current <see cref="WinPattern"/> enum are reserved for future waves
    /// but their slot is preserved in the integer scale so insertions don't shift
    /// existing positions):
    /// <list type="number">
    ///   <item>HeavenlyHand (天和)</item>
    ///   <item>EarthlyHand (地和)</item>
    ///   <item>LastTileFromWall — 海底捞月 ("LastTileDraw")</item>
    ///   <item>LastDiscardCatch — 河底捞鱼 ("LastTileDiscard")</item>
    ///   <item>KongReplacementWin (杠上开花)</item>
    ///   <item>RobbedKong (抢杠胡) — not a <see cref="WinPattern"/>, lives on
    ///         <see cref="WinResult.IsRobbedKong"/>; slot reserved.</item>
    ///   <item>NineGates (九莲宝灯) — not yet implemented; slot reserved.</item>
    ///   <item>NineTerminals (九幺 / 九门十三幺)</item>
    ///   <item>AllPungs (碰碰胡)</item>
    ///   <item>AllConcealed (门前清) — not yet implemented; slot reserved.</item>
    ///   <item>SevenPairs (七对子)</item>
    ///   <item>SelfDraw (自摸) — not a <see cref="WinPattern"/>, lives on
    ///         <see cref="WinResult.IsSelfDraw"/>; slot reserved.</item>
    ///   <item>SingleWait (独张) — not yet implemented; slot reserved.</item>
    /// </list>
    /// Patterns not listed above fall to the alphabetical tail
    /// (<see cref="AlphabeticalFallbackOrder"/>): FullFlush (100), Standard (101).
    /// </summary>
    public static readonly IReadOnlyDictionary<WinPattern, int> Order =
        new Dictionary<WinPattern, int>
        {
            [WinPattern.HeavenlyHand] = 1,
            [WinPattern.EarthlyHand] = 2,
            [WinPattern.LastTileFromWall] = 3,
            [WinPattern.LastDiscardCatch] = 4,
            [WinPattern.KongReplacementWin] = 5,
            // slot 6 reserved for RobbedKong (lives on WinResult.IsRobbedKong)
            // slot 7 reserved for NineGates (future)
            [WinPattern.NineTerminals] = 8,
            [WinPattern.AllPungs] = 9,
            // slot 10 reserved for AllConcealed (future)
            [WinPattern.SevenPairs] = 11,
            // slot 12 reserved for SelfDraw (lives on WinResult.IsSelfDraw)
            // slot 13 reserved for SingleWait (future)
            // alphabetical fallback follows:
            [WinPattern.FullFlush] = 100,
            [WinPattern.Standard] = 101
        };

    /// <summary>
    /// Sentinel order returned by <see cref="GetOrder"/> for patterns not present
    /// in the canonical table. Keeps unknown patterns sorted to the tail without
    /// throwing — defensive against future <see cref="WinPattern"/> additions
    /// that ship before the ordering table is updated.
    /// </summary>
    public const int AlphabeticalFallbackOrder = 999;

    /// <summary>
    /// Returns the canonical display order for <paramref name="pattern"/>. Lower
    /// values render first. Unknown patterns return <see cref="AlphabeticalFallbackOrder"/>.
    /// </summary>
    public static int GetOrder(WinPattern pattern) =>
        Order.TryGetValue(pattern, out var rank) ? rank : AlphabeticalFallbackOrder;

    /// <summary>
    /// Returns a copy of <paramref name="patterns"/> sorted by canonical display
    /// order (stable for ties). Convenience helper for backend callers that want
    /// to mirror the frontend rendering order locally (e.g., the move-log
    /// summary or replay export).
    /// </summary>
    public static IReadOnlyList<WinPattern> Sort(IEnumerable<WinPattern> patterns)
        => patterns.OrderBy(GetOrder).ToList();
}
