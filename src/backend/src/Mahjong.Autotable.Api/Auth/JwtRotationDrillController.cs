using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 20 — Bishop. Admin-gated rotation drill
/// endpoint. Wires the
/// <c>POST /api/admin/jwt-keys/rotation-drill</c> surface so
/// an operator can exercise the per-tenant rotation pipeline
/// (validator + JWKS cache invalidation + audit trail) in a
/// non-production environment without minting a real key.
///
/// <para><b>Non-prod-only.</b> The endpoint is gated on
/// <see cref="IWebHostEnvironment.IsProduction"/> — production
/// callers receive HTTP 403. An additional environment
/// variable <see cref="DrillEnvVar"/> may further gate
/// non-prod environments (e.g. staging deployments that want
/// to lock the drill behind a deliberate "yes I really mean
/// it" flag); when the env var is set to <c>"false"</c> the
/// endpoint returns HTTP 403 even in non-prod environments.
/// Default behaviour: enabled in every non-prod environment.</para>
///
/// <para><b>What "drill" means.</b> The endpoint does NOT
/// modify the active signing key — it walks every per-tenant
/// rotation policy row, calls the validator's
/// <see cref="PerTenantJwksRotationValidator.EvaluateAsync"/>,
/// invalidates the JWKS cache, and records the outcome in the
/// audit log. The drill exercises the same code paths a
/// production rotation would touch, but the underlying keys
/// remain unchanged so the operator can repeat the drill
/// safely.</para>
///
/// <para>Mandatory headers + caps + auth posture match the
/// W19 bulk-update surface (X-Admin-Reason required).</para>
///
/// <para>See <c>docs/jwt-rotation.md §14 "Rotation drill"</c>
/// (added W20).</para>
/// </summary>
[ApiController]
[Route("api/admin/jwt-keys/rotation-drill")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class JwtRotationDrillController : ControllerBase
{
    /// <summary>Environment variable that toggles the drill
    /// endpoint in non-prod environments. When unset / empty
    /// the drill is enabled; when set to <c>"false"</c> the
    /// drill returns HTTP 403 with <c>error =
    /// "drill-disabled"</c>.</summary>
    public const string DrillEnvVar = "MAHJONG_JWT_ROTATION_DRILL_ENABLED";

    public const string AdminReasonHeader = PerTenantRotationBulkUpdateController.AdminReasonHeader;
    public const int MaxAdminReasonLength = PerTenantRotationBulkUpdateController.MaxAdminReasonLength;

    private readonly AuthCookieService _cookies;
    private readonly IWebHostEnvironment _env;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JwtRotationDrillController> _logger;
    private readonly PerTenantJwksRotationValidator _validator;
    private readonly PerTenantJwksRotationOptions _options;
    private readonly IPerTenantJwksRotationStore? _store;
    private readonly JwksCacheService? _cache;

    public JwtRotationDrillController(
        AuthCookieService cookies,
        IWebHostEnvironment env,
        IServiceScopeFactory scopeFactory,
        ILogger<JwtRotationDrillController> logger,
        PerTenantJwksRotationValidator validator,
        PerTenantJwksRotationOptions options,
        IPerTenantJwksRotationStore? store = null,
        JwksCacheService? cache = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store;
        _cache = cache;
    }

    [HttpPost]
    public async Task<IActionResult> Drill(CancellationToken ct)
    {
        // Production gate.
        if (_env.IsProduction())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "drill-not-allowed-in-production",
                environment = _env.EnvironmentName,
            });
        }

        // Optional env-var gate on top of the production check.
        var envVar = Environment.GetEnvironmentVariable(DrillEnvVar);
        if (!string.IsNullOrEmpty(envVar) &&
            string.Equals(envVar.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "drill-disabled",
                envVar = DrillEnvVar,
            });
        }

        // Auth gate.
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }

        // X-Admin-Reason header — mirrors the W19 bulk-update
        // surface.
        if (!HttpContext.Request.Headers.TryGetValue(AdminReasonHeader, out var reasonValues))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        var reason = reasonValues.ToString();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        if (reason.Length > MaxAdminReasonLength)
        {
            return BadRequest(new
            {
                error = "admin-reason-too-long",
                maximum = MaxAdminReasonLength,
                actual = reason.Length,
            });
        }

        var drillId = Guid.NewGuid();
        var tenants = new List<string>();
        var verdicts = new List<DrillTenantOutcome>();
        if (_options.Enabled && _store is not null)
        {
            var policies = await _store.ListAsync(ct);
            var nowUtc = DateTimeOffset.UtcNow;
            foreach (var policy in policies)
            {
                tenants.Add(policy.TenantId);
                var verdict = await _validator.EvaluateAsync(policy.TenantId, nowUtc, ct);
                verdicts.Add(new DrillTenantOutcome(
                    policy.TenantId,
                    verdict.Allowed,
                    verdict.Kind.ToString(),
                    verdict.Reason));
            }
        }

        // Invalidate the JWKS cache so the next request
        // re-reads the rotation policy state.
        _cache?.Invalidate();

        // Audit the drill outcome.
        await WriteAuditAsync(reason, drillId, tenants.Count, ct);

        return Ok(new
        {
            drillId = drillId.ToString("N"),
            environment = _env.EnvironmentName,
            perTenantEnabled = _options.Enabled && _store is not null,
            tenantsExercised = tenants.Count,
            tenants = tenants.ToArray(),
            verdicts,
            reason,
        });
    }

    private async Task WriteAuditAsync(string reason, Guid drillId, int tenantCount, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = "admin",
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindJwtKeyRotationDrill,
                Detail = $"reason={reason}|drillId={drillId:N}|env={_env.EnvironmentName}|tenants={tenantCount}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "JWT rotation drill audit write failed (drillId={DrillId}).", drillId);
        }
    }
}

/// <summary>
/// Phase K Wave 20 — Bishop. Per-tenant verdict captured by
/// the drill endpoint. Wire-stable so the operator dashboard
/// can render the drill outcome directly from the response
/// without re-running the validator.
/// </summary>
public sealed record DrillTenantOutcome(
    string TenantId,
    bool Allowed,
    string VerdictKind,
    string? Reason);
