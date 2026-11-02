using System.Globalization;
using System.Security.Claims;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Spectator;

/// <summary>
/// Phase K Wave 12 — Bishop. Spectator handoff surface — mints
/// a short-lived JWT scoped to a single game so non-cookie
/// clients (native mobile, embedded Janus, headless test
/// harnesses) can attach to spectator endpoints without round-
/// tripping through the cookie-based auth flow.
///
/// <list type="bullet">
///   <item><c>POST /api/spectator/handoff</c> — body
///         <c>{ gameId }</c>. Resolves the caller's session
///         (cookie), confirms they're seated or already
///         spectating, and mints a JWT with
///         <c>scope = "spectator:{gameId}"</c> and a 5-minute
///         TTL. Returns <c>{ token, expiresAt, scope }</c>.</item>
///   <item>The livestream endpoint
///         (<c>/api/replay/{id}/livestream.m3u8</c>) accepts
///         the token via <c>?token=…</c> as an alternative to
///         the session cookie — the
///         <see cref="SpectatorHandoffTokenValidator"/>
///         resolves both the signature + the scope claim and
///         returns the canonical envelope.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/spectator")]
public sealed class SpectatorHandoffController : ControllerBase
{
    /// <summary>Default spectator handoff TTL. 5 minutes per
    /// the W12 contract; short enough that a leaked token has
    /// limited blast radius, long enough that a slow mobile
    /// network can still authenticate before expiry.</summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Scope prefix stamped on minted tokens. The
    /// validator pins this prefix so a token minted for game A
    /// can't be replayed against game B.</summary>
    public const string ScopePrefix = "spectator:";

    private readonly AuthCookieService _cookies;
    private readonly JwtIssuingService _issuer;
    private readonly ILogger<SpectatorHandoffController> _logger;
    private readonly ISpectatorHandoffAuditStore? _audit;
    private readonly IOptionsMonitor<SpectatorHandoffAuditOptions>? _auditOptions;
    private readonly Mahjong.Autotable.Api.Observability.TournamentQueryLatencyMetrics? _latencyMetrics;

    public SpectatorHandoffController(
        AuthCookieService cookies,
        JwtIssuingService issuer,
        ILogger<SpectatorHandoffController> logger,
        ISpectatorHandoffAuditStore? audit = null,
        IOptionsMonitor<SpectatorHandoffAuditOptions>? auditOptions = null,
        Mahjong.Autotable.Api.Observability.TournamentQueryLatencyMetrics? latencyMetrics = null)
    {
        _cookies = cookies;
        _issuer = issuer;
        _logger = logger;
        _audit = audit;
        _auditOptions = auditOptions;
        _latencyMetrics = latencyMetrics;
    }

