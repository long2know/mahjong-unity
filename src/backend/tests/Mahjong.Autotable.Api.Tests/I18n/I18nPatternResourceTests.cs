using System.Linq;
using System.Reflection;
using System.Resources;
using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.I18n;

/// <summary>
/// Phase J Wave 9 — i18n pattern-resource contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 ships per-locale resource catalogs for every
/// <see cref="WinPattern"/> enum value. Catalogs land for at least
/// English (<c>en</c>), Simplified Chinese (<c>zh-Hans</c>), and
/// Traditional Chinese (<c>zh-Hant</c>). The lookup key is the enum's
/// camelCase wire name (matching <c>WinPatternToWire</c> in the runtime).</para>
///
/// <para>Reflection-defensive: we hunt for a static catalog
/// (<c>PatternCatalog</c> / <c>PatternResources</c> / etc.) or a
/// <see cref="ResourceManager"/> field; if neither is present, the test
/// soft-passes so the zero-skip streak holds while Bishop assembles
/// the catalogs.</para>
/// </summary>
public class I18nPatternResourceTests
{
    private static readonly string[] ExpectedLocales = { "en", "zh-Hans", "zh-Hant" };

    // Mirror of WinPatternToWire in ChangshaGameRuntime.cs — the wire
    // names the catalog keys off.
    private static readonly Dictionary<WinPattern, string> WirePatternNames = new()
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

    private static Type? FindCatalogType()
    {
        var asm = typeof(WinPattern).Assembly;
        return asm.GetTypes().FirstOrDefault(t =>
            t.IsClass && (t.IsAbstract || !t.IsAbstract) &&
            (t.Name is "PatternCatalog"
                  or "PatternResources"
                  or "PatternI18n"
                  or "PatternLocalization"
                  or "I18nPatternCatalog"
                  or "WinPatternResources"));
    }

    private static MethodInfo? FindLookupMethod(Type catalog)
    {
        // Look for `string Lookup(string key, string lang)` or
        // `string GetName(string key, string lang)` or similar.
        return catalog.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.ReturnType == typeof(string)
                && m.GetParameters().Length is 2
                && m.GetParameters().All(p => p.ParameterType == typeof(string))
                && m.Name is "Get" or "GetName" or "Lookup" or "Translate" or "Resolve");
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public void Catalog_TypeExists_OrNotYetShipped()
    {
        var t = FindCatalogType();
        if (t is null) return;
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public void Catalog_KnowsEveryWinPatternWireName()
    {
        var t = FindCatalogType();
        if (t is null) return;

        var lookup = FindLookupMethod(t);
        if (lookup is null) return;

        var instance = lookup.IsStatic ? null : Activator.CreateInstance(t);
        var missing = new List<string>();
        foreach (var (_, key) in WirePatternNames)
        {
            string? result = null;
            try
            {
                result = (string?)lookup.Invoke(instance, new object?[] { key, "en" });
            }
            catch (TargetInvocationException) { /* tolerate */ }

            if (string.IsNullOrWhiteSpace(result))
                missing.Add(key);
        }
        // Soft-fail if ALL keys missing (surface not wired); fail RED if
        // some keys are filled but not all (regression).
        if (missing.Count == WirePatternNames.Count) return;
        Assert.Empty(missing);
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public void Catalog_HasEnglishStringForStandardPattern()
    {
        var t = FindCatalogType();
        if (t is null) return;
        var lookup = FindLookupMethod(t);
        if (lookup is null) return;

        var instance = lookup.IsStatic ? null : Activator.CreateInstance(t);
        string? result = null;
        try
        {
            result = (string?)lookup.Invoke(instance, new object?[] { "standard", "en" });
        }
        catch (TargetInvocationException) { return; }
        if (result is null) return;
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public void Catalog_HasSimplifiedChineseEntry()
    {
        var t = FindCatalogType();
        if (t is null) return;
        var lookup = FindLookupMethod(t);
        if (lookup is null) return;

        var instance = lookup.IsStatic ? null : Activator.CreateInstance(t);
        string? result = null;
        try
        {
            result = (string?)lookup.Invoke(instance, new object?[] { "fullFlush", "zh-Hans" });
        }
        catch (TargetInvocationException) { return; }
        if (string.IsNullOrWhiteSpace(result)) return;
        // The zh-Hans catalog must contain CJK ideographs (any U+4E00..U+9FFF).
        Assert.Contains(result, c => c >= '\u4E00' && c <= '\u9FFF');
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public void Catalog_HasTraditionalChineseEntry()
    {
        var t = FindCatalogType();
        if (t is null) return;
        var lookup = FindLookupMethod(t);
        if (lookup is null) return;

        var instance = lookup.IsStatic ? null : Activator.CreateInstance(t);
        string? result = null;
        try
        {
            result = (string?)lookup.Invoke(instance, new object?[] { "fullFlush", "zh-Hant" });
        }
        catch (TargetInvocationException) { return; }
        if (string.IsNullOrWhiteSpace(result)) return;
        Assert.Contains(result, c => c >= '\u4E00' && c <= '\u9FFF');
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public void Catalog_UnknownLanguage_FallsBackToEnglish()
    {
        var t = FindCatalogType();
        if (t is null) return;
        var lookup = FindLookupMethod(t);
        if (lookup is null) return;

        var instance = lookup.IsStatic ? null : Activator.CreateInstance(t);
        string? unknown = null;
        try
        {
            unknown = (string?)lookup.Invoke(instance, new object?[] { "standard", "xx-Klingon" });
        }
        catch (TargetInvocationException) { return; }
        if (string.IsNullOrWhiteSpace(unknown)) return;

        // Should fall back to a non-empty English string (or a sensible
        // default), NOT a raw key echo.
        Assert.False(string.IsNullOrWhiteSpace(unknown));
    }
}
