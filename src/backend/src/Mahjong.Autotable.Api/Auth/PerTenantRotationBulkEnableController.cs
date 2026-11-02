using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 20 — Bishop. Admin-gated BULK-ENABLE surface
/// for the per-tenant JWKS rotation policy table. W19 landed
/// bulk-update; W20 completes the bulk triad with bulk-delete
/// + bulk-enable.
///
/// <para>"Enable" semantics: the per-tenant policy row carries
/// a rotation window via <see cref="PerTenantJwksRotationPolicy.RotationStartUtc"/>
/// + <see cref="PerTenantJwksRotationPolicy.RotationCompleteUtc"/>;
/// a policy is "stale" when <c>now &gt; RotationCompleteUtc + overlapDays</c>
/// (see <see cref="PerTenantJwksRotationValidator.EvaluateAsync"/>).
/// "Enable" renews the rotation window so the validator stops
/// blocking signing: the controller stamps
/// <see cref="PerTenantJwksRotationPolicy.RotationStartUtc"/>
/// to <c>UtcNow</c> and
/// <see cref="PerTenantJwksRotationPolicy.RotationCompleteUtc"/>
/// to <c>UtcNow + RenewalWindowDays</c> (default 30; per-row
/// override via the request body).</para>
///
/// <list type="bullet">
///   <item><c>POST /api/admin/per-tenant-jwks-rotation-policies/bulk-enable</c>
///         — accepts a body of <c>{ items: [{ tenantId, renewalWindowDays? }] }</c>;
///         renews the rotation window on each row that exists,
///         records the result-per-row, and emits one
///         <see cref="ReconnectAuditEntry.KindAuthJwksPerTenantBulkEnabled"/>
///         row per successfully-renewed policy. Tenant ids
///         absent from the store are reported in the response
///         under <c>notFoundTenants</c> without failing the
///         batch.</item>
/// </list>
///
/// <para>Mandatory headers + caps + auth posture match the
/// W19 bulk-update + W20 bulk-delete surfaces.</para>
///
/// <para>See <c>docs/per-tenant-jwks-rotation.md §3.5 "Bulk
/// enable"</c> (added W20) for the operator runbook.</para>
/// </summary>
[ApiController]
[Route("api/admin/per-tenant-jwks-rotation-policies/bulk-enable")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class PerTenantRotationBulkEnableController : ControllerBase
{
    public const int MaxBatchSize = PerTenantRotationBulkUpdateController.MaxBatchSize;
    public const int MaxAdminReasonLength = PerTenantRotationBulkUpdateController.MaxAdminReasonLength;
    public const string AdminReasonHeader = PerTenantRotationBulkUpdateController.AdminReasonHeader;
    public const int MaxTenantIdLength = PerTenantRotationBulkDeleteController.MaxTenantIdLength;

    /// <summary>Default rotation-window length applied when an
    /// item omits <see cref="PerTenantRotationBulkEnableItem.RenewalWindowDays"/>.
    /// 30 days mirrors the global rotation cadence baseline.</summary>
    public const int DefaultRenewalWindowDays = 30;

    /// <summary>Maximum permitted renewal window. 365 days —
    /// no operator should ever need a longer renewal; longer
    /// values likely indicate an off-by-thousand input error.</summary>
    public const int MaxRenewalWindowDays = 365;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PerTenantRotationBulkEnableController> _logger;
    private readonly PerTenantJwksRotationOptions _options;
    private readonly IPerTenantJwksRotationStore? _store;

    public PerTenantRotationBulkEnableController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<PerTenantRotationBulkEnableController> logger,
        PerTenantJwksRotationOptions options,
        IPerTenantJwksRotationStore? store = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store;
    }

    private async Task<IActionResult?> GateAsync(CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }
        if (!_options.Enabled || _store is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "per-tenant-disabled",
                detail = "Set JwksRotation:PerTenant:Enabled=true to enable this endpoint.",
            });
        }
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> BulkEnable(
        [FromBody] PerTenantRotationBulkEnableBody? body,
        CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;

        if (body is null) return BadRequest(new { error = "body-required" });
        if (body.Items is null || body.Items.Count == 0)
        {
            return BadRequest(new { error = "items-required" });
        }
        if (body.Items.Count > MaxBatchSize)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                error = "batch-exceeds-maximum",
                maximum = MaxBatchSize,
                actual = body.Items.Count,
            });
        }

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

        for (var i = 0; i < body.Items.Count; i++)
        {
            var item = body.Items[i];
            if (item is null)
            {
                return BadRequest(new { error = "item-null", index = i });
            }
            if (string.IsNullOrWhiteSpace(item.TenantId))
            {
                return BadRequest(new { error = "tenant-id-required", index = i });
            }
            if (item.TenantId.Length > MaxTenantIdLength)
            {
                return BadRequest(new
                {
                    error = "tenant-id-too-long",
                    index = i,
                    maximum = MaxTenantIdLength,
                    actual = item.TenantId.Length,
                });
            }
            if (item.RenewalWindowDays.HasValue)
            {
                if (item.RenewalWindowDays.Value <= 0)
                {
                    return BadRequest(new
                    {
                        error = "renewal-window-days-must-be-positive",
                        index = i,
                        actual = item.RenewalWindowDays.Value,
                    });
                }
                if (item.RenewalWindowDays.Value > MaxRenewalWindowDays)
                {
                    return BadRequest(new
                    {
                        error = "renewal-window-days-exceeds-maximum",
                        index = i,
                        maximum = MaxRenewalWindowDays,
                        actual = item.RenewalWindowDays.Value,
                    });
                }
            }
        }

        var batchId = Guid.NewGuid();
        var enabledTenants = new List<string>(body.Items.Count);
        var notFoundTenants = new List<string>();
        var nowUtc = DateTimeOffset.UtcNow;
        try
        {
            foreach (var item in body.Items)
            {
                var tid = item!.TenantId!.Trim();
                var existing = await _store!.GetAsync(tid, ct);
                if (existing is null)
                {
                    notFoundTenants.Add(tid);
                    continue;
                }
                var windowDays = item.RenewalWindowDays ?? DefaultRenewalWindowDays;
                existing.RotationStartUtc = nowUtc;
                existing.RotationCompleteUtc = nowUtc.AddDays(windowDays);
                await _store!.UpsertAsync(existing, ct);
                enabledTenants.Add(tid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Per-tenant rotation bulk enable failed mid-batch (enabled {Enabled}/{Total}).",
                enabledTenants.Count, body.Items.Count);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "bulk-enable-failed",
                enabledCount = enabledTenants.Count,
                totalCount = body.Items.Count,
            });
        }

        foreach (var tenantId in enabledTenants)
        {
            await WriteAuditAsync(tenantId, reason, batchId, ct);
        }

        return Ok(new
        {
            enabledCount = enabledTenants.Count,
            notFoundCount = notFoundTenants.Count,
            batchId = batchId.ToString("N"),
            enabledTenants = enabledTenants.ToArray(),
            notFoundTenants = notFoundTenants.ToArray(),
        });
    }

    private async Task WriteAuditAsync(string tenantId, string reason, Guid batchId, CancellationToken ct)
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
                Kind = ReconnectAuditEntry.KindAuthJwksPerTenantBulkEnabled,
                Detail = $"tenant={tenantId}|reason={reason}|batchId={batchId:N}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Per-tenant rotation bulk-enable audit write failed for tenant={TenantId}, batchId={BatchId}.",
                tenantId, batchId);
        }
    }
}

/// <summary>
/// Phase K Wave 20 — Bishop. Per-row body for the bulk-enable
/// surface. Carrying the (optional) renewal window per row
/// supports a heterogeneous batch where some tenants need a
/// short 7-day renewal and others a long 90-day renewal in a
/// single request.
/// </summary>
public sealed class PerTenantRotationBulkEnableItem
{
    /// <summary>Tenant id to renew. Required.</summary>
    public string? TenantId { get; set; }

    /// <summary>Optional renewal window in days. Null falls back to
    /// <see cref="PerTenantRotationBulkEnableController.DefaultRenewalWindowDays"/>.
    /// Must be strictly positive and ≤
    /// <see cref="PerTenantRotationBulkEnableController.MaxRenewalWindowDays"/>.</summary>
    public int? RenewalWindowDays { get; set; }
}

/// <summary>
/// Phase K Wave 20 — Bishop. Request body for the bulk-enable
/// surface.
/// </summary>
public sealed class PerTenantRotationBulkEnableBody
{
    /// <summary>Items to renew.</summary>
    public List<PerTenantRotationBulkEnableItem>? Items { get; set; }
}
