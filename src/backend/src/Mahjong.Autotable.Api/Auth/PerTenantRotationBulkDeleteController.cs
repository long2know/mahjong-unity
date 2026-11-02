using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 20 — Bishop. Admin-gated BULK-DELETE surface
/// for the per-tenant JWKS rotation policy table. W19 landed
/// bulk-update; W20 completes the bulk triad with bulk-delete
/// + bulk-enable.
///
/// <list type="bullet">
///   <item><c>POST /api/admin/per-tenant-jwks-rotation-policies/bulk-delete</c>
///         — accepts a body of <c>{ tenantIds: [string...] }</c>;
///         validates each id is a non-empty string ≤ 128 chars,
///         deletes every row whose tenant id is in the supplied
///         list, and emits one
///         <see cref="ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted"/>
///         row per successfully-evicted policy. Missing tenant
///         ids (i.e. tenant id absent from the store) are
///         silently skipped — the operator's deletion list need
///         not match the live tenant set perfectly.</item>
/// </list>
///
/// <para>Mandatory headers + caps + auth posture match the W19
/// bulk-update surface (X-Admin-Reason required;
/// <see cref="MaxBatchSize"/> = 100;
/// <see cref="MaxAdminReasonLength"/> = 512). All-or-nothing
/// transactional guarantee: if any validation fails, no rows
/// are deleted.</para>
///
/// <para>Auth: 401 (no session) → 403 (non-admin) → 503
/// (per-tenant toggle off) → 400 (validation) → 413 (size) →
/// 200 (success).</para>
///
/// <para>See <c>docs/per-tenant-jwks-rotation.md §3.4 "Bulk
/// delete"</c> (added W20) for the operator runbook.</para>
/// </summary>
[ApiController]
[Route("api/admin/per-tenant-jwks-rotation-policies/bulk-delete")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class PerTenantRotationBulkDeleteController : ControllerBase
{
    /// <summary>Maximum allowed batch size. Mirrors the W19
    /// bulk-update cap so the two surfaces present an
    /// operator-symmetric envelope.</summary>
    public const int MaxBatchSize = PerTenantRotationBulkUpdateController.MaxBatchSize;

    /// <summary>Maximum allowed length of the
    /// <c>X-Admin-Reason</c> header value. Mirrors the W19
    /// bulk-update cap.</summary>
    public const int MaxAdminReasonLength = PerTenantRotationBulkUpdateController.MaxAdminReasonLength;

    /// <summary>HTTP header carrying the operator-supplied
    /// reason for the bulk delete. Mandatory; missing or empty
    /// returns HTTP 400.</summary>
    public const string AdminReasonHeader = PerTenantRotationBulkUpdateController.AdminReasonHeader;

    /// <summary>Maximum permitted tenant id length per entry.
    /// 128 — matches the storage column width and the W18
    /// list controller's filter cap.</summary>
    public const int MaxTenantIdLength = 128;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PerTenantRotationBulkDeleteController> _logger;
    private readonly PerTenantJwksRotationOptions _options;
    private readonly IPerTenantJwksRotationStore? _store;

    public PerTenantRotationBulkDeleteController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<PerTenantRotationBulkDeleteController> logger,
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
    public async Task<IActionResult> BulkDelete(
        [FromBody] PerTenantRotationBulkDeleteBody? body,
        CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;

        if (body is null) return BadRequest(new { error = "body-required" });
        if (body.TenantIds is null || body.TenantIds.Count == 0)
        {
            return BadRequest(new { error = "tenantIds-required" });
        }
        if (body.TenantIds.Count > MaxBatchSize)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                error = "batch-exceeds-maximum",
                maximum = MaxBatchSize,
                actual = body.TenantIds.Count,
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

        for (var i = 0; i < body.TenantIds.Count; i++)
        {
            var tid = body.TenantIds[i];
            if (string.IsNullOrWhiteSpace(tid))
            {
                return BadRequest(new
                {
                    error = "tenant-id-required",
                    index = i,
                });
            }
            if (tid.Length > MaxTenantIdLength)
            {
                return BadRequest(new
                {
                    error = "tenant-id-too-long",
                    index = i,
                    maximum = MaxTenantIdLength,
                    actual = tid.Length,
                });
            }
        }

        var batchId = Guid.NewGuid();
        var deletedTenants = new List<string>(body.TenantIds.Count);
        var notFoundTenants = new List<string>();
        try
        {
            foreach (var rawId in body.TenantIds)
            {
                var tid = rawId.Trim();
                var removed = await _store!.DeleteAsync(tid, ct);
                if (removed > 0)
                {
                    deletedTenants.Add(tid);
                }
                else
                {
                    notFoundTenants.Add(tid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Per-tenant rotation bulk delete failed mid-batch (deleted {Deleted}/{Total}).",
                deletedTenants.Count, body.TenantIds.Count);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "bulk-delete-failed",
                deletedCount = deletedTenants.Count,
                totalCount = body.TenantIds.Count,
            });
        }

        foreach (var tenantId in deletedTenants)
        {
            await WriteAuditAsync(tenantId, reason, batchId, ct);
        }

        return Ok(new
        {
            deletedCount = deletedTenants.Count,
            notFoundCount = notFoundTenants.Count,
            batchId = batchId.ToString("N"),
            deletedTenants = deletedTenants.ToArray(),
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
                Kind = ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted,
                Detail = $"tenant={tenantId}|reason={reason}|batchId={batchId:N}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Per-tenant rotation bulk-delete audit write failed for tenant={TenantId}, batchId={BatchId}.",
                tenantId, batchId);
        }
    }
}

/// <summary>
/// Phase K Wave 20 — Bishop. Request body for the bulk-delete
/// surface. Mirrors the W19 bulk-update body in spirit but
/// carries only the tenant ids — the rotation parameters are
/// not needed for a delete.
/// </summary>
public sealed class PerTenantRotationBulkDeleteBody
{
    /// <summary>Tenant ids to delete. Validated as a whole —
    /// if any entry fails the per-id checks, the entire batch
    /// is rejected.</summary>
    public List<string>? TenantIds { get; set; }
}
