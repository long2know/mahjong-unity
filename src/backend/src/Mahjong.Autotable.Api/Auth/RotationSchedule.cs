using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 21 — Bishop. Per-tenant scheduled JWKS rotation
/// policy. The W15 → W20 surfaces deal with the rotation *state*
/// (active vs previous kid, overlap window, in-flight rotation
/// instants); W21 adds the scheduled *cadence* — an operator
/// declares "rotate this tenant on a 5-of-7 cron schedule" and
/// the <c>RotationScheduledExecutorService</c> picks the row up
/// at the next tick, runs the rotation, and stamps the audit row.
///
/// <para>The natural key is <see cref="TenantId"/> — a tenant has
/// at most one scheduled cadence. Reschedule by re-POSTing the
/// schedule endpoint; remove by DELETEing the row.</para>
///
/// <para>Wire shape:
/// <code>
/// POST /api/admin/per-tenant-jwks-rotation-policies/{id}/schedule
/// { "cronExpression": "0 0 3 * * *",
///   "enabled": true,
///   "notes": "nightly 03:00 UTC" }
/// </code></para>
///
/// <para>The <see cref="CronExpression"/> column is a free-form
/// 5- or 6-field Quartz-flavour cron string. The W21 executor
/// uses a small in-house tick matcher (see
/// <c>SimpleCronMatcher</c>) which is sufficient for the
/// canonical patterns operators use (hourly / daily / weekly
/// rotations). Length-bounded at 64 chars.</para>
/// </summary>
public class RotationScheduleEntity
{
    /// <summary>Maximum length of the <see cref="CronExpression"/>
    /// column. 64 — comfortably above the canonical 5- or 6-field
    /// cron string lengths but bounded enough to keep the table
    /// index narrow.</summary>
    public const int MaxCronLength = 64;

    /// <summary>Maximum length of the <see cref="Notes"/> column.
    /// 512 — leaves the row narrow without truncating
    /// operator-supplied context strings.</summary>
    public const int MaxNotesLength = 512;

    /// <summary>Owning tenant id. Natural key — a tenant has at
    /// most one scheduled cadence. Matches the
    /// <see cref="PerTenantJwksRotationPolicy.TenantId"/> shape
    /// (128 chars).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Cron expression that drives the rotation. The
    /// W21 executor evaluates "is now a tick boundary for this
    /// cron?" at every poll; the cadence floor is therefore the
    /// executor poll interval (default 60s).</summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>Master toggle. When false the executor skips
    /// the row entirely so an operator can pause a schedule
    /// without losing the cadence.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Free-form operator notes. Surfaced on the
    /// listing endpoint so a dashboard can render the cadence
    /// alongside its justification.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>UTC instant the row was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the row was last updated. Stamped
    /// by the admin POST.</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the executor last ran the schedule.
    /// Stamped at the end of every successful tick. The poller
    /// uses this as the "did we just run this row?" idempotency
    /// guard — runs that fall within the same cron-minute as
    /// <see cref="LastRunAtUtc"/> short-circuit.</summary>
    public DateTime? LastRunAtUtc { get; set; }
}

/// <summary>
/// Phase K Wave 21 — Bishop. Prometheus counter tracking
/// scheduled JWT rotation executions.
///
/// <para>Wire shape:
/// <code>
/// # HELP jwt_scheduled_rotation_total Total scheduled JWKS rotations executed by the W21 RotationScheduledExecutorService.
/// # TYPE jwt_scheduled_rotation_total counter
/// jwt_scheduled_rotation_total{tenant="tenant-abc",status="success"} 42
/// jwt_scheduled_rotation_total{tenant="tenant-abc",status="error"}   3
/// jwt_scheduled_rotation_total{tenant="tenant-xyz",status="skipped"} 1
/// </code></para>
///
/// <para>The collector is intentionally side-channel — the
/// executor optionally resolves it from DI; a test fixture that
/// wires only the executor still works (the counter is null and
/// the recording is a no-op).</para>
/// </summary>
public sealed class JwtScheduledRotationMetrics
{
    public const string MetricName = "jwt_scheduled_rotation_total";
    public const string TenantLabel = "tenant";
    public const string StatusLabel = "status";

