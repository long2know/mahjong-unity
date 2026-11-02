using System.Reflection;
using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Changsha.Patterns;

/// <summary>
/// Phase J Wave 9 — server-side i18n catalog for win-pattern names.
/// Mirrors Hicks's frontend resource bundle so a client that doesn't
/// ship its own bundle (e.g. mobile shell, future native viewer) can
/// localise win banners by calling
/// <c>GET /api/i18n/patterns?lang=&lt;tag&gt;</c>.
///
/// <para>Supported languages:
/// <list type="bullet">
///   <item><c>en</c> — English (canonical, always present).</item>
///   <item><c>zh-Hans</c> — Simplified Chinese.</item>
///   <item><c>zh-Hant</c> — Traditional Chinese.</item>
/// </list>
/// Unknown language codes fall back to English (no 404).</para>
/// </summary>
public static class PatternResourceCatalog
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> _catalogs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new Dictionary<string, string>
        {
            ["standard"] = "Standard win",
            ["sevenPairs"] = "Seven Pairs",
            ["allPungs"] = "All Pungs",
            ["fullFlush"] = "Full Flush",
            ["nineTerminals"] = "Nine Terminals",
            ["heavenlyHand"] = "Heavenly Hand",
            ["earthlyHand"] = "Earthly Hand",
            ["lastTileFromWall"] = "Last Tile from the Wall",
            ["lastDiscardCatch"] = "Last Discard Catch",
            ["kongReplacementWin"] = "Win on Kong Replacement",
        },
        ["zh-Hans"] = new Dictionary<string, string>
        {
            ["standard"] = "平和",
            ["sevenPairs"] = "七对",
            ["allPungs"] = "碰碰胡",
            ["fullFlush"] = "清一色",
            ["nineTerminals"] = "九幺",
            ["heavenlyHand"] = "天和",
            ["earthlyHand"] = "地和",
            ["lastTileFromWall"] = "海底捞月",
            ["lastDiscardCatch"] = "河底捞鱼",
            ["kongReplacementWin"] = "杠上开花",
        },
        ["zh-Hant"] = new Dictionary<string, string>
        {
            ["standard"] = "平和",
            ["sevenPairs"] = "七對",
            ["allPungs"] = "碰碰胡",
            ["fullFlush"] = "清一色",
            ["nineTerminals"] = "九么",
            ["heavenlyHand"] = "天和",
            ["earthlyHand"] = "地和",
            ["lastTileFromWall"] = "海底撈月",
            ["lastDiscardCatch"] = "河底撈魚",
            ["kongReplacementWin"] = "槓上開花",
        },
    };

    /// <summary>
    /// Returns the catalog for <paramref name="lang"/>. Falls back to
    /// English when the requested tag isn't supported; never null.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForLanguage(string? lang)
    {
        if (!string.IsNullOrWhiteSpace(lang))
        {
            if (_catalogs.TryGetValue(lang, out var direct)) return direct;
            // Match on primary subtag — e.g. "zh-CN" → "zh-Hans".
            var primary = lang.Split('-')[0];
            if (string.Equals(primary, "zh", StringComparison.OrdinalIgnoreCase))
                return _catalogs["zh-Hans"];
            if (_catalogs.TryGetValue(primary, out var primaryHit)) return primaryHit;
        }
        return _catalogs["en"];
    }

    public static IEnumerable<string> SupportedLanguages => _catalogs.Keys;

    /// <summary>
    /// Resolves a single <see cref="WinPattern"/> to its canonical resource
    /// key, reading the <see cref="PatternResourceAttribute"/> via reflection.
    /// Cached on first hit so the per-win lookup is O(1).
    /// </summary>
    public static string KeyFor(WinPattern pattern)
    {
        return _patternKeyCache.GetOrAdd(pattern, p =>
        {
            var field = typeof(WinPattern).GetField(p.ToString(), BindingFlags.Public | BindingFlags.Static);
            var attr = field?.GetCustomAttribute<PatternResourceAttribute>();
            if (attr is not null) return attr.Key;
            // Fallback: camelCase the enum member name (e.g. SevenPairs → sevenPairs)
            // so the wire surface stays stable even when the [PatternResource]
            // attributes haven't been applied to the enum (e.g. mid-merge where
            // another agent reset ChangshaDomain.cs to baseline).
            var name = p.ToString();
            if (string.IsNullOrEmpty(name)) return name;
            return char.ToLowerInvariant(name[0]) + name[1..];
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<WinPattern, string> _patternKeyCache = new();
}
