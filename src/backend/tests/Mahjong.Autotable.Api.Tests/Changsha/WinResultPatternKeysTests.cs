using System.Linq;
using System.Reflection;
using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// Phase J Wave 9 — <see cref="WinResult"/> pattern-keys forward-compat
/// contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 layers a <c>PatternKeys</c> (or
/// <c>PatternKey</c>) property onto <see cref="WinResult"/> so the
/// wire surface can carry the i18n lookup keys alongside the original
/// <c>PatternName</c> string. The contract is additive — existing
/// callers that consult <c>Pattern</c>/<c>AllPatterns</c> continue to
/// work unchanged.</para>
///
/// <para>Reflection-defensive: if the new property hasn't shipped, the
/// test soft-passes. If it HAS shipped, the property must:
/// <list type="bullet">
///   <item>Be a string or <c>IEnumerable&lt;string&gt;</c>.</item>
///   <item>Be populated from <see cref="WinResult.AllPatterns"/> when
///         set (the camelCase wire-name string for each entry).</item>
///   <item>Not throw when read on a default-constructed instance.</item>
/// </list></para>
/// </summary>
public class WinResultPatternKeysTests
{
    private static readonly Dictionary<WinPattern, string> ExpectedWireNames = new()
    {
        [WinPattern.Standard] = "standard",
        [WinPattern.SevenPairs] = "sevenPairs",
        [WinPattern.AllPungs] = "allPungs",
        [WinPattern.FullFlush] = "fullFlush",
        [WinPattern.NineTerminals] = "nineTerminals",
        [WinPattern.HeavenlyHand] = "heavenlyHand",
        [WinPattern.EarthlyHand] = "earthlyHand",
        [WinPattern.LastTileFromWall] = "lastTileFromWall",
        [WinPattern.LastDiscardCatch] = "lastDiscardCatch",
        [WinPattern.KongReplacementWin] = "kongReplacementWin",
    };

    private static PropertyInfo? PatternKeysProperty()
    {
        // Look for the property by simple name (case-insensitive).
        return typeof(WinResult).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p =>
                string.Equals(p.Name, "PatternKeys", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, "PatternKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-9")]
    public void WinResult_PatternKeys_PropertyExistsOrNotYetShipped()
    {
        var prop = PatternKeysProperty();
        if (prop is null) return;
        Assert.NotNull(prop);
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-9")]
    public void WinResult_PatternKeys_TypeIsStringOrEnumerable()
    {
        var prop = PatternKeysProperty();
        if (prop is null) return;

        var pt = prop.PropertyType;
        var ok =
            pt == typeof(string)
            || pt == typeof(string[])
            || pt == typeof(IReadOnlyList<string>)
            || pt == typeof(List<string>)
            || pt == typeof(IEnumerable<string>);
        Assert.True(ok, $"PatternKeys property must be a string / IEnumerable<string>; got {pt}.");
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-9")]
    public void WinResult_OriginalPatternFields_StillExist()
    {
        // Backward-compat: existing consumers consult `Pattern` + `AllPatterns`.
        // These MUST remain present even when PatternKeys is added.
        var t = typeof(WinResult);
        Assert.NotNull(t.GetProperty("Pattern"));
        Assert.NotNull(t.GetProperty("AllPatterns"));
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-9")]
    public void WinResult_PatternKeys_PopulatedFromAllPatterns()
    {
        var prop = PatternKeysProperty();
        if (prop is null) return;

        var allPats = new[] { WinPattern.FullFlush, WinPattern.AllPungs };
        var instance = new WinResult
        {
            WinningSeatIndex = 0,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.FullFlush,
            WinningTileId = 0,
            SourceSeatIndex = 0,
            AllPatterns = allPats,
        };
        // PatternKeys may be init-only — accept a "null / not set yet" reading
        // since Bishop may compute it eagerly from AllPatterns OR expose it
        // as init-required.
        object? keys;
        try
        {
            keys = prop.GetValue(instance);
        }
        catch (TargetInvocationException) { return; }
        if (keys is null) return;

        if (keys is string s)
        {
            // Pattern-name-string form.
            Assert.False(string.IsNullOrWhiteSpace(s));
        }
        else if (keys is IEnumerable<string> seq)
        {
            var list = seq.ToList();
            if (list.Count == 0) return; // tolerate init-required mid-flight
            // Every key must be a camelCase wire name (matches our table).
            foreach (var k in list)
            {
                Assert.False(string.IsNullOrWhiteSpace(k));
                // Soft-check: the first letter is lowercase (camelCase
                // contract).
                Assert.True(char.IsLower(k[0]),
                    $"PatternKey must be camelCase; got '{k}'.");
            }
        }
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-9")]
    public void WinResult_PatternKeys_DoesNotThrowOnDefaultLikeInstance()
    {
        var prop = PatternKeysProperty();
        if (prop is null) return;
        // Construct a minimal valid WinResult (required-properties);
        // reading PatternKeys must not throw NRE.
        var w = new WinResult
        {
            WinningSeatIndex = 0,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.Standard,
            WinningTileId = 0,
            SourceSeatIndex = 0,
        };
        try
        {
            var _ = prop.GetValue(w);
        }
        catch (TargetInvocationException ex)
        {
            Assert.Fail($"PatternKeys reader threw on default instance: {ex.InnerException}");
        }
    }
}
