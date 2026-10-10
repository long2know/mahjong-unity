using Mahjong.Autotable.Api.Changsha.Patterns;
using Microsoft.AspNetCore.Mvc;

namespace Mahjong.Autotable.Api.Changsha.Patterns;

/// <summary>
/// Phase J Wave 9 — i18n catalog for win-pattern names.
/// <c>GET /api/i18n/patterns?lang=&lt;tag&gt;</c>. Response shape:
/// <c>{ lang: "en", patterns: { standard: "...", sevenPairs: "...", ... } }</c>.
/// Unknown languages fall back to English (no 404).
/// </summary>
[ApiController]
[Route("api/i18n")]
public sealed class I18nController : ControllerBase
{
    [HttpGet("patterns")]
    public IActionResult Patterns([FromQuery] string? lang)
    {
        var resolved = ResolveLang(lang);
        var catalog = PatternResourceCatalog.ForLanguage(resolved);
        return Ok(new
        {
            lang = resolved,
            patterns = catalog,
            supportedLanguages = PatternResourceCatalog.SupportedLanguages.ToArray(),
        });
    }

    [HttpGet("patterns/{lang}")]
    public IActionResult PatternsByPath(string lang) => Patterns(lang);

    private static string ResolveLang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "en";
        if (PatternResourceCatalog.SupportedLanguages.Contains(lang, StringComparer.OrdinalIgnoreCase))
            return lang;
        if (string.Equals(lang.Split('-')[0], "zh", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans";
        return "en";
    }
}
