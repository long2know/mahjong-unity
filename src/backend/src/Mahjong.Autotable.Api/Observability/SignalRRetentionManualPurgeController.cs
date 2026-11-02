using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 21 — Bishop. Prometheus counter tracking
/// SignalR-sequence rows purged by the W21 manual purge admin
/// surface. Distinct from the W17 retention sweep — the sweep
/// is the daily/hourly automatic walker; the manual purge is
/// the targeted operator surface used after an incident.
///
/// <para>Wire shape:
/// <code>
/// # HELP signalr_manual_purge_total Total SignalR sequence rows purged by the W21 manual-purge admin surface.
/// # TYPE signalr_manual_purge_total counter
/// signalr_manual_purge_total{tenant="tenant-abc"} 42
/// </code></para>
/// </summary>
public sealed class SignalRManualPurgeMetrics
{
    public const string MetricName = "signalr_manual_purge_total";
    public const string TenantLabel = "tenant";
    public const string UnknownTenantBucket = "_unknown";

    private readonly ConcurrentDictionary<string, long> _counters =
        new(StringComparer.Ordinal);

    public void Add(string? tenantId, long delta)
    {
        if (delta <= 0) return;
        var key = string.IsNullOrEmpty(tenantId) ? UnknownTenantBucket : tenantId;
        _counters.AddOrUpdate(key, delta, (_, prev) => prev + delta);
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
          .AppendLine(" Total SignalR sequence rows purged by the W21 manual-purge admin surface. Labelled by `tenant`.");
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
/// Phase K Wave 21 — Bishop. Admin-gated endpoint that bulk-
/// deletes SignalR sequence-store rows older than the supplied
/// <c>before</c> ISO 8601 timestamp.
///
/// <para>Surface:
/// <c>POST /api/admin/signalr/retention-purge?tenant=...&amp;before=ISO8601</c>.
/// </para>
///
/// <para>Auth: 401 / 403 / 400 / 200. Mandatory
/// <c>X-Admin-Reason</c> header. The endpoint is intentionally
/// narrow — it does not sweep the connections table; it removes
/// rows from <see cref="SignalRSequenceEntry"/> only.</para>
///
/// <para>Per-tenant scoping: when <c>tenant</c> is supplied,
/// only rows with that <see cref="SignalRSequenceEntry.TenantId"/>
/// are removed. Without the parameter the purge applies
/// globally (empty-tenant rows + named-tenant rows alike).</para>
/// </summary>
[ApiController]
[Route("api/admin/signalr/retention-purge")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class SignalRRetentionManualPurgeController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const int MaxAdminReasonLength = 512;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SignalRManualPurgeMetrics? _metrics;

    public SignalRRetentionManualPurgeController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        SignalRManualPurgeMetrics? metrics = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _metrics = metrics;
    }

    [HttpPost]
    public async Task<IActionResult> Purge(
        [FromQuery] string? tenant,
        [FromQuery] string? before,
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
        if (string.IsNullOrWhiteSpace(before))
        {
            return BadRequest(new { error = "before-required" });
        }
        if (!DateTime.TryParse(before,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var beforeUtc))
        {
            return BadRequest(new { error = "before-must-be-iso8601" });
        }
        if (beforeUtc >= DateTime.UtcNow.AddMinutes(1))
        {
            return BadRequest(new { error = "before-must-be-in-past" });
        }

        var tenantFilter = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IQueryable<SignalRSequenceEntry> query = db.SignalRSequenceEntries
            .Where(e => e.CreatedAt < beforeUtc);
        if (tenantFilter is not null)
        {
            query = query.Where(e => e.TenantId == tenantFilter);
        }

        var victims = await query.ToListAsync(ct);
        db.SignalRSequenceEntries.RemoveRange(victims);

        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "admin",
            At = DateTime.UtcNow,
            Kind = ReconnectAuditEntry.KindSignalRManualPurge,
            Detail = $"reason={adminReason}|tenant={tenantFilter ?? string.Empty}|before={beforeUtc:O}|purged={victims.Count}",
        });

        await db.SaveChangesAsync(ct);
        _metrics?.Add(tenantFilter ?? string.Empty, victims.Count);

        return Ok(new
        {
            tenant = tenantFilter,
            before = beforeUtc,
            purged = victims.Count,
            adminReason,
        });
    }
}
