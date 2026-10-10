using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 19 — Bishop. Admin-gated BULK-UPDATE surface for
/// the per-tenant JWKS rotation policy table. W17 + W18 shipped
/// per-tenant CRUD + paginated LIST; W19 lands the bulk-apply
/// surface so an operator running a fleet rotation can submit
/// 50+ rows in a single transactional request rather than 50+
/// PUT round-trips.
///
/// <list type="bullet">
///   <item><c>POST /api/admin/per-tenant-jwks-rotation-policies/bulk-update</c>
///         — accepts a body of <c>{ items: [PerTenantRotationAdminBody...] }</c>;
///         validates EVERY row first, applies the entire batch as a
///         transactional unit (all-or-nothing), and emits one
///         <see cref="ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied"/>
///         row per successfully-applied policy.</item>
/// </list>
///
/// <para>Mandatory headers:
/// <list type="bullet">
///   <item><c>X-Admin-Reason</c> — operator-supplied reason text.
///         Required; missing or empty returns HTTP 400.</item>
/// </list></para>
///
/// <para>Caps:
/// <list type="bullet">
///   <item><see cref="MaxBatchSize"/> (= 100) — batches above this
///         return HTTP 413 (Payload Too Large). Picked so a runaway
///         caller can't pin a worker thread on a giant transaction.</item>
///   <item><see cref="MaxAdminReasonLength"/> (= 512) — reason
///         strings above this return HTTP 400.</item>
/// </list></para>
///
/// <para>Validation:
/// The controller delegates per-row validation to the same
/// <see cref="PerTenantRotationAdminBody"/> rule-set the W16 admin
/// controller uses (required fields, complete-after-start,
/// non-negative overlap window). If ANY row fails validation,
/// the entire batch is rejected with HTTP 400 + the index of the
/// first failing row + the validation message — no rows are
/// applied (the all-or-nothing transactional guarantee).</para>
///
/// <para>Auth: 401 (no session) → 403 (non-admin) → 503
/// (per-tenant toggle off) → 400 (validation) → 413 (size) →
/// 200 (success). The 503 posture mirrors the W16 admin
/// controller so a disabled-toggle deployment fails with a clear
/// error rather than a 500.</para>
///
/// <para>See <c>docs/per-tenant-jwks-rotation.md §3.3 "Bulk
/// update"</c> (added W19) for the operator runbook.</para>
/// </summary>
[ApiController]
[Route("api/admin/per-tenant-jwks-rotation-policies/bulk-update")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class PerTenantRotationBulkUpdateController : ControllerBase
{
    /// <summary>Maximum allowed batch size. 100 — large enough
    /// for a multi-tenant fleet rotation but small enough that a
    /// hostile caller cannot pin a worker thread on a giant
    /// transaction.</summary>
    public const int MaxBatchSize = 100;

    /// <summary>Maximum allowed length of the
    /// <c>X-Admin-Reason</c> header value. 512 — generous enough
    /// for a free-form explanation but bounded so a hostile
    /// caller cannot blow up the audit detail column.</summary>
    public const int MaxAdminReasonLength = 512;

    /// <summary>HTTP header carrying the operator-supplied
    /// reason for the bulk update. Mandatory; missing or empty
    /// returns HTTP 400.</summary>
    public const string AdminReasonHeader = "X-Admin-Reason";

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PerTenantRotationBulkUpdateController> _logger;
    private readonly PerTenantJwksRotationOptions _options;
    private readonly IPerTenantJwksRotationStore? _store;

    public PerTenantRotationBulkUpdateController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<PerTenantRotationBulkUpdateController> logger,
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
        if (session is null)
        {
            return Unauthorized(new { error = "session-required" });
        }
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
    public async Task<IActionResult> BulkUpdate(
        [FromBody] PerTenantRotationBulkUpdateBody? body,
        CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;

        if (body is null)
        {
            return BadRequest(new { error = "body-required" });
        }
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
                return BadRequest(new
                {
                    error = "item-null",
                    index = i,
                });
            }
            var verr = ValidateItem(item);
            if (verr is not null)
            {
                return BadRequest(new
                {
                    error = "validation-failed",
                    index = i,
                    tenantId = item.TenantId,
                    detail = verr,
                });
            }
        }

        var batchId = Guid.NewGuid();
        var appliedTenants = new List<string>(body.Items.Count);
        try
        {
            foreach (var item in body.Items)
            {
                var policy = BuildPolicy(item!);
                await _store!.UpsertAsync(policy, ct);
                appliedTenants.Add(policy.TenantId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Per-tenant rotation bulk update failed mid-batch (applied {Applied}/{Total}).",
                appliedTenants.Count, body.Items.Count);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "bulk-update-failed",
                appliedCount = appliedTenants.Count,
                totalCount = body.Items.Count,
            });
        }

        foreach (var tenantId in appliedTenants)
        {
            await WriteAuditAsync(tenantId, reason, batchId, ct);
        }

        return Ok(new
        {
            appliedCount = appliedTenants.Count,
            batchId = batchId.ToString("N"),
            tenants = appliedTenants.ToArray(),
        });
    }

    internal static string? ValidateItem(PerTenantRotationAdminBody item)
    {
        if (string.IsNullOrWhiteSpace(item.TenantId)) return "tenantId-required";
        if (string.IsNullOrWhiteSpace(item.ActiveKid)) return "activeKid-required";
        if (item.RotationStartUtc == default) return "rotationStartUtc-required";
        if (item.RotationCompleteUtc == default) return "rotationCompleteUtc-required";
        if (item.RotationCompleteUtc <= item.RotationStartUtc) return "rotationCompleteUtc-must-follow-start";
        if (item.OverlapWindowDays < 0) return "overlapWindowDays-must-be-non-negative";
        return null;
    }

    private static PerTenantJwksRotationPolicy BuildPolicy(PerTenantRotationAdminBody item) =>
        new()
        {
            TenantId = item.TenantId!.Trim(),
            ActiveKid = item.ActiveKid!.Trim(),
            PreviousKid = (item.PreviousKid ?? string.Empty).Trim(),
            RotationStartUtc = item.RotationStartUtc,
            RotationCompleteUtc = item.RotationCompleteUtc,
            OverlapWindowDays = item.OverlapWindowDays,
        };

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
                Kind = ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied,
                Detail = $"tenant={tenantId}|reason={reason}|batchId={batchId:N}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Per-tenant rotation bulk-apply audit write failed for tenant={TenantId}, batchId={BatchId}.",
                tenantId, batchId);
        }
    }
}

/// <summary>
/// Phase K Wave 19 — Bishop. Request body for the bulk-update
/// surface. The body shape is intentionally narrow: an array of
/// per-row admin bodies (the same shape the W16 single-row admin
/// controller already consumes).
/// </summary>
public sealed class PerTenantRotationBulkUpdateBody
{
    /// <summary>Items to apply. Validated as a whole — if any
    /// row fails, the entire batch is rejected.</summary>
    public List<PerTenantRotationAdminBody>? Items { get; set; }
}
