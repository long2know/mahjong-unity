using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 18 — Bishop. Admin-gated CRUD surface for the
/// retention-ceiling override allow-list. The W18 hard-cap
/// (<see cref="SignalRRetentionPolicyEvaluator"/>) clips any
/// tenant TTL above the global ceiling (default 30 days) DOWN
/// to the ceiling; the override surface lets operators
/// affirmatively exempt a specific tenant from the cap by
/// adding their tenant id to the allow-list.
///
/// <list type="bullet">
///   <item><c>GET    /api/admin/signalr/retention-ceiling</c> —
///         current ceiling + allow-list snapshot.</item>
///   <item><c>POST   /api/admin/signalr/retention-ceiling/overrides</c>
///         — body <c>{ tenantId }</c> + mandatory
///         <c>X-Admin-Reason</c> header. Grants the tenant the
///         exemption. 201 on first grant, 200 on re-grant.</item>
///   <item><c>DELETE /api/admin/signalr/retention-ceiling/overrides/{tenantId}</c>
///         — revokes the exemption. 204 on success; 404 when
///         the tenant wasn't on the list.</item>
/// </list>
///
/// <para>Every write captures a
/// <see cref="ReconnectAuditEntry.KindSignalRRetentionCeilingOverride"/>
/// audit row with detail
/// <c>"{tenantId}|{grant|revoke}|{reason}"</c>. The W17 admin
/// gate (<c>session.Role == "admin"</c>) carries through; the
/// mandatory reason header mirrors the W17 retention surface
/// pattern.</para>
///
/// <para>See <c>docs/realtime-resilience.md §7.1 "Per-tenant
/// retention ceiling"</c>.</para>
/// </summary>
[ApiController]
[Route("api/admin/signalr/retention-ceiling")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class SignalRRetentionCeilingAdminController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const string ActionGrant = "grant";
    public const string ActionRevoke = "revoke";

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignalRRetentionCeilingAdminController> _logger;
    private readonly SignalRRetentionCeilingOptions _options;
    private readonly SignalRRetentionPolicyEvaluator _evaluator;

    public SignalRRetentionCeilingAdminController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<SignalRRetentionCeilingAdminController> logger,
        SignalRRetentionCeilingOptions options,
        SignalRRetentionPolicyEvaluator evaluator)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
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
        return null;
    }

    private string? ResolveAdminReason()
    {
        if (HttpContext is null || HttpContext.Request is null) return null;
        if (!HttpContext.Request.Headers.TryGetValue(AdminReasonHeader, out var values)) return null;
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.ToString().Trim();
        }
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;
        return Ok(new
        {
            ceilingMinutes = _evaluator.EffectiveCeilingMinutes,
            ceilingDays = _evaluator.EffectiveCeilingMinutes / (24.0 * 60.0),
            overrides = _options.AllowAboveCeilingTenants
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray(),
            overrideCount = _options.AllowAboveCeilingTenants.Count,
        });
    }

    [HttpPost("overrides")]
    public async Task<IActionResult> Grant(
        [FromBody] SignalRRetentionCeilingOverrideBody? body,
        CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;
        if (body is null) return BadRequest(new { error = "body-required" });
        if (string.IsNullOrWhiteSpace(body.TenantId))
        {
            return BadRequest(new { error = "tenantId-required" });
        }
        var reason = ResolveAdminReason();
        if (reason is null)
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }

        var tenantId = body.TenantId!.Trim();
        var preExisting = _options.AllowAboveCeilingTenants.Contains(tenantId);
        if (!preExisting)
        {
            _options.AllowAboveCeilingTenants.Add(tenantId);
        }
        await WriteAuditAsync(tenantId, ActionGrant, reason, ct);
        var payload = new
        {
            tenantId,
            ceilingMinutes = _evaluator.EffectiveCeilingMinutes,
            overrideApplied = true,
            preExisting,
        };
        if (preExisting)
        {
            return Ok(payload);
        }
        return CreatedAtAction(nameof(Get), payload);
    }

    [HttpDelete("overrides/{tenantId}")]
    public async Task<IActionResult> Revoke(
        [FromRoute] string tenantId,
        CancellationToken ct)
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
        var removed = _options.AllowAboveCeilingTenants.RemoveAll(t =>
            string.Equals(t, tenantId, StringComparison.Ordinal));
        if (removed == 0)
        {
            return NotFound(new { error = "tenant-not-found", tenantId });
        }
        await WriteAuditAsync(tenantId, ActionRevoke, reason, ct);
        return NoContent();
    }

    private async Task WriteAuditAsync(string tenantId, string action, string reason, CancellationToken ct)
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
                Kind = ReconnectAuditEntry.KindSignalRRetentionCeilingOverride,
                Detail = $"{tenantId}|{action}|{reason}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SignalR retention ceiling override audit write failed for tenant={TenantId}, action={Action}.",
                tenantId, action);
        }
    }
}

/// <summary>POST body for the W18 retention-ceiling override
/// grant. <c>X-Admin-Reason</c> header carries the audit
/// rationale (validated separately).</summary>
public sealed class SignalRRetentionCeilingOverrideBody
{
    public string? TenantId { get; set; }
}
