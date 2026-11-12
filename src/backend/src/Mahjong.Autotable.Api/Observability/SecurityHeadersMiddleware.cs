using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase J Wave 8 — security headers + CDN cache-policy middleware
/// (Apone, DevOps). One ASP.NET Core middleware that:
///
/// <list type="number">
///   <item>Stamps the OWASP-recommended security headers on every response
///         (<c>X-Frame-Options</c>, <c>X-Content-Type-Options</c>,
///         <c>Referrer-Policy</c>, <c>Content-Security-Policy</c>).</item>
///   <item>Rewrites <c>Cache-Control</c> and <c>Vary</c> on static-asset
///         responses so Cloudflare and downstream CDNs cache hashed bundles
///         immutably while letting <c>index.html</c> revalidate every
///         request. Without this, Vite's hashed bundles get the
///         framework's default <c>no-cache</c>, which kills Cloudflare's
///         hit rate.</item>
/// </list>
///
/// <para>The CSP is tight: <c>script-src 'self' 'wasm-unsafe-eval'</c>.
/// Hicks's Phase-J-Wave-9 audit confirmed the shipped bundle
/// contains zero <c>new Function(...)</c> / <c>eval(...)</c> callsites
/// (Three.js's <c>three.module.js</c> build doesn't need eval — that's
/// only <c>three.webgpu.js</c>, which we don't import). The
/// <c>'wasm-unsafe-eval'</c> token is the CSP-Level-3 permission that
/// allows <c>WebAssembly.compile()</c> only (used by future Draco /
/// KTX decoders); it does NOT re-enable <c>eval()</c> /
/// <c>new Function</c>. <c>'unsafe-inline'</c> is intentionally NOT in
/// the policy — the frontend bundles every <c>&lt;script&gt;</c> tag
/// through Vite so inline scripts are already absent.</para>
///
/// <para><b>Phase J Wave 9 — strict-mode + nonces + report-uri (Apone).</b>
/// The middleware now ships three CSP knobs:
/// <list type="bullet">
///   <item><c>Security:CspStrict</c> (bool, default <c>false</c>) — when
///         true, drops <c>'wasm-unsafe-eval'</c> too, leaving
///         <c>script-src 'self'</c>. The Wave-9 default already
///         removed <c>'unsafe-eval'</c>; flip CspStrict on once we've
///         confirmed no future loader pulls in WebAssembly.</item>
///   <item><c>Security:UseScriptNonces</c> (bool, default <c>false</c>) —
///         when true, the middleware generates a per-request 16-byte
///         base64url nonce, exposes it via <c>HttpContext.Items["csp-nonce"]</c>
///         for any view that injects an inline script, and emits
///         <c>script-src 'self' 'nonce-…'</c>. Most responses won't use
///         the nonce (Vite bundles all scripts), but the hook is in
///         place for future inline-bootstrap injection.</item>
///   <item><c>Security:CspReportOnly</c> (bool, default <c>false</c>) —
///         when true, the policy ships under the
///         <c>Content-Security-Policy-Report-Only</c> header instead of
///         the enforcing <c>Content-Security-Policy</c>. Useful for
///         canary deployments where you want to audit violations
///         before enforcing.</item>
/// </list>
/// In every mode the policy carries
/// <c>report-uri /api/csp-report</c> so browsers POST violation reports to
/// <see cref="CspReportEndpoint"/>; that hooks into a persisted
/// <c>CspViolation</c> table + the structured logging pipeline.</para>
///
/// <para><b>Cache policy.</b> Vite emits filenames of the form
/// <c>name.&lt;hash&gt;.{js,css,wav,png,glb}</c> where <c>&lt;hash&gt;</c>
/// is 8 hex chars (Rollup 4 <c>hashCharacters: 'hex'</c> — see
/// <c>vite.config.ts</c>). These are content-addressed so we serve them
/// with
/// <c>Cache-Control: public, max-age=31536000, immutable</c> — Cloudflare
/// caches indefinitely + skip revalidation. Everything else (HTML,
/// the index.html shell, and any non-hashed asset) is served with
/// <c>Cache-Control: no-cache, must-revalidate</c> so a new deploy
/// becomes immediately visible.</para>
///
/// <para>Configuration shape (<c>appsettings.json</c> § <c>Security</c>):</para>
/// <list type="bullet">
///   <item><b>ContentSecurityPolicy</b> (string, optional) — overrides the
///         default CSP entirely. Useful when a specific deploy needs to
///         allow additional script sources (e.g. an analytics CDN).
///         When set, the <c>CspStrict</c>/<c>UseScriptNonces</c> knobs
///         have no effect — the operator-supplied policy ships verbatim
///         (the middleware still appends a <c>report-uri</c> directive
///         if absent, so reports keep flowing).</item>
///   <item><b>CspStrict</b> (bool, default <c>false</c>) — drops
///         <c>'wasm-unsafe-eval'</c> from the built-in default CSP.</item>
///   <item><b>UseScriptNonces</b> (bool, default <c>false</c>) — adds a
///         per-request <c>'nonce-…'</c> source to <c>script-src</c> and
///         exposes the nonce via <c>HttpContext.Items["csp-nonce"]</c>.</item>
///   <item><b>CspReportOnly</b> (bool, default <c>false</c>) — ships
///         under the <c>Content-Security-Policy-Report-Only</c> header
///         instead of enforcing.</item>
///   <item><b>CspReportUri</b> (string, default <c>/api/csp-report</c>) —
///         endpoint browsers POST violations to. Setting this to the
///         empty string disables the directive entirely.</item>
///   <item><b>EnableHsts</b> (bool, default <c>false</c>) — when true,
///         adds <c>Strict-Transport-Security: max-age=31536000; includeSubDomains</c>.
///         Off by default because HSTS sticks once issued; production
///         deploys should opt in explicitly once HTTPS is confirmed
///         stable on the public origin.</item>
/// </list>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>Configuration key for the CSP override (string).</summary>
    public const string CspConfigKey = "Security:ContentSecurityPolicy";

    /// <summary>Configuration key for the HSTS opt-in (bool).</summary>
    public const string HstsConfigKey = "Security:EnableHsts";

    /// <summary>Phase J Wave 9 — configuration key for strict CSP (bool).
    /// When true, drops <c>'wasm-unsafe-eval'</c> from the default CSP.</summary>
    public const string CspStrictConfigKey = "Security:CspStrict";

    /// <summary>Phase J Wave 9 — configuration key for per-request CSP
    /// nonces (bool). When true, emits a <c>'nonce-…'</c> source on
    /// <c>script-src</c> and exposes the nonce via HttpContext.Items.</summary>
    public const string CspUseNoncesConfigKey = "Security:UseScriptNonces";

    /// <summary>Phase J Wave 10 — configuration key for strict styles
    /// (bool). When true, drops <c>'unsafe-inline'</c> from
    /// <c>style-src</c>. Default OFF for backwards compat — flip on
    /// once Hicks's Wave-10 bundle has eliminated every inline
    /// <c>style="…"</c> attribute and the canary's
    /// <c>/api/csp-report</c> sink shows zero <c>style-src</c>
    /// violations.</summary>
    public const string CspStrictStylesConfigKey = "Security:CspStrictStyles";

    /// <summary>Phase J Wave 9 — configuration key for report-only mode
    /// (bool). When true, ships under <c>Content-Security-Policy-Report-Only</c>.</summary>
    public const string CspReportOnlyConfigKey = "Security:CspReportOnly";

    /// <summary>Phase J Wave 9 — override for the report-uri endpoint
    /// (string). Set to empty string to disable the directive.</summary>
    public const string CspReportUriConfigKey = "Security:CspReportUri";

    /// <summary>HttpContext.Items key under which the per-request CSP
    /// nonce is exposed (when <see cref="CspUseNoncesConfigKey"/> is on).
    /// Views / endpoints that inject an inline script can read it as
    /// <c>HttpContext.Items["csp-nonce"] as string</c>.</summary>
    public const string CspNonceItemKey = "csp-nonce";

    /// <summary>Default CSP report endpoint. Routed by
    /// <see cref="CspReportEndpoint.Path"/>.</summary>
    public const string DefaultCspReportUri = "/api/csp-report";

    /// <summary>
    /// Phase J Wave 9 — production CSP. Drops <c>'unsafe-eval'</c>
    /// (the Wave-8 permission for Three.js's runtime shader compiler)
    /// after Hicks's Wave-9 audit confirmed the shipped bundle contains
    /// zero <c>new Function(...)</c> / <c>eval(...)</c> callsites.
    /// <c>'wasm-unsafe-eval'</c> remains so any future Three.js loader
    /// that compiles a WebAssembly draco / ktx decoder keeps working;
    /// per CSP Level 3 this allows <c>WebAssembly.compile()</c> only
    /// and does NOT re-open <c>eval()</c>. Vasquez's
    /// <c>CspHeaderTests.DefaultCspConstant_Wave9_HasNoUnsafeEval</c>
    /// uses <c>'wasm-unsafe-eval'</c> as the canonical landed signal.
    /// A <c>report-uri</c> directive is appended at runtime.
    ///
    /// Phase J Wave 10 — <c>style-src</c> retains <c>'unsafe-inline'</c>
    /// in this default by design.  Apone's opt-in knob
    /// (<see cref="CspStrictStylesConfigKey"/>, default OFF) drops it
    /// at runtime via <see cref="DropStyleUnsafeInline(string)"/>.
    /// Hicks's Wave-10 frontend pass migrated every HTML
    /// <c>style="..."</c> attribute to a CSS class
    /// (<c>src/frontend/autotable-src/src/style.css</c>) so the knob is
    /// now safe to flip in production without bricking the bundle.
    /// The contract that pins the default is
    /// <c>CspStyleSrcNoUnsafeInlineTests.DefaultCspConstant_StylesSection_KeepsUnsafeInlineUntilOptIn</c>.
    ///
    /// Phase L WP-G (#121) — <c>connect-src</c> / <c>media-src</c> widened
    /// for the two optional features the shipped bundle carries so they do
    /// not trip CSP when exercised:
    /// <list type="bullet">
    ///   <item><b>Sentry</b> (<c>src/frontend/autotable-src/src/sentry.ts</c>,
    ///         DSN-gated — no network I/O when no DSN is configured) POSTs
    ///         events to its ingest host, so <c>connect-src</c> allows
    ///         <c>https://*.sentry.io</c> (the wildcard covers
    ///         <c>o###.ingest[.us].sentry.io</c>).</item>
    ///   <item><b>Spectator HLS</b>
    ///         (<c>src/frontend/autotable-src/src/spectator-livestream.ts</c>,
    ///         user-gesture-gated) plays a CloudFront-fronted audio stream
    ///         via HLS.js MSE (segment <c>fetch</c> → <c>connect-src</c>;
    ///         MediaSource hand-off → <c>media-src blob:</c>) or native
    ///         Safari playback (<c>media-src https://*.cloudfront.net</c>).
    ///         The HLS.js polyfill itself is vendored + same-origin, so
    ///         <c>script-src</c> stays <c>'self'</c>.</item>
    /// </list>
    /// These are narrow host allowlists, not a relaxation of the
    /// script/eval surface. <c>docs/frontend-csp-requirements.md</c> is
    /// reconciled to this constant (middleware is the CSP source of truth).
    /// </summary>
    public const string DefaultCsp =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' blob: https://*.cloudfront.net; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss: blob: https://*.sentry.io https://*.cloudfront.net; " +
        "worker-src 'self' blob:; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    /// <summary>
    /// Phase J Wave 9 — ultra-strict CSP that drops even
    /// <c>'wasm-unsafe-eval'</c>. Selected when <c>Security:CspStrict</c>
    /// is true. Otherwise identical to <see cref="DefaultCsp"/> (including
    /// the Phase L WP-G Sentry/HLS <c>connect-src</c> / <c>media-src</c>
    /// host allowlists — those gate optional features, not the eval
    /// surface the strict knob targets).
    /// </summary>
    public const string StrictCsp =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' blob: https://*.cloudfront.net; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss: blob: https://*.sentry.io https://*.cloudfront.net; " +
        "worker-src 'self' blob:; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    /// <summary>
    /// Suffixes that Vite attaches a content hash to. These get the
    /// immutable Cache-Control treatment. Kept as a static array so the
    /// hot path skips a regex compile. Hash detection is delegated to
    /// <see cref="HasContentHash"/>.
    /// </summary>
    private static readonly string[] HashableSuffixes =
        [".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".woff", ".woff2", ".ttf", ".glb", ".gltf", ".wav", ".mp4", ".m4a", ".ogg", ".mp3"];

    private readonly RequestDelegate _next;
    private readonly string _cspTemplate;
    private readonly bool _hstsEnabled;
    private readonly bool _cspStrict;
    private readonly bool _cspStrictStyles;
    private readonly bool _useNonces;
    private readonly bool _reportOnly;
    private readonly string? _reportUri;
    private readonly bool _cspOverrideSupplied;

    public SecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;

        _cspStrict = configuration.GetValue<bool?>(CspStrictConfigKey) ?? false;
        _cspStrictStyles = configuration.GetValue<bool?>(CspStrictStylesConfigKey) ?? false;
        _useNonces = configuration.GetValue<bool?>(CspUseNoncesConfigKey) ?? false;
        _reportOnly = configuration.GetValue<bool?>(CspReportOnlyConfigKey) ?? false;
        _hstsEnabled = configuration.GetValue<bool?>(HstsConfigKey) ?? false;

        var rawReportUri = configuration.GetValue<string?>(CspReportUriConfigKey);
        _reportUri = rawReportUri is null ? DefaultCspReportUri
            : string.IsNullOrWhiteSpace(rawReportUri) ? null
            : rawReportUri;

        var overrideCsp = configuration.GetValue<string?>(CspConfigKey);
        if (!string.IsNullOrWhiteSpace(overrideCsp))
        {
            _cspTemplate = overrideCsp;
            _cspOverrideSupplied = true;
        }
        else
        {
            var baseCsp = _cspStrict ? StrictCsp : DefaultCsp;
            _cspTemplate = _cspStrictStyles ? DropStyleUnsafeInline(baseCsp) : baseCsp;
            _cspOverrideSupplied = false;
        }
    }

    /// <summary>The CSP header name we ship under. <c>Content-Security-Policy</c>
    /// for enforcing mode, <c>-Report-Only</c> for canary mode.</summary>
    private string CspHeaderName => _reportOnly
        ? "Content-Security-Policy-Report-Only"
        : "Content-Security-Policy";

    public async Task InvokeAsync(HttpContext context)
    {
        // Phase J Wave 9 — generate the nonce up front (cheap; 16 bytes)
        // so it's available to downstream code via HttpContext.Items
        // BEFORE the response is generated. The nonce is only attached
        // to the header inside ApplyHeaders, but views need to read it
        // earlier to embed it in `<script nonce="...">` tags.
        string? nonce = null;
        if (_useNonces)
        {
            nonce = GenerateNonce();
            context.Items[CspNonceItemKey] = nonce;
        }

        // OnStarting fires just before the response body is written so
        // header mutations (Cache-Control, Vary) by downstream middleware
        // (UseStaticFiles in particular) have already settled. Closure
        // captures `this` so the CSP / HSTS settings are reachable
        // without spinning up DI inside the callback.
        var self = this;
        context.Response.OnStarting(() =>
        {
            ApplyHeaders(context, self, nonce);
            return Task.CompletedTask;
        });
        await _next(context);
    }

    private static void ApplyHeaders(HttpContext ctx, SecurityHeadersMiddleware instance, string? nonce)
    {
        var headers = ctx.Response.Headers;

        // 1. Security headers — applied on every response, regardless of
        //    Content-Type. Cheap idempotent assignments (header dictionary
        //    is a multi-value set; we use indexer-replace to avoid
        //    duplicates if a downstream component already set the key).
        if (!headers.ContainsKey("X-Frame-Options"))
        {
            headers["X-Frame-Options"] = "DENY";
        }
        if (!headers.ContainsKey("X-Content-Type-Options"))
        {
            headers["X-Content-Type-Options"] = "nosniff";
        }
        if (!headers.ContainsKey("Referrer-Policy"))
        {
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        }

        // Build the final CSP string. When a per-request nonce is in play
        // and the operator hasn't supplied a hard override, splice the
        // nonce into the script-src directive so any future inline
        // script (carrying matching `nonce="<value>"`) is permitted.
        var headerName = instance.CspHeaderName;
        if (!headers.ContainsKey(headerName) && !headers.ContainsKey("Content-Security-Policy"))
        {
            var csp = instance._cspTemplate;
            if (!instance._cspOverrideSupplied && nonce is not null)
            {
                csp = InjectNonceIntoScriptSrc(csp, nonce);
            }
            if (!string.IsNullOrEmpty(instance._reportUri) && !csp.Contains("report-uri", StringComparison.OrdinalIgnoreCase))
            {
                // Append (single source of truth — directives are
                // semicolon-separated; trailing ';' is tolerated by every UA).
                csp = csp.TrimEnd(';', ' ') + "; report-uri " + instance._reportUri;
            }
            headers[headerName] = csp;
        }
        if (instance._hstsEnabled && !headers.ContainsKey("Strict-Transport-Security"))
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        // 2. Cache-Control policy for static assets. Only rewrite when the
        //    request was clearly for a file (path has a known extension)
        //    and the framework hasn't already set an explicit Cache-Control.
        var path = ctx.Request.Path.Value;
        if (!string.IsNullOrEmpty(path))
        {
            var lower = path.ToLowerInvariant();
            var isIndexHtml = lower.EndsWith(".html", StringComparison.Ordinal)
                || lower == "/" || lower.EndsWith("/", StringComparison.Ordinal);

            if (isIndexHtml)
            {
                headers["Cache-Control"] = "no-cache, must-revalidate";
                AppendVary(headers, "Accept-Encoding");
            }
            else if (HasHashableExtension(lower))
            {
                if (HasContentHash(lower))
                {
                    headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
                else
                {
                    // Non-hashed static asset (e.g. /img/icon-32.png).
                    // Cache for an hour at the edge but allow revalidation.
                    if (!headers.ContainsKey("Cache-Control"))
                    {
                        headers["Cache-Control"] = "public, max-age=3600, must-revalidate";
                    }
                }
                AppendVary(headers, "Accept-Encoding");
            }
        }
    }

    /// <summary>
    /// Phase J Wave 10 — strips <c>'unsafe-inline'</c> from the
    /// <c>style-src</c> directive of a CSP string while leaving every
    /// other directive (including <c>script-src</c> nonces /
    /// <c>'wasm-unsafe-eval'</c>) untouched. Internal-public for unit
    /// testing. If the policy has no <c>style-src</c> directive, returns
    /// the input unchanged (default-src fallback applies).
    /// </summary>
    internal static string DropStyleUnsafeInline(string csp)
    {
        var parts = csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("style-src", StringComparison.OrdinalIgnoreCase))
            {
                // Remove "'unsafe-inline'" tokens (whitespace-separated).
                var tokens = parts[i].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var kept = new List<string>(tokens.Length);
                foreach (var t in tokens)
                {
                    if (string.Equals(t, "'unsafe-inline'", StringComparison.OrdinalIgnoreCase)) continue;
                    kept.Add(t);
                }
                parts[i] = string.Join(' ', kept);
                changed = true;
            }
        }
        return changed ? string.Join("; ", parts) : csp;
    }

    /// <summary>
    /// Inserts <c>'nonce-…'</c> into the <c>script-src</c> directive of
    /// a CSP string. Preserves the existing source list and other
    /// directives. Internal-public for unit testing.
    /// </summary>
    internal static string InjectNonceIntoScriptSrc(string csp, string nonce)
    {
        // Walk directives ('; ' separated). Replace script-src; if absent,
        // we leave the policy alone — a hand-rolled policy with no script-src
        // already falls back to default-src.
        var parts = csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("script-src", StringComparison.OrdinalIgnoreCase))
            {
                if (parts[i].Contains("'nonce-", StringComparison.OrdinalIgnoreCase))
                {
                    // Already nonce-bearing — replace the first nonce token.
                    parts[i] = System.Text.RegularExpressions.Regex.Replace(
                        parts[i],
                        @"'nonce-[A-Za-z0-9_\-]+'",
                        $"'nonce-{nonce}'");
                }
                else
                {
                    parts[i] = parts[i] + " 'nonce-" + nonce + "'";
                }
                return string.Join("; ", parts);
            }
        }
        return csp;
    }

    /// <summary>Generates a base64url-encoded 16-byte CSP nonce. Public
    /// for unit testing — cryptographically random.</summary>
    public static string GenerateNonce()
    {
        Span<byte> buf = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        // base64url — strip padding, replace + / per RFC 4648 §5.
        var b64 = Convert.ToBase64String(buf);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void AppendVary(IHeaderDictionary headers, string value)
    {
        if (!headers.TryGetValue("Vary", out var existing) || StringValues.IsNullOrEmpty(existing))
        {
            headers["Vary"] = value;
            return;
        }
        // Avoid duplicating the token.
        foreach (var token in existing.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, value, StringComparison.OrdinalIgnoreCase)) return;
        }
        headers["Vary"] = existing + ", " + value;
    }

    private static bool HasHashableExtension(string path)
    {
        foreach (var suffix in HashableSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Detect Vite's content-hash filename convention: <c>name.&lt;hash&gt;.ext</c>
    /// where <c>&lt;hash&gt;</c> is 8 hex characters. Examples that match:
    /// <c>autotable.9519e86d.js</c>, <c>autotable-src.6633d8fb.css</c>,
    /// <c>tiles.df85b4c4.png</c>. Examples that don't match (and so get
    /// the short-lived <c>max-age=3600</c> policy): <c>index.html</c>,
    /// <c>img/icon-32.auto.png</c> (the <c>.auto</c> token is 4 chars, not 8).
    /// Public for unit testing — pure function, safe to expose.
    /// </summary>
    public static bool HasContentHash(string path)
    {
        var slash = path.LastIndexOf('/');
        var fileName = slash >= 0 ? path[(slash + 1)..] : path;
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot <= 0) return false;
        var stem = fileName[..lastDot];
        var prevDot = stem.LastIndexOf('.');
        if (prevDot <= 0) return false;
        var hashCandidate = stem[(prevDot + 1)..];
        if (hashCandidate.Length != 8) return false;
        foreach (var ch in hashCandidate)
        {
            if (!IsHex(ch)) return false;
        }
        return true;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