    public const string StatusSuccess = "success";
    public const string StatusError = "error";
    public const string StatusSkipped = "skipped";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Tenant, string Status), long> _counters =
        new();

    public void Record(string tenant, string status)
    {
        var key = (tenant ?? string.Empty, status ?? string.Empty);
        _counters.AddOrUpdate(key, 1, (_, prev) => prev + 1);
    }

    public long Get(string tenant, string status) =>
        _counters.TryGetValue((tenant, status), out var v) ? v : 0;

    public IReadOnlyDictionary<(string Tenant, string Status), long> Snapshot() =>
        new Dictionary<(string, string), long>(_counters);

    public void AppendPrometheus(System.Text.StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Total scheduled JWKS rotations executed by the W21 RotationScheduledExecutorService. Labelled by `tenant` and `status` (`success`, `error`, `skipped`).");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" counter");
        foreach (var kv in _counters)
        {
            sb.Append(MetricName)
              .Append('{').Append(TenantLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key.Tenant)).Append("\",")
              .Append(StatusLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key.Status)).Append("\"} ")
              .AppendLine(kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string EscapeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.IndexOfAny(['\\', '"', '\n']) < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length + 8);
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
/// Phase K Wave 21 — Bishop. Small in-house cron tick matcher.
/// Supports the canonical patterns operators use:
/// <list type="bullet">
///   <item><c>*</c> — every value.</item>
///   <item><c>5</c> — exact value.</item>
///   <item><c>0,15,30,45</c> — explicit list.</item>
///   <item><c>*/15</c> — step (every 15 from 0).</item>
///   <item><c>1-5</c> — inclusive range.</item>
/// </list>
/// Fields (5 or 6, 6-field form treats the leading column as
/// seconds and ignored for the per-minute poll): minute,
/// hour, day-of-month, month, day-of-week.
/// </summary>
public static class SimpleCronMatcher
{
    public static bool MatchesNow(string cronExpression, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(cronExpression)) return false;
        var parts = cronExpression.Trim().Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (5 or 6)) return false;
        // 6-field form: leading column is seconds; we evaluate
        // per-minute so any seconds value passes.
        var offset = parts.Length == 6 ? 1 : 0;
        return MatchField(parts[offset + 0], nowUtc.Minute, 0, 59)
            && MatchField(parts[offset + 1], nowUtc.Hour, 0, 23)
            && MatchField(parts[offset + 2], nowUtc.Day, 1, 31)
            && MatchField(parts[offset + 3], nowUtc.Month, 1, 12)
            && MatchField(parts[offset + 4], (int)nowUtc.DayOfWeek, 0, 6);
    }

    internal static bool MatchField(string field, int value, int min, int max)
    {
        if (string.IsNullOrEmpty(field)) return false;
        // Comma-list.
        if (field.Contains(','))
        {
            foreach (var token in field.Split(','))
            {
                if (MatchField(token, value, min, max)) return true;
            }
            return false;
        }
        // Step: */N or A/N or A-B/N.
        var stepIdx = field.IndexOf('/');
        if (stepIdx >= 0)
        {
            var head = field[..stepIdx];
            var stepStr = field[(stepIdx + 1)..];
            if (!int.TryParse(stepStr, out var step) || step <= 0) return false;
            int rangeStart, rangeEnd;
            if (head == "*")
            {
                rangeStart = min; rangeEnd = max;
            }
            else if (head.Contains('-'))
            {
                var dashIdx = head.IndexOf('-');
                if (!int.TryParse(head[..dashIdx], out rangeStart)
                    || !int.TryParse(head[(dashIdx + 1)..], out rangeEnd)) return false;
            }
            else if (int.TryParse(head, out var startOnly))
            {
                rangeStart = startOnly; rangeEnd = max;
            }
            else
            {
                return false;
            }
            if (value < rangeStart || value > rangeEnd) return false;
            return (value - rangeStart) % step == 0;
        }
        // Range: A-B.
        if (field.Contains('-'))
        {
            var dashIdx = field.IndexOf('-');
            if (!int.TryParse(field[..dashIdx], out var a)
                || !int.TryParse(field[(dashIdx + 1)..], out var b)) return false;
            return value >= a && value <= b;
        }
        if (field == "*") return true;
        return int.TryParse(field, out var exact) && exact == value;
    }
}