    [HttpPost("handoff")]
    [EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> Handoff([FromBody] HandoffBody? body, CancellationToken ct)
    {
        if (body is null || body.GameId == Guid.Empty)
        {
            return BadRequest(new { error = "gameId is required." });
        }
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
        {
            return Unauthorized(new { error = "session-required" });
        }
        var scope = $"{ScopePrefix}{body.GameId:D}";
        // Phase K Wave 13 — Bishop. Generate a deterministic jti
        // so the audit row + the JWT carry the same identifier.
        // The claim lands inside the existing "claims" envelope
        // (matches the rest of the issuer's payload shape).
        var jti = Guid.NewGuid().ToString("D");
        var claims = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["game_id"] = body.GameId.ToString("D"),
            ["jti"] = jti,
        };
        var result = await _issuer.IssueAsync(
            session.PlayerId,
            claims,
            DefaultTokenLifetime,
            ct);
        // Phase K Wave 13 — Bishop. Write the audit row after the
        // mint succeeds so we don't stamp a row for a token that
        // never reached the issuer. The store is optional —
        // legacy callers that haven't wired the store still get
        // a working handoff (with a logged warning).
        if (_audit is null)
        {
            _logger.LogDebug(
                "Spectator handoff audit store not registered; skipping audit row for jti={Jti}.",
                jti);
        }
        else
        {
            try
            {
                var ua = HttpContext.Request.Headers.UserAgent.ToString();
                if (ua.Length > 256) ua = ua[..256];
                var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
                if (ip.Length > 64) ip = ip[..64];
                await _audit.InsertAsync(new SpectatorHandoffAuditRecord
                {
                    UserId = session.PlayerId,
                    GameId = body.GameId,
                    TokenJti = jti,
                    IssuedAt = DateTime.UtcNow,
                    Scope = scope,
                    ClientIp = ip,
                    UserAgent = ua,
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Spectator handoff audit insert failed for jti={Jti}; mint succeeded.",
                    jti);
            }
        }
        return Ok(new
        {
            token = result.Token,
            expiresAt = result.ExpiresAtUtc,
            scope,
            ttlSeconds = (int)DefaultTokenLifetime.TotalSeconds,
        });
    }

    /// <summary>Phase K Wave 12 — Bishop. POST body.</summary>
    public sealed class HandoffBody
    {
        public Guid GameId { get; set; }
    }

    /// <summary>
    /// Phase K Wave 14 — Bishop. Admin-only paginated query over the
    /// W13 audit trail. The endpoint pins three filters: an optional
    /// <c>gameId</c>, an optional UTC time range
    /// (<c>from</c>/<c>to</c>) and the standard <c>skip</c>/<c>limit</c>
    /// pair. Results carry the audited columns (no token material) so
    /// the security review can reconstruct issuance history without
    /// re-fetching the token store. See
    /// <c>docs/spectator-handoff.md §4 "Audit query API"</c>.
    /// </summary>
    [HttpGet("handoff/audit")]
    [EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> QueryAudit(
        [FromQuery(Name = "gameId")] Guid? gameId,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "skip")] int? skip,
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct = default)
    {
        // Phase K Wave 14 — Bishop. HTTP precedence:
        //   401 (no session) → 403 (non-admin) → 503 (store unwired) → 200.
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
        {
            return Unauthorized(new { error = "session-required" });
        }
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "admin-required",
            });
        }
        if (_audit is null)
        {
            // Defence in depth — without a store wired the endpoint
            // cannot fulfil the query, so we surface 503 rather than
            // returning an empty array (which could mask a real
            // mis-configuration).
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "audit-store-unavailable",
            });
        }

        DateTime? fromUtc = ParseUtc(from);
        if (!string.IsNullOrWhiteSpace(from) && fromUtc is null)
        {
            return BadRequest(new { error = "from must be an ISO 8601 UTC timestamp." });
        }
        DateTime? toUtc = ParseUtc(to);
        if (!string.IsNullOrWhiteSpace(to) && toUtc is null)
        {
            return BadRequest(new { error = "to must be an ISO 8601 UTC timestamp." });
        }

        var configuredPageSize = _auditOptions?.CurrentValue.PageSize ?? SpectatorHandoffAuditOptions.DefaultPageSize;
        if (configuredPageSize <= 0) configuredPageSize = SpectatorHandoffAuditOptions.DefaultPageSize;
        if (configuredPageSize > SpectatorHandoffAuditOptions.MaxPageSize)
            configuredPageSize = SpectatorHandoffAuditOptions.MaxPageSize;
        var take = Math.Clamp(limit ?? configuredPageSize, 1, SpectatorHandoffAuditOptions.MaxPageSize);
        var skipN = Math.Max(0, skip ?? 0);

        // Phase K Wave 15 — Bishop. Time the query so the
        // tournament-scale latency histogram can surface a p99 by
        // page-size bucket. The metric is side-channel — when the
        // collector is null (test fixtures that don't wire it) the
        // recording is a no-op.
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = await _audit.QueryAsync(gameId, fromUtc, toUtc, skipN, take, ct);
        _latencyMetrics?.ObserveTimestamp("spectator-audit-query", take, t0);
        return Ok(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                userId = r.UserId,
                gameId = r.GameId,
                tokenJti = r.TokenJti,
                issuedAt = r.IssuedAt,
                scope = r.Scope,
                clientIp = r.ClientIp,
                userAgent = r.UserAgent,
            }).ToArray(),
            count = rows.Count,
            skip = skipN,
            limit = take,
            pageSize = configuredPageSize,
        });
    }

    private static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }
}

/// <summary>
/// Phase K Wave 12 — Bishop. Token-validator companion for the
/// spectator handoff surface. Wraps
/// <see cref="JwtValidationService"/> with the additional
/// per-game scope check so a token minted for game A can't be
/// replayed against game B.
/// </summary>
public sealed class SpectatorHandoffTokenValidator
{
    private readonly JwtValidationService _validator;

    public SpectatorHandoffTokenValidator(JwtValidationService validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// Validates a spectator-handoff token. Returns
    /// <see cref="SpectatorTokenValidationResult.Allow"/> when
    /// the signature is good AND the token's <c>scope</c> claim
    /// matches <c>"spectator:{gameId}"</c>. Returns
    /// <see cref="SpectatorTokenValidationResult.Deny"/> with a
    /// reason code on every failure path.
    /// </summary>
    public SpectatorTokenValidationResult Validate(string? token, Guid gameId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return SpectatorTokenValidationResult.Deny("token-missing");
        }
        var result = _validator.Validate(token);
        if (!result.Ok)
        {
            return SpectatorTokenValidationResult.Deny(result.Error ?? "token-invalid");
        }
        var expectedScope = $"{SpectatorHandoffController.ScopePrefix}{gameId:D}";
        var scope = ExtractScopeClaim(result);
        if (!string.Equals(scope, expectedScope, StringComparison.Ordinal))
        {
            return SpectatorTokenValidationResult.Deny("scope-mismatch");
        }
        return SpectatorTokenValidationResult.Allow(result.Subject ?? string.Empty);
    }

    private static string ExtractScopeClaim(JwtValidationResult result)
    {
        if (result.Claims is null) return string.Empty;
        if (result.Claims.TryGetValue("scope", out var scopeValue) && scopeValue is not null)
        {
            return scopeValue.ToString() ?? string.Empty;
        }
        return string.Empty;
    }
}

/// <summary>
/// Phase K Wave 12 — Bishop. Validation result envelope.
/// </summary>
public readonly record struct SpectatorTokenValidationResult(
    bool Allowed,
    string Subject,
    string Reason)
{
    public static SpectatorTokenValidationResult Allow(string subject) =>
        new(true, subject, "ok");

    public static SpectatorTokenValidationResult Deny(string reason) =>
        new(false, string.Empty, reason);
}
