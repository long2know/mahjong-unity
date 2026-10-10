using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 9 — Bishop. Legacy livestream URL alias.
///
/// <para>Wave 6 shipped the canonical
/// <c>/api/voice/livestream/{gameId}/...</c> route family. A handful
/// of early integration clients (mobile betas + the spectator demo
/// page) were minted against the older
/// <c>/api/tables/{tableId}/livestream/...</c> shape. Wave 9
/// canonicalises the URL space:</para>
///
/// <list type="bullet">
///   <item>GET / HEAD on a legacy path 301-redirects to the canonical
///         voice route. Body-less requests can safely follow the
///         redirect without losing semantics.</item>
///   <item>POST (and any other body-bearing verb) returns 308
///         (Permanent Redirect) — the method-preserving form — so
///         the start / stop bodies survive the second hop.</item>
///   <item>The response stamps
///         <c>Cache-Control: public, max-age=86400</c> so CDNs cache
///         the redirect for a day and don't hammer the API.</item>
///   <item>Headers
///         <c>Sunset: Wed, 23 May 2027 00:00:00 GMT</c>,
///         <c>Deprecation: true</c>, and
///         <c>Link: &lt;https://...&gt;; rel="sunset"</c> advertise
///         the removal date to operators + integrators (RFC 8594).</item>
/// </list>
///
/// <para>In Wave 9 <c>tableId ≡ gameId</c> (the
/// <c>ChangshaGame.Id</c> Guid is reused as the table identity),
/// so the alias rewrites 1:1 without a lookup. If a later wave
/// splits the two identities the alias controller must grow a
/// lookup or be retired in favour of an explicit migration path.</para>
/// </summary>
[ApiController]
[Route("api/tables/{tableId}/livestream")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class LegacyLivestreamAliasController : ControllerBase
{
    /// <summary>Sunset RFC date — Wed, 23 May 2027 00:00:00 GMT.</summary>
    public const string SunsetDate = "Wed, 23 May 2027 00:00:00 GMT";

    /// <summary>Cache-Control directive applied to every alias hop.</summary>
    public const string CacheControlDirective = "public, max-age=86400";

    /// <summary>Canonical route prefix the alias rewrites onto.</summary>
    public const string CanonicalPrefix = "/api/voice/livestream";

    private const string DeprecationHeader = "Deprecation";
    private const string SunsetHeader = "Sunset";
    private const string LinkHeader = "Link";
    private const string CacheControlHeader = "Cache-Control";

    [HttpGet("{*rest}")]
    [HttpHead("{*rest}")]
    public IActionResult RedirectGet(string tableId, string? rest)
    {
        StampDeprecationHeaders();
        var canonical = BuildCanonicalUrl(tableId, rest);
        return RedirectPermanent(canonical);
    }

    [HttpPost("{*rest}")]
    [HttpPut("{*rest}")]
    [HttpPatch("{*rest}")]
    [HttpDelete("{*rest}")]
    public IActionResult RedirectMutating(string tableId, string? rest)
    {
        StampDeprecationHeaders();
        var canonical = BuildCanonicalUrl(tableId, rest);
        return RedirectPermanentPreserveMethod(canonical);
    }

    private static string BuildCanonicalUrl(string tableId, string? rest)
    {
        var safeTable = Uri.EscapeDataString(tableId ?? string.Empty);
        if (string.IsNullOrWhiteSpace(rest))
        {
            return $"{CanonicalPrefix}/{safeTable}";
        }
        // The catch-all binder hands us the raw remainder without a
        // leading slash; preserve internal slashes (segment paths,
        // sub-resources) as-is.
        return $"{CanonicalPrefix}/{safeTable}/{rest}";
    }

    private void StampDeprecationHeaders()
    {
        var headers = Response.Headers;
        headers[CacheControlHeader] = CacheControlDirective;
        headers[SunsetHeader] = SunsetDate;
        headers[DeprecationHeader] = "true";
        headers[LinkHeader] = $"<{CanonicalPrefix}>; rel=\"sunset\"";
    }
}