/// <summary>
/// Phase K Wave 21 — Bishop. Admin-gated endpoint that creates
/// or replaces the scheduled rotation cadence for a tenant.
/// Surface:
/// <c>POST /api/admin/per-tenant-jwks-rotation-policies/{id}/schedule</c>
/// where <c>{id}</c> is the tenant id.
///
/// <para>Auth: 401 (no session) → 403 (non-admin) → 400 (input
/// validation) → 200 (success). Mandatory <c>X-Admin-Reason</c>
/// header.</para>
/// </summary>
[ApiController]
[Route("api/admin/per-tenant-jwks-rotation-policies/{tenantId}/schedule")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class RotationScheduleAdminController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const int MaxAdminReasonLength = 512;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;

    public RotationScheduleAdminController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public sealed class ScheduleRequest
    {
        public string? CronExpression { get; set; }
        public bool Enabled { get; set; } = true;
        public string? Notes { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Schedule(
        [FromRoute] string tenantId,
        [FromBody] ScheduleRequest? request,
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
        var reason = reasonValues.ToString();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        if (reason.Length > MaxAdminReasonLength)
        {
            return BadRequest(new { error = "admin-reason-too-long", maximum = MaxAdminReasonLength });
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "tenant-id-required" });
        }
        if (request is null)
        {
            return BadRequest(new { error = "body-required" });
        }
        if (string.IsNullOrWhiteSpace(request.CronExpression))
        {
            return BadRequest(new { error = "cron-expression-required" });
        }
        if (request.CronExpression.Length > RotationScheduleEntity.MaxCronLength)
        {
            return BadRequest(new { error = "cron-expression-too-long", maximum = RotationScheduleEntity.MaxCronLength });
        }
        if (!SimpleCronMatcher.MatchesNow(request.CronExpression, DateTime.UtcNow)
            && !LooksLikeValidCron(request.CronExpression))
        {
            // Validation: at minimum the cron must parse. We
            // probe the matcher with current time; a malformed
            // expression returns false-AND-fails the
            // LooksLikeValidCron heuristic.
            return BadRequest(new { error = "cron-expression-invalid" });
        }
        if (!string.IsNullOrEmpty(request.Notes)
            && request.Notes.Length > RotationScheduleEntity.MaxNotesLength)
        {
            return BadRequest(new { error = "notes-too-long", maximum = RotationScheduleEntity.MaxNotesLength });
        }

        var now = DateTime.UtcNow;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.RotationSchedules.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (existing is null)
        {
            db.RotationSchedules.Add(new RotationScheduleEntity
            {
                TenantId = tenantId,
                CronExpression = request.CronExpression,
                Enabled = request.Enabled,
                Notes = request.Notes ?? string.Empty,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            existing.CronExpression = request.CronExpression;
            existing.Enabled = request.Enabled;
            existing.Notes = request.Notes ?? string.Empty;
            existing.UpdatedAtUtc = now;
        }

        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "admin",
            At = now,
            Kind = ReconnectAuditEntry.KindAuthJwksRotationScheduled,
            Detail = $"reason={reason}|tenantId={tenantId}|cron={request.CronExpression}",
        });

        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            tenantId,
            cronExpression = request.CronExpression,
            enabled = request.Enabled,
            notes = request.Notes ?? string.Empty,
            created = existing is null,
        });
    }

    /// <summary>
    /// Best-effort validation — checks for 5 or 6 space-separated
    /// fields. The full parse runs in <see cref="SimpleCronMatcher.MatchField"/>;
    /// invalid fields will simply never match a real timestamp,
    /// surfaced as a never-fires schedule (which the executor
    /// treats as a no-op).
    /// </summary>
    internal static bool LooksLikeValidCron(string cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression)) return false;
        var parts = cronExpression.Trim().Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length is 5 or 6;
    }
}

