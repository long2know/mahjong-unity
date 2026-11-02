using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.Extensions.Hosting;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 20 — Bishop. Per-tenant Prometheus counter
/// tracking auto-expired replay rows. The
/// <see cref="ReplayStoreExpiryHandler"/> hosted service ticks
/// the
/// <see cref="IReplayStore.SweepWithPerTenantBreakdownAsync"/>
/// surface every <see cref="ReplayStoreExpiryHandler.DefaultTickIntervalMinutes"/>
/// minutes and increments this counter per tenant. Rows
/// without a tenant id are bucketed under the wire-name
/// <c>"_unknown"</c> (matching the W19 integrity-audit
/// per-tenant bucket naming so dashboards can join the two
/// surfaces).
///
/// <para>Wire shape:
/// <code>
/// # HELP replay_expired_total Total replay rows auto-expired by the W20 ReplayStoreExpiryHandler.
/// # TYPE replay_expired_total counter
/// replay_expired_total{tenant="tenant-abc"} 42
/// replay_expired_total{tenant="_unknown"}   7
/// </code></para>
///
/// <para>The collector is intentionally side-channel — the
/// handler optionally resolves it from DI and records
/// observations through <see cref="Add"/>. A test fixture that
/// wires only the store still works (the collector is null and
/// the recording is a no-op).</para>
/// </summary>
public sealed class ReplayExpiryMetrics
{
    public const string MetricName = "replay_expired_total";
    public const string TenantLabel = "tenant";

    /// <summary>Wire-name for the empty-tenant bucket. Matches
    /// the W19 integrity-audit empty-tenant rendering so
    /// dashboards can join both surfaces by tenant name.</summary>
    public const string UnknownTenantBucket = "_unknown";

    private readonly ConcurrentDictionary<string, long> _counters =
        new(StringComparer.Ordinal);

    /// <summary>Increment the counter for the supplied tenant
    /// id by <paramref name="delta"/>. Empty / null tenant ids
    /// land in the <see cref="UnknownTenantBucket"/> bucket.
    /// Delta must be non-negative.</summary>
    public void Add(string? tenantId, long delta)
    {
        if (delta <= 0) return;
        var key = string.IsNullOrEmpty(tenantId) ? UnknownTenantBucket : tenantId;
        _counters.AddOrUpdate(key, delta, (_, prev) => prev + delta);
    }

    /// <summary>Read the current counter value for the supplied
    /// tenant (or <see cref="UnknownTenantBucket"/>). Tests
    /// assert against this snapshot.</summary>
    public long Get(string tenantId)
    {
        var key = string.IsNullOrEmpty(tenantId) ? UnknownTenantBucket : tenantId;
        return _counters.TryGetValue(key, out var v) ? v : 0;
    }

