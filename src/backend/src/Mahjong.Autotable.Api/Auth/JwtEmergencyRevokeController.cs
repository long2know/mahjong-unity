using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 22 — Bishop. Prometheus counter tracking
/// per-tenant emergency JWT key revocations. Stamped once per
/// successful revoke call by
/// <see cref="JwtEmergencyRevokeController"/>.
///
/// <para>Wire shape:
/// <code>
/// # HELP jwt_emergency_revoke_total Per-tenant emergency JWT key revocations recorded by the W22 admin surface.
/// # TYPE jwt_emergency_revoke_total counter
/// jwt_emergency_revoke_total{tenant="tenant-abc"} 7
/// </code></para>
/// </summary>
public sealed class JwtEmergencyRevokeMetrics
{
    public const string MetricName = "jwt_emergency_revoke_total";
    public const string TenantLabel = "tenant";
    public const string UnknownTenantBucket = "_unknown";

    private readonly ConcurrentDictionary<string, long> _counters =
        new(StringComparer.Ordinal);

    public void Increment(string? tenantId)
    {
        var key = string.IsNullOrEmpty(tenantId) ? UnknownTenantBucket : tenantId;
        _counters.AddOrUpdate(key, 1, (_, prev) => prev + 1);
    }

    public long Get(string tenantId)
    {
        var key = string.IsNullOrEmpty(tenantId) ? UnknownTenantBucket : tenantId;
        return _counters.TryGetValue(key, out var v) ? v : 0;
    }

    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_counters, StringComparer.Ordinal);

    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Per-tenant emergency JWT key revocations recorded by the W22 admin surface. Labelled by `tenant`.");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" counter");
        foreach (var kv in _counters)
        {
            sb.Append(MetricName)
              .Append('{').Append(TenantLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key)).Append("\"} ")
              .AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string EscapeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.IndexOfAny(['\\', '"', '\n']) < 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Phase K Wave 22 — Bishop. Admin-gated endpoint that
/// emergency-revokes a per-tenant JWT signing key id. Surface:
/// <c>POST /api/admin/jwt-keys/emergency-revoke?tenant=...&amp;kid=...</c>.
///
/// <para>The endpoint persists a
/// <see cref="JwtEmergencyRevokedKid"/> row (idempotent on the
/// <c>(TenantId, Kid)</c> unique index), invalidates the global
/// <see cref="JwksCacheService"/> so the next JWKS-document
/// fetch returns a freshly-rebuilt document (the revoked kid
/// is filtered by downstream surfaces), and increments
/// <see cref="JwtEmergencyRevokeMetrics"/> labelled by
/// tenant.</para>
///
/// <para>Auth: 401 / 403 / 400 / 200. Mandatory
/// <c>X-Admin-Reason</c> header.</para>
/// </summary>
[ApiController]
[Route("api/admin/jwt-keys/emergency-revoke")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class JwtEmergencyRevokeController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const int MaxAdminReasonLength = 512;
    public const int MaxTenantLength = 128;
    public const int MaxKidLength = 128;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JwksCacheService? _cache;
    private readonly JwtEmergencyRevokeMetrics? _metrics;

    public JwtEmergencyRevokeController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        JwksCacheService? cache = null,
        JwtEmergencyRevokeMetrics? metrics = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _cache = cache;
        _metrics = metrics;
    }

    [HttpPost]
    public async Task<IActionResult> Revoke(
        [FromQuery] string? tenant,
        [FromQuery] string? kid,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }
        if (!HttpContext.Request.Headers.TryGetValue(AdminReasonHeader, out var reasonValues))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        var adminReason = reasonValues.ToString();
        if (string.IsNullOrWhiteSpace(adminReason))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        if (adminReason.Length > MaxAdminReasonLength)
        {
            return BadRequest(new { error = "admin-reason-too-long", maximum = MaxAdminReasonLength });
        }
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return BadRequest(new { error = "tenant-required" });
        }
        if (tenant.Length > MaxTenantLength)
        {
            return BadRequest(new { error = "tenant-too-long", maximum = MaxTenantLength });
        }
        if (string.IsNullOrWhiteSpace(kid))
        {
            return BadRequest(new { error = "kid-required" });
        }
        if (kid.Length > MaxKidLength)
        {
            return BadRequest(new { error = "kid-too-long", maximum = MaxKidLength });
        }

        var tenantId = tenant.Trim();
        var kidValue = kid.Trim();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.JwtEmergencyRevokedKids
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Kid == kidValue, ct);

        bool idempotent;
        if (existing is null)
        {
            db.JwtEmergencyRevokedKids.Add(new JwtEmergencyRevokedKid
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kid = kidValue,
                RevokedAtUtc = DateTime.UtcNow,
                Reason = adminReason,
            });
            idempotent = false;
        }
        else
        {
            idempotent = true;
        }

        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "admin",
            At = DateTime.UtcNow,
            Kind = ReconnectAuditEntry.KindJwtEmergencyRevoke,
            Detail = $"reason={adminReason}|tenant={tenantId}|kid={kidValue}",
        });

        await db.SaveChangesAsync(ct);

        // Push immediate cache invalidation so downstream
        // validators don't accept a token signed under the
        // revoked kid past the next cache-hit edge.
        _cache?.Invalidate();

        // Counter increments even on idempotent re-revocation —
        // operators want a faithful count of attempts, not
        // unique kids.
        _metrics?.Increment(tenantId);

        return Ok(new
        {
            tenant = tenantId,
            kid = kidValue,
            idempotent,
            revokedAtUtc = existing?.RevokedAtUtc ?? DateTime.UtcNow,
            adminReason,
        });
    }
}