/// <summary>
/// Phase K Wave 21 — Bishop. Background service that polls the
/// <see cref="RotationScheduleEntity"/> table and runs the
/// scheduled rotation for any tenant whose cadence ticks at the
/// current minute. Emits the
/// <c>jwt_scheduled_rotation_total{tenant,status}</c> counter
/// on every evaluation.
///
/// <para>Tick cadence: 60s default (the cron resolution floor).
/// Each tick the executor:
/// <list type="number">
///   <item>Loads every enabled <see cref="RotationScheduleEntity"/>
///         row.</item>
///   <item>For each row, evaluates the cron against the
///         current UTC instant. Non-match → record
///         <c>status="skipped"</c>.</item>
///   <item>On match, looks up the matching
///         <see cref="PerTenantJwksRotationPolicy"/>, advances the
///         rotation window, stamps the audit row, increments the
///         counter with <c>status="success"</c>. Errors map to
///         <c>status="error"</c>.</item>
/// </list></para>
/// </summary>
public sealed class RotationScheduledExecutorService : Microsoft.Extensions.Hosting.BackgroundService
{
    public const int DefaultTickIntervalSeconds = 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JwtScheduledRotationMetrics? _metrics;
    private readonly ILogger<RotationScheduledExecutorService> _logger;
    private readonly Func<DateTime> _clock;

    public RotationScheduledExecutorService(
        IServiceScopeFactory scopeFactory,
        ILogger<RotationScheduledExecutorService> logger,
        JwtScheduledRotationMetrics? metrics = null,
        Func<DateTime>? clock = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(DefaultTickIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RotationScheduledExecutorService tick failed (non-fatal).");
            }
        }
    }

    /// <summary>Single-tick entry-point. Internal so tests can
    /// drive evaluations deterministically against a mocked
    /// clock.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = _clock();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schedules = await db.RotationSchedules
            .Where(s => s.Enabled)
            .ToListAsync(ct);

        int executed = 0;
        foreach (var schedule in schedules)
        {
            if (!SimpleCronMatcher.MatchesNow(schedule.CronExpression, now))
            {
                _metrics?.Record(schedule.TenantId, JwtScheduledRotationMetrics.StatusSkipped);
                continue;
            }
            // Idempotency: if the schedule already ran this
            // minute, skip.
            if (schedule.LastRunAtUtc is { } last
                && last.Year == now.Year
                && last.Month == now.Month
                && last.Day == now.Day
                && last.Hour == now.Hour
                && last.Minute == now.Minute)
            {
                _metrics?.Record(schedule.TenantId, JwtScheduledRotationMetrics.StatusSkipped);
                continue;
            }
            try
            {
                await ExecuteScheduledRotationAsync(db, schedule, now, ct);
                schedule.LastRunAtUtc = now;
                executed++;
                _metrics?.Record(schedule.TenantId, JwtScheduledRotationMetrics.StatusSuccess);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RotationScheduledExecutorService failed for tenant {Tenant}.",
                    schedule.TenantId);
                _metrics?.Record(schedule.TenantId, JwtScheduledRotationMetrics.StatusError);
            }
        }
        if (executed > 0) await db.SaveChangesAsync(ct);
        return executed;
    }

    private async Task ExecuteScheduledRotationAsync(
        AppDbContext db,
        RotationScheduleEntity schedule,
        DateTime now,
        CancellationToken ct)
    {
        var policy = await db.PerTenantJwksRotationPolicies
            .FirstOrDefaultAsync(p => p.TenantId == schedule.TenantId, ct);
        if (policy is null)
        {
            // No policy row yet — surface as an error so the
            // operator can backfill.
            throw new InvalidOperationException(
                $"No PerTenantJwksRotationPolicy row for tenant '{schedule.TenantId}'");
        }
        // Advance the rotation window. Mirrors the W20
        // bulk-enable semantics: re-stamp the rotation window
        // so the validator picks up the schedule.
        var nowOffset = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc), TimeSpan.Zero);
        policy.RotationStartUtc = nowOffset;
        policy.RotationCompleteUtc = nowOffset.AddDays(30);
        policy.UpdatedAt = now;

        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "system",
            At = now,
            Kind = ReconnectAuditEntry.KindAuthJwksRotationScheduledExecuted,
            Detail = $"tenantId={schedule.TenantId}|cron={schedule.CronExpression}|status=success",
        });
    }
}