    /// <summary>Snapshot the full counter map. Returned as a
    /// new dictionary so the caller can mutate without affecting
    /// the live collector.</summary>
    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_counters, StringComparer.Ordinal);

    /// <summary>Render the Prometheus exposition for this
    /// collector. Bishop's MetricsEndpoint composes this into
    /// the global /metrics output.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Total replay rows auto-expired by the W20 ReplayStoreExpiryHandler. Bucketed by tenant.");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" counter");
        // Stable rendering order — tests assert on byte-for-
        // byte equality so we sort by tenant label.
        var keys = _counters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var key in keys)
        {
            var value = _counters.TryGetValue(key, out var v) ? v : 0;
            sb.Append(MetricName)
              .Append('{').Append(TenantLabel).Append("=\"").Append(EscapeLabel(key)).Append("\"} ")
              .AppendLine(value.ToString(CultureInfo.InvariantCulture));
        }
        // Always emit a zeroed _unknown envelope so dashboards
        // never observe a missing series.
        if (!_counters.ContainsKey(UnknownTenantBucket))
        {
            sb.Append(MetricName)
              .Append('{').Append(TenantLabel).Append("=\"").Append(UnknownTenantBucket).Append("\"} 0")
              .AppendLine();
        }
    }

    private static string EscapeLabel(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>
/// Phase K Wave 20 — Bishop. Auto-expiry background service.
/// W12 / W15 / W16 shipped the
/// <see cref="ReplayStoreRetentionSweep"/> which deletes rows
/// past the retention window — sufficient for cleanup but
/// silent on the per-tenant cardinality. W20 lands a parallel
/// "expiry handler" that consults the W16 per-tenant policy
/// store, deletes rows, and emits the per-tenant
/// <see cref="ReplayExpiryMetrics"/> counter so operator
/// dashboards can render a tenant-aware eviction trend.
///
/// <para>The two services coexist — they share the same
/// underlying <see cref="IReplayStore"/> and the delete
/// operations are idempotent, so a row evicted by the W16
/// sweep is a no-op on the W20 handler's tick (and vice
/// versa). The W20 handler runs at a configurable cadence
/// (default <see cref="DefaultTickIntervalMinutes"/> minutes)
/// independently from the W15 sweep.</para>
///
/// <para>Audit trail: when the handler evicts any rows in a
/// tick, it writes a single
/// <see cref="ReconnectAuditEntry.KindReplayAutoExpiry"/> row
/// summarising the eviction (removed=N|tenants=N|cutoff=...).
/// Best-effort — an audit write failure does not block the
/// next tick.</para>
///
/// <para>See <c>docs/replay-by-id.md §4.2 "Auto-expiry
/// handler"</c> (added W20).</para>
/// </summary>
public sealed class ReplayStoreExpiryHandler : BackgroundService
{
    /// <summary>Default tick cadence in minutes when
    /// <see cref="ReplayOptions.AutoExpiryTickIntervalMinutes"/>
    /// is unset / non-positive. 15 minutes is the W20 baseline —
    /// tight enough that a runtime retention dial takes effect
    /// in well under an hour, loose enough that the per-tenant
    /// pre-count round-trips don't dominate the database
    /// budget.</summary>
    public const int DefaultTickIntervalMinutes = 15;

    private readonly IReplayStore _store;
    private readonly ReplayOptions _options;
    private readonly ILogger<ReplayStoreExpiryHandler> _logger;
    private readonly IReplayRetentionPolicyStore? _policyStore;
    private readonly ReplayExpiryMetrics? _metrics;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<DateTime> _clock;

    public ReplayStoreExpiryHandler(
        IReplayStore store,
        ReplayOptions options,
        ILogger<ReplayStoreExpiryHandler> logger,
        IReplayRetentionPolicyStore? policyStore = null,
        ReplayExpiryMetrics? metrics = null,
        IServiceScopeFactory? scopeFactory = null,
        Func<DateTime>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policyStore = policyStore;
        _metrics = metrics;
        _scopeFactory = scopeFactory;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _options.AutoExpiryTickIntervalMinutes > 0
            ? _options.AutoExpiryTickIntervalMinutes
            : DefaultTickIntervalMinutes;
        var interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _logger.LogInformation(
            "ReplayStoreExpiryHandler started (interval={Minutes}m, retention={Days}d, perTenant={PerTenant}).",
            interval.TotalMinutes,
            _options.RetentionDays,
            _policyStore is not null);

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
                _logger.LogWarning(ex,
                    "ReplayStoreExpiryHandler tick failed (non-fatal); next tick in {Minutes}m.",
                    interval.TotalMinutes);
            }
        }

        _logger.LogInformation("ReplayStoreExpiryHandler stopped.");
    }

    /// <summary>Single-tick entry-point exposed so tests can
    /// drive evictions deterministically against a mocked
    /// clock.</summary>
    internal async Task<IReadOnlyDictionary<string, int>> RunOnceAsync(CancellationToken ct)
    {
        var retention = _options.RetentionDays > 0
            ? _options.RetentionDays
            : ReplayOptions.DefaultRetentionDays;
        var utcNow = _clock();
        if (_policyStore is null)
        {
            // No policy store wired -- fall back to a single
            // global sweep and bucket every eviction under the
            // empty-tenant key (rendered as "_unknown" by the
            // metric collector).
            var globalRemoved = await _store.SweepByCompletedAtAsync(retention, utcNow, ct).ConfigureAwait(false);
            if (globalRemoved <= 0)
            {
                return new Dictionary<string, int>();
            }
            _metrics?.Add(string.Empty, globalRemoved);
            _logger.LogInformation(
                "ReplayStoreExpiryHandler removed {Count} record(s) (no per-tenant policy store wired).",
                globalRemoved);
            await WriteAuditAsync(globalRemoved, 1, utcNow.AddDays(-retention), ct).ConfigureAwait(false);
            return new Dictionary<string, int> { [string.Empty] = globalRemoved };
        }
        var breakdown = await _store.SweepWithPerTenantBreakdownAsync(
            _policyStore, retention, utcNow, ct).ConfigureAwait(false);

        var totalRemoved = 0;
        foreach (var (tenant, count) in breakdown)
        {
            totalRemoved += count;
            _metrics?.Add(tenant, count);
        }

        if (totalRemoved > 0)
        {
            _logger.LogInformation(
                "ReplayStoreExpiryHandler removed {Count} record(s) across {Tenants} tenant(s).",
                totalRemoved, breakdown.Count);
            await WriteAuditAsync(totalRemoved, breakdown.Count, utcNow.AddDays(-retention), ct).ConfigureAwait(false);
        }
        return breakdown;
    }

    private async Task WriteAuditAsync(int removed, int tenantCount, DateTime cutoff, CancellationToken ct)
    {
        if (_scopeFactory is null) return;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = "system",
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindReplayAutoExpiry,
                Detail = $"removed={removed}|tenants={tenantCount}|cutoff={cutoff:O}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "ReplayStoreExpiryHandler audit write failed (removed={Removed}).", removed);
        }
    }
}
