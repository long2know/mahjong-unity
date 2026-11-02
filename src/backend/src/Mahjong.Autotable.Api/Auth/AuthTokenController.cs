using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 4 — Bishop. Machine-to-machine JWT mint + validation
/// endpoints layered on top of <see cref="JwtIssuingService"/> +
/// <see cref="JwtValidationService"/>.
///
/// <list type="bullet">
///   <item><c>POST /api/auth/token</c> — body
///         <c>{ subject, claims? }</c>. Requires an admin session
///         (resolved via <see cref="AuthCookieService"/>, role must
///         be <c>admin</c>). Returns the pinned
///         <see cref="AuthTokenResponse"/> envelope on success. 401
///         when no session, 403 when not admin, 400 when subject is
///         empty.</item>
///   <item><c>POST /api/auth/validate</c> — body <c>{ token }</c>.
///         Unauthenticated; rate-limited per-IP at 100/min via the
///         <see cref="RateLimitingExtensions.AuthValidatePolicy"/>
///         token-bucket. Returns
///         <c>{ valid, subject?, claims?, kid?, error? }</c>.</item>
///   <item><c>GET /api/auth/.well-known/jwks.json</c> — Phase K
///         Wave 5. HS256 signing leaves nothing publishable (the
///         shared secret would compromise the entire surface), so
///         the endpoint returns 404 with
///         <c>Cache-Control: no-store</c> + a structured envelope
///         that explains the rationale. The route MUST exist so
///         downstream caches don't pin a positive 404 ahead of the
///         Phase L RS256 flip — operators can validate the negative
///         shape today and the surface will swap to a real
///         <c>{ keys: [...] }</c> document without a URL change.</item>
/// </list>
///
/// <para>The endpoints are deliberately split into a separate
/// controller from <see cref="AuthController"/> so the cookie-based
/// flows stay isolated from the machine-to-machine surface — the two
/// audiences have different rate-limit profiles and different audit
/// requirements.</para>
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthTokenController : ControllerBase
{
    private readonly AuthCookieService _cookies;
    private readonly JwtIssuingService _issuer;
    private readonly JwtValidationService _validator;
    private readonly AuthOptions _authOptions;
    private readonly IOAuthIntrospectRateLimiter? _introspectLimiter;

    public AuthTokenController(
        AuthCookieService cookies,
        JwtIssuingService issuer,
        JwtValidationService validator,
        AuthOptions authOptions,
        IOAuthIntrospectRateLimiter? introspectLimiter = null)
    {
        _cookies = cookies;
        _issuer = issuer;
        _validator = validator;
        _authOptions = authOptions ?? throw new ArgumentNullException(nameof(authOptions));
        _introspectLimiter = introspectLimiter;
    }

    [HttpPost("token")]
    [EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> Issue([FromBody] IssueBody? body, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
            return Unauthorized(new { error = "Authentication required to mint tokens." });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required to mint tokens." });
        if (body is null || string.IsNullOrWhiteSpace(body.Subject))
            return BadRequest(new { error = "subject is required." });

        IReadOnlyDictionary<string, object?>? claims = body.Claims is { Count: > 0 }
            ? body.Claims!
            : null;
        var result = await _issuer.IssueAsync(body.Subject.Trim(), claims, ct: ct);

        // Phase K Wave 5 — Bishop. Surface the pinned AuthTokenResponse
        // envelope (tokenType + expiresInSeconds) on top of the Wave-4
        // { token, expiresAtUtc, kid } triple. Clamp the relative TTL
        // at zero so a token minted right at the expiry boundary never
        // returns a negative integer (some SDK schedulers treat that
        // as "immediate retry forever").
        var expiresInSeconds = (int)Math.Max(
            0,
            Math.Round((result.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds));
        return Ok(new AuthTokenResponse(
            Token: result.Token,
            ExpiresAtUtc: result.ExpiresAtUtc,
            Kid: result.Kid,
            TokenType: AuthTokenResponse.BearerTokenType,
            ExpiresInSeconds: expiresInSeconds));
    }

    [HttpPost("validate")]
    [EnableRateLimiting(RateLimitingExtensions.AuthValidatePolicy)]
    public IActionResult Validate([FromBody] ValidateBody? body)
    {
        if (body is null || string.IsNullOrEmpty(body.Token))
            return Ok(new { valid = false, error = JwtValidationService.ErrorMalformed });
        var result = _validator.Validate(body.Token);
        if (!result.Ok)
            return Ok(new { valid = false, error = result.Error });
        return Ok(new
        {
            valid = true,
            subject = result.Subject,
            claims = result.Claims,
            kid = result.Kid,
        });
    }

    /// <summary>
    /// Phase K Wave 5 — Bishop. RFC 7517 JWKS document endpoint. The
    /// Wave-5 issuance pipeline is HMAC-only (HS256), so a JWKS
    /// document is intentionally NOT published — exposing the HMAC
    /// secret in <c>k</c> form would defeat the entire purpose of
    /// the signing key. The endpoint nonetheless exists so caches
    /// (browser, CDN, sidecar) pin the negative 404 with
    /// <c>Cache-Control: no-store</c>, preventing them from blocking
    /// the Phase L RS256 flip when this route will start returning a
    /// real <c>{ keys: [...] }</c> array under the same URL.
    ///
    /// <para>Phase K Wave 6 — Bishop. The endpoint now branches on
    /// <see cref="JwtSigningKeyProvider.Algorithm"/>:</para>
    /// <list type="bullet">
    ///   <item><b>RS256:</b> publishes the full RFC 7517 JWKS document
    ///         carrying every loaded public key (modulus + exponent),
    ///         with <c>Cache-Control: public, max-age=3600</c> so
    ///         downstream verifiers can briefly cache the resolved
    ///         keys without missing a rotation window.</item>
    ///   <item><b>HS256:</b> retains the negative 404 — the body now
    ///         carries <c>{"reason":"jwt-algorithm-is-hs256","migrate-to":"RS256"}</c>
    ///         so the migration target is wire-discoverable. The cache
    ///         header is pinned to <c>no-store</c> so a misconfigured
    ///         CDN cannot 30-day-cache the 404 envelope and block the
    ///         Phase L RS256 flip (the Wave-5 contract — see
    ///         tests/e2e/jwks-endpoint-shape.spec.ts).</item>
    /// </list>
    /// </summary>
    [HttpGet(".well-known/jwks.json")]
    public IActionResult Jwks(
        [FromServices] JwtSigningKeyProvider keys,
        [FromServices] JwksCacheService cache)
    {
        if (string.Equals(keys.Algorithm, "RS256", StringComparison.Ordinal)
            && keys.AllRsaKeys.Count > 0)
        {
            // Phase K Wave 8 — Bishop. Cached marshalling. The W7
            // endpoint deserialised RSA keys + serialised the JSON
            // envelope on every request — a heavy CPU path under
            // load. W8 caches the pre-serialised body + strong ETag
            // for 60s; rotations invalidate via the kid-fingerprint.
            var payload = cache.Resolve(keys);
            Response.Headers.CacheControl = "public, max-age=3600";
            Response.Headers.ETag = payload.ETag;

            // RFC 7232 If-None-Match conditional. When the inbound
            // header matches the cached ETag we return 304 with no
            // body so the federated verifier saves the bytes.
            if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
            {
                foreach (var candidate in ifNoneMatch)
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    if (string.Equals(candidate, payload.ETag, StringComparison.Ordinal)
                        || string.Equals(candidate, "*", StringComparison.Ordinal))
                    {
                        return StatusCode(StatusCodes.Status304NotModified);
                    }
                }
            }

            return Content(payload.Body, "application/json");
        }

        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status404NotFound, new
        {
            error = "JWKS document is not published for HMAC-signed tokens.",
            algorithm = "HS256",
            note = "Phase L will flip this surface to an RS256 JWKS array; the URL is reserved.",
            reason = "jwt-algorithm-is-hs256",
            migrateTo = "RS256",
            // Wave 6 — wire-name with hyphen for clients that consume the
            // raw JSON property key directly (matches the OAuth/OIDC
            // convention for migration-hint envelopes).
            migrate_to = "RS256",
        });
    }

    /// <summary>
    /// Phase K Wave 6 — Bishop. Companion OIDC discovery document for
    /// the JWKS surface. Published only when
    /// <see cref="JwtSigningKeyProvider.Algorithm"/> is RS256 (when
    /// HS256, returns 404 with
    /// <c>{"reason":"oidc-discovery-disabled"}</c> + the same brief
    /// cacheable negative as the JWKS endpoint). Carries the minimum
    /// fields a downstream verifier needs to bootstrap against the
    /// Mahjong-Autotable auth surface — issuer, jwks_uri,
    /// token_endpoint, and the supported grant types.
    ///
    /// <para>Phase K Wave 7 — Bishop. Hard contract: when RS256 is
    /// active AND <c>Auth:Issuer</c> is configured, the 200 envelope
    /// MUST carry the populated <c>issuer</c>, <c>jwks_uri</c>,
    /// <c>token_endpoint</c>, and <c>grant_types_supported</c> fields
    /// — the discovery document is now a load-bearing surface for
    /// federated verifiers. With an unset issuer the endpoint falls
    /// back to the request's scheme+host (Wave-6 soft behaviour) so
    /// dev / test hosts resolve without explicit operator config.</para>
    /// </summary>
    [HttpGet(".well-known/openid-configuration")]
    public IActionResult OpenIdConfiguration([FromServices] JwtSigningKeyProvider keys)
    {
        if (!string.Equals(keys.Algorithm, "RS256", StringComparison.Ordinal))
        {
            Response.Headers.CacheControl = "public, max-age=60";
            return StatusCode(StatusCodes.Status404NotFound, new
            {
                reason = "oidc-discovery-disabled",
                algorithm = keys.Algorithm,
                note = "OIDC discovery activates with the RS256 flip; the URL is reserved.",
            });
        }

        var origin = $"{Request.Scheme}://{Request.Host}";
        var issuer = string.IsNullOrEmpty(keys.ConfiguredIssuer) ? origin : keys.ConfiguredIssuer;
        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(new
        {
            issuer,
            jwks_uri = $"{origin}/api/auth/.well-known/jwks.json",
            token_endpoint = $"{origin}/api/auth/token",
            introspection_endpoint = $"{origin}/api/auth/introspect",
            introspection_endpoint_auth_methods_supported = new[] { "client_secret_basic" },
            grant_types_supported = new[] { "password", "authorization_code" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            response_types_supported = new[] { "token" },
            subject_types_supported = new[] { "public" },
        });
    }

    /// <summary>Body for <c>POST /api/auth/token</c>.</summary>
    public sealed class IssueBody
    {
        public string? Subject { get; set; }
        public Dictionary<string, object?>? Claims { get; set; }
    }

    /// <summary>Body for <c>POST /api/auth/validate</c>.</summary>
    public sealed class ValidateBody
    {
        public string? Token { get; set; }
    }

    /// <summary>
    /// Phase K Wave 11 — Bishop. RFC 7662 token-introspection
    /// endpoint. Designed for programmatic verifiers (Janus
    /// health-probe, bot frameworks, etc.) that need to confirm
    /// a token's <c>active</c> status without re-implementing the
    /// JWT validation flow.
    ///
    /// <list type="bullet">
    ///   <item>Body: <c>application/x-www-form-urlencoded</c> with
    ///         a <c>token</c> field. Per RFC 7662 §2.1 a missing
    ///         token returns 400.</item>
    ///   <item>Auth: HTTP Basic with a client allowlisted in
    ///         <see cref="AuthOptions.IntrospectionClients"/>.
    ///         Missing/invalid credentials return 401.</item>
    ///   <item>Response: JSON envelope with <c>active</c>,
    ///         <c>scope</c>, <c>client_id</c>, <c>username</c>,
    ///         <c>exp</c>, <c>iat</c>, <c>sub</c>. When the token
    ///         is invalid (malformed, bad signature, expired,
    ///         unsupported alg), <c>active</c> is <c>false</c>
    ///         and the optional fields are omitted per RFC
    ///         §2.2.</item>
    /// </list>
    /// </summary>
    [HttpPost("introspect")]
    [EnableRateLimiting(RateLimitingExtensions.AuthValidatePolicy)]
    [Consumes("application/x-www-form-urlencoded")]
    public IActionResult Introspect([FromForm] IntrospectionForm? form)
    {
        // RFC 7662 §2.3: WWW-Authenticate: Basic on the 401 path.
        var client = AuthenticateBasicClient(HttpContext, _authOptions);
        if (client is null)
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"introspect\"";
            return Unauthorized(new { error = "invalid_client" });
        }

        // Phase K Wave 12 — Bishop. Per-client_id sliding-window
        // rate limit. The W11 surface relied only on the
        // global AuthValidatePolicy bucket; W12 adds a
        // client-scoped cap so one chatty verifier can't
        // exhaust the global budget for everyone else.
        if (_introspectLimiter is not null)
        {
            var decision = _introspectLimiter.TryAcquire(client.ClientId, DateTimeOffset.UtcNow);
            // Standard RFC 6585 headers — `X-RateLimit-*`
            // surfaces the live budget; `Retry-After` is the
            // canonical 429 hint.
            Response.Headers["X-RateLimit-Limit"] = _introspectLimiter.RequestsPerWindow.ToString();
            Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString();
            Response.Headers["X-RateLimit-Window"] = _introspectLimiter.WindowSeconds.ToString();
            if (!decision.Allowed)
            {
                Response.Headers["Retry-After"] = decision.RetryAfterSeconds.ToString();
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    error = "rate_limit_exceeded",
                    error_description = "introspect cap per client_id reached.",
                    retry_after_seconds = decision.RetryAfterSeconds,
                });
            }
        }

        if (form is null || string.IsNullOrWhiteSpace(form.Token))
        {
            return BadRequest(new { error = "invalid_request", error_description = "token is required." });
        }

        var result = _validator.Validate(form.Token);
        if (!result.Ok)
        {
            // RFC 7662 §2.2: per-token errors map to active=false
            // (NOT to HTTP 4xx). The 4xx response codes are
            // reserved for transport-layer errors (missing auth,
            // unsupported media type, etc.).
            return Ok(new
            {
                active = false,
            });
        }

        var (iat, exp) = ExtractTimestamps(form.Token);
        return Ok(new
        {
            active = true,
            scope = client.Scope,
            client_id = client.ClientId,
            username = result.Subject,
            sub = result.Subject,
            iat,
            exp,
            kid = result.Kid,
            token_type = AuthTokenResponse.BearerTokenType,
        });
    }

    /// <summary>
    /// Phase K Wave 11 — Bishop. Form body for the introspection
    /// endpoint. <see cref="Token"/> is the only field consumed;
    /// the optional <see cref="TokenTypeHint"/> is accepted for
    /// RFC 7662 compliance but ignored (we only mint bearer
    /// tokens — no need to disambiguate).
    /// </summary>
    public sealed class IntrospectionForm
    {
        [FromForm(Name = "token")]
        public string? Token { get; set; }

        [FromForm(Name = "token_type_hint")]
        public string? TokenTypeHint { get; set; }
    }

    private static AuthOptions.IntrospectionClient? AuthenticateBasicClient(HttpContext ctx, AuthOptions options)
    {
        if (options.IntrospectionClients.Length == 0) return null;
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var header)) return null;
        var value = header.ToString();
        const string prefix = "Basic ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        string decoded;
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(value.Substring(prefix.Length).Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
        var colon = decoded.IndexOf(':');
        if (colon < 0) return null;
        var clientId = decoded.Substring(0, colon);
        var clientSecret = decoded.Substring(colon + 1);

        foreach (var candidate in options.IntrospectionClients)
        {
            if (!string.Equals(candidate.ClientId, clientId, StringComparison.Ordinal)) continue;
            var resolved = candidate.ResolveSecret();
            if (resolved.Length == 0) continue;
            if (FixedTimeEquals(resolved, clientSecret))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Constant-time string comparison — defends against
    /// timing-side-channel client-secret discovery.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = System.Text.Encoding.UTF8.GetBytes(a);
        var bytesB = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }

    /// <summary>
    /// Pulls the <c>iat</c> + <c>exp</c> integer timestamps out of
    /// the JWT payload. Returns null for either when the claim is
    /// missing; the introspection response uses the null shape
    /// directly (the JSON omits null fields).
    /// </summary>
    private static (long? Iat, long? Exp) ExtractTimestamps(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return (null, null);
            var pad = parts[1].Length % 4;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/')
                + new string('=', pad == 0 ? 0 : 4 - pad);
            var payloadBytes = Convert.FromBase64String(b64);
            using var doc = System.Text.Json.JsonDocument.Parse(payloadBytes);
            long? iat = doc.RootElement.TryGetProperty("iat", out var iatEl)
                && iatEl.ValueKind == System.Text.Json.JsonValueKind.Number
                && iatEl.TryGetInt64(out var iatV)
                ? iatV
                : null;
            long? exp = doc.RootElement.TryGetProperty("exp", out var expEl)
                && expEl.ValueKind == System.Text.Json.JsonValueKind.Number
                && expEl.TryGetInt64(out var expV)
                ? expV
                : null;
            return (iat, exp);
        }
        catch
        {
            return (null, null);
        }
    }
}
