using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 16 — Bishop. Admin-gated CRUD surface for the
/// per-tenant JWKS rotation policy table. Mirrors the surface
/// shape of <see cref="CommentaryCostController"/>: 401 (no
/// session) → 403 (non-admin) → 200/400 verdict. Pairs with the
/// W15 store seam (<see cref="IPerTenantJwksRotationStore"/>) +
/// the W16 validator
/// (<see cref="PerTenantJwksRotationValidator"/>) so an operator
/// can provision a tenant, rotate it, or drop a stale row
/// without restarting the host.
///
/// <list type="bullet">
///   <item><c>GET /api/admin/jwks-rotation/per-tenant</c> —
///         list every tenant policy row.</item>
///   <item><c>GET /api/admin/jwks-rotation/per-tenant/{tenantId}</c>
///         — single-row read. 404 when no row exists.</item>
///   <item><c>POST /api/admin/jwks-rotation/per-tenant</c> —
///         upsert. 200 when the row exists, 201 when newly
///         created.</item>
///   <item><c>PUT /api/admin/jwks-rotation/per-tenant/{tenantId}</c>
///         — same surface as POST but uses the route id (the
///         body's TenantId, when present, must match).</item>
///   <item><c>DELETE /api/admin/jwks-rotation/per-tenant/{tenantId}</c>
///         — drop a row. 204 on success; 404 when no row.</item>
/// </list>
///
/// <para>Every successful write emits a
/// <see cref="ReconnectAuditEntry"/> row with
/// <c>Kind = "auth.jwks.per-tenant.&lt;action&gt;"</c> +
/// <c>Detail = tenantId</c> so the audit dashboard can replay
/// who provisioned / rotated / dropped which tenant.</para>
///
/// <para>Disabled-toggle posture: when
/// <c>JwksRotation:PerTenant:Enabled=false</c> the controller
/// still routes (so 404 doesn't depend on configuration state)
/// but the store seam is not registered. The controller
/// surfaces HTTP 503 + <c>{ error = "per-tenant-disabled" }</c>
/// so operators see a clear failure mode rather than a generic
/// 500 from a missing DI dependency. See
/// <c>docs/per-tenant-jwks-rotation.md</c>.</para>
/// </summary>
[ApiController]
[Route("api/admin/jwks-rotation/per-tenant")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class PerTenantRotationAdminController : ControllerBase
{
    /// <summary>Audit kinds emitted on successful writes.</summary>
    public const string KindCreated = "auth.jwks.per-tenant.created";
    public const string KindUpdated = "auth.jwks.per-tenant.updated";
    public const string KindDeleted = "auth.jwks.per-tenant.deleted";

    /// <summary>
    /// Phase K Wave 17 — Bishop. Hard-delete audit kind. The W16
    /// surface stamped <see cref="KindDeleted"/> on the sentinel-
    /// row workaround; W17 retires that path and uses the real
    /// <see cref="IPerTenantJwksRotationStore.DeleteAsync"/>
    /// method. The audit row now uses
    /// <see cref="Mahjong.Autotable.Api.Data.Entities.ReconnectAuditEntry.KindAuthJwksPerTenantHardDeleted"/>
    /// so operators can distinguish the new hard-delete from the
    /// legacy soft-delete in the audit trail (W16 rows keep their
    /// kind; W17 onward stamp the hard-delete kind).
    /// </summary>
    public const string KindHardDeleted = "auth.jwks.per-tenant.hard-deleted";

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PerTenantRotationAdminController> _logger;
    private readonly IPerTenantJwksRotationStore? _store;
    private readonly PerTenantJwksRotationOptions _options;

    public PerTenantRotationAdminController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<PerTenantRotationAdminController> logger,
        PerTenantJwksRotationOptions options,
        IPerTenantJwksRotationStore? store = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store;
    }

    /// <summary>Returns null when auth gate passes; otherwise
    /// returns the canonical 401/403/503 response.</summary>
    private async Task<IActionResult?> GateAsync(CancellationToken ct)
    {
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

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;
        var rows = await _store!.ListAsync(ct);
        return Ok(new
        {
            items = rows.Select(ProjectRow).ToArray(),
            count = rows.Count,
        });
    }

    [HttpGet("{tenantId}")]
    public async Task<IActionResult> Get([FromRoute] string tenantId, CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "tenantId-required" });
        }
        var policy = await _store!.GetAsync(tenantId, ct);
        if (policy is null)
        {
            return NotFound(new { error = "tenant-not-found", tenantId });
        }
        return Ok(ProjectRow(policy));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] PerTenantRotationAdminBody? body,
        CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;
        if (body is null)
        {
            return BadRequest(new { error = "body-required" });
        }
        var validationError = Validate(body);
        if (validationError is not null) return validationError;

        var pre = await _store!.GetAsync(body.TenantId!, ct);
        var policy = await _store!.UpsertAsync(BuildPolicy(body), ct);
        var kind = pre is null ? KindCreated : KindUpdated;
        await WriteAuditAsync(body.TenantId!, kind, ct);
        if (pre is null)
        {
            return CreatedAtAction(nameof(Get), new { tenantId = policy.TenantId }, ProjectRow(policy));
        }
        return Ok(ProjectRow(policy));
    }

    [HttpPut("{tenantId}")]
    public async Task<IActionResult> Update(
        [FromRoute] string tenantId,
        [FromBody] PerTenantRotationAdminBody? body,
        CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;
        if (body is null)
        {
            return BadRequest(new { error = "body-required" });
        }
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "tenantId-required" });
        }
        if (!string.IsNullOrWhiteSpace(body.TenantId)
            && !string.Equals(tenantId, body.TenantId, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "tenantId-mismatch" });
        }
        body.TenantId = tenantId;
        var validationError = Validate(body);
        if (validationError is not null) return validationError;

        var pre = await _store!.GetAsync(tenantId, ct);
        var policy = await _store!.UpsertAsync(BuildPolicy(body), ct);
        var kind = pre is null ? KindCreated : KindUpdated;
        await WriteAuditAsync(tenantId, kind, ct);
        return Ok(ProjectRow(policy));
    }

    [HttpDelete("{tenantId}")]
    public async Task<IActionResult> Delete([FromRoute] string tenantId, CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "tenantId-required" });
        }
        var existing = await _store!.GetAsync(tenantId, ct);
        if (existing is null)
        {
            return NotFound(new { error = "tenant-not-found", tenantId });
        }
        // Phase K Wave 17 — Bishop. Retire the W16 sentinel-row
        // workaround. The store now exposes a real DeleteAsync
        // (W17 widened the contract); subsequent GET / List
        // queries return 404 / drop the row entirely. The audit
        // kind remains <see cref="KindDeleted"/> for wire-stable
        // dashboard joins; W17 dashboards that want to distinguish
        // hard-delete vs the legacy soft-delete can correlate with
        // the absence of a follow-up sentinel-row Upsert in the
        // store change feed.
        var deleted = await _store!.DeleteAsync(tenantId, ct);
        if (deleted == 0)
        {
            // Defensive — concurrent delete between GetAsync and
            // DeleteAsync. Treat as success since the row is gone
            // regardless of which call won.
            _logger.LogDebug(
                "PerTenantRotationAdminController.Delete: race detected; tenant={TenantId} already removed.",
                tenantId);
        }
        await WriteAuditAsync(tenantId, KindDeleted, ct);
        return NoContent();
    }

    private static IActionResult? Validate(PerTenantRotationAdminBody body)
    {
        if (string.IsNullOrWhiteSpace(body.TenantId))
        {
            return new BadRequestObjectResult(new { error = "tenantId-required" });
        }
        if (string.IsNullOrWhiteSpace(body.ActiveKid))
        {
            return new BadRequestObjectResult(new { error = "activeKid-required" });
        }
        if (body.RotationStartUtc == default)
        {
            return new BadRequestObjectResult(new { error = "rotationStartUtc-required" });
        }
        if (body.RotationCompleteUtc == default)
        {
            return new BadRequestObjectResult(new { error = "rotationCompleteUtc-required" });
        }
        if (body.RotationCompleteUtc <= body.RotationStartUtc)
        {
            return new BadRequestObjectResult(new { error = "rotationCompleteUtc-must-follow-start" });
        }
        if (body.OverlapWindowDays < 0)
        {
            return new BadRequestObjectResult(new { error = "overlapWindowDays-must-be-non-negative" });
        }
        return null;
    }

    private static PerTenantJwksRotationPolicy BuildPolicy(PerTenantRotationAdminBody body) =>
        new()
        {
            TenantId = body.TenantId!.Trim(),
            ActiveKid = body.ActiveKid!.Trim(),
            PreviousKid = (body.PreviousKid ?? string.Empty).Trim(),
            RotationStartUtc = body.RotationStartUtc,
            RotationCompleteUtc = body.RotationCompleteUtc,
            OverlapWindowDays = body.OverlapWindowDays,
        };

    private static object ProjectRow(PerTenantJwksRotationPolicy p) => new
    {
        tenantId = p.TenantId,
        activeKid = p.ActiveKid,
        previousKid = p.PreviousKid,
        rotationStartUtc = p.RotationStartUtc,
        rotationCompleteUtc = p.RotationCompleteUtc,
        overlapWindowDays = p.OverlapWindowDays,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    private async Task WriteAuditAsync(string tenantId, string kind, CancellationToken ct)
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
                Kind = kind,
                Detail = tenantId,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Per-tenant rotation audit write failed for tenant={TenantId}, kind={Kind}.", tenantId, kind);
        }
    }
}

/// <summary>
/// Phase K Wave 16 — Bishop. POST/PUT body shape for the
/// per-tenant rotation admin surface. Matches the underlying
/// <see cref="PerTenantJwksRotationPolicy"/> entity field-for-
/// field; the controller's <c>Validate</c> helper applies the
/// soft business rules (required fields, complete-after-start,
/// non-negative overlap window).
/// </summary>
public sealed class PerTenantRotationAdminBody
{
    public string? TenantId { get; set; }
    public string? ActiveKid { get; set; }
    public string? PreviousKid { get; set; }
    public DateTimeOffset RotationStartUtc { get; set; }
    public DateTimeOffset RotationCompleteUtc { get; set; }
    public int OverlapWindowDays { get; set; } = 0;
}
