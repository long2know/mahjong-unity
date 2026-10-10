using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 17 — Bishop. Admin-gated CRUD surface for the
/// per-tenant SignalR-sequence retention policy table.
/// Operators flip retention from the global default
/// (<see cref="SignalRSequenceStoreOptions.RetentionMinutes"/>)
/// to a per-tenant value (e.g. 30-minute free-tier vs 24-hour
/// enterprise replay window) without restarting the host; the
/// W17 sweep already consults
/// <see cref="ISignalRRetentionPolicyStore.GetAsync"/> once per
/// distinct tenant per tick so the runtime upsert takes effect
/// on the next sweep.
///
/// <list type="bullet">
///   <item><c>GET    /api/admin/signalr/retention</c> — list
///         every tenant's retention row.</item>
///   <item><c>GET    /api/admin/signalr/retention/{tenantId}</c>
///         — single-row read. 404 when no row exists.</item>
///   <item><c>POST   /api/admin/signalr/retention</c> — upsert.
///         200 on update, 201 on create.</item>
///   <item><c>PUT    /api/admin/signalr/retention/{tenantId}</c>
///         — same as POST but uses the route id.</item>
///   <item><c>DELETE /api/admin/signalr/retention/{tenantId}</c>
///         — drop a row. 204 on success; 404 when no row.</item>
/// </list>
///
/// <para>Every WRITE request requires the <c>X-Admin-Reason</c>
/// header (mirrors the W17 commentary + replay surfaces). The
/// reason is mandatory (empty / whitespace-only → 400) and
/// captured verbatim on the audit row's
/// <see cref="ReconnectAuditEntry.Detail"/> field as
/// <c>"{tenantId}|{reason}"</c>.</para>
///
/// <para>Disabled-toggle posture: when the W17 per-tenant store
/// is not registered (<c>SignalR:Sequences:PerTenant:Enabled=false</c>),
/// the controller still routes but returns HTTP 503 +
/// <c>{ error = "per-tenant-disabled" }</c>. See
/// <c>docs/realtime-resilience.md §7 "Per-tenant retention"</c>.</para>
/// </summary>
[ApiController]
[Route("api/admin/signalr/retention")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class SignalRRetentionAdminController : ControllerBase
{
    /// <summary>Mandatory header on every write. The value is
    /// captured verbatim on the audit row.</summary>
    public const string AdminReasonHeader = "X-Admin-Reason";

    /// <summary>Wire-stable audit kinds — re-exported from
    /// <see cref="ReconnectAuditEntry"/> so callers can reach them
    /// off the controller without an extra import.</summary>
    public const string KindCreated = ReconnectAuditEntry.KindSignalRRetentionCreated;
    public const string KindUpdated = ReconnectAuditEntry.KindSignalRRetentionUpdated;
    public const string KindDeleted = ReconnectAuditEntry.KindSignalRRetentionDeleted;

    /// <summary>Upper bound on per-tenant retention (minutes).
    /// 60 days is well beyond the longest reconnect window the
    /// platform has seen; pinning a max keeps a runaway upsert
    /// from costing the operator the database.</summary>
    public const int MaxRetentionMinutes = SignalRRetentionPolicy.MaxRetentionMinutes;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignalRRetentionAdminController> _logger;
    private readonly ISignalRRetentionPolicyStore? _store;

    public SignalRRetentionAdminController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<SignalRRetentionAdminController> logger,
        ISignalRRetentionPolicyStore? store = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "admin-required",
            });
        }
        if (_store is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "per-tenant-disabled",
                detail = "Register ISignalRRetentionPolicyStore to enable this endpoint.",
            });
        }
        return null;
    }

    private string? ResolveAdminReason()
    {
        if (HttpContext is null || HttpContext.Request is null) return null;
        if (!HttpContext.Request.Headers.TryGetValue(AdminReasonHeader, out var values))
        {
            return null;
        }
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.ToString().Trim();
            }
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
        var row = await _store!.GetAsync(tenantId, ct);
        if (row is null)
        {
            return NotFound(new { error = "tenant-not-found", tenantId });
        }
        return Ok(ProjectRow(row));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] SignalRRetentionAdminBody? body,
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

        var reason = ResolveAdminReason();
        if (reason is null)
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }

        var pre = await _store!.GetAsync(body.TenantId!, ct);
        var row = await _store!.UpsertAsync(BuildPolicy(body), ct);
        var kind = pre is null ? KindCreated : KindUpdated;
        await WriteAuditAsync(body.TenantId!, kind, reason, ct);
        if (pre is null)
        {
            return CreatedAtAction(nameof(Get), new { tenantId = row.TenantId }, ProjectRow(row));
        }
        return Ok(ProjectRow(row));
    }

    [HttpPut("{tenantId}")]
    public async Task<IActionResult> Update(
        [FromRoute] string tenantId,
        [FromBody] SignalRRetentionAdminBody? body,
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

        var reason = ResolveAdminReason();
        if (reason is null)
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }

        var pre = await _store!.GetAsync(tenantId, ct);
        var row = await _store!.UpsertAsync(BuildPolicy(body), ct);
        var kind = pre is null ? KindCreated : KindUpdated;
        await WriteAuditAsync(tenantId, kind, reason, ct);
        return Ok(ProjectRow(row));
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
        var reason = ResolveAdminReason();
        if (reason is null)
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        var existing = await _store!.GetAsync(tenantId, ct);
        if (existing is null)
        {
            return NotFound(new { error = "tenant-not-found", tenantId });
        }
        var deleted = await _store!.DeleteAsync(tenantId, ct);
        if (deleted == 0)
        {
            _logger.LogDebug(
                "SignalRRetentionAdminController.Delete: race detected; tenant={TenantId} already removed.",
                tenantId);
        }
        await WriteAuditAsync(tenantId, KindDeleted, reason, ct);
        return NoContent();
    }

    private static IActionResult? Validate(SignalRRetentionAdminBody body)
    {
        if (string.IsNullOrWhiteSpace(body.TenantId))
        {
            return new BadRequestObjectResult(new { error = "tenantId-required" });
        }
        if (body.RetentionMinutes <= 0)
        {
            return new BadRequestObjectResult(new { error = "retentionMinutes-must-be-positive" });
        }
        if (body.RetentionMinutes > MaxRetentionMinutes)
        {
            return new BadRequestObjectResult(new
            {
                error = "retentionMinutes-exceeds-maximum",
                maximum = MaxRetentionMinutes,
            });
        }
        return null;
    }

    private static SignalRRetentionPolicy BuildPolicy(SignalRRetentionAdminBody body) =>
        new()
        {
            TenantId = body.TenantId!.Trim(),
            RetentionMinutes = body.RetentionMinutes,
        };

    private static object ProjectRow(SignalRRetentionPolicy p) => new
    {
        tenantId = p.TenantId,
        retentionMinutes = p.RetentionMinutes,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
        createdAtOffset = p.CreatedAtOffset,
        updatedAtOffset = p.UpdatedAtOffset,
    };

    private async Task WriteAuditAsync(string tenantId, string kind, string reason, CancellationToken ct)
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
                Detail = $"{tenantId}|{reason}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SignalR retention audit write failed for tenant={TenantId}, kind={Kind}.",
                tenantId, kind);
        }
    }
}

/// <summary>
/// Phase K Wave 17 — Bishop. POST/PUT body shape for the
/// SignalR-retention admin surface. The
/// <c>SignalRRetentionPolicy.RetentionMinutes</c> column is the
/// only operator-tunable knob; <c>CreatedAt</c> / <c>UpdatedAt</c>
/// are stamped by the store on write.
/// </summary>
public sealed class SignalRRetentionAdminBody
{
    public string? TenantId { get; set; }
    public int RetentionMinutes { get; set; }
}
