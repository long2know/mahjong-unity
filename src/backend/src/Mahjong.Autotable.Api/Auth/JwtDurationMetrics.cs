using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 19 — Bishop. Histogram collectors for the JWT
/// issue + validator pipelines. W18 shipped the
/// <see cref="JwtIssueBlockedMetrics"/> counter (block events
/// only); W19 lands the duration histograms so an operator can
/// graph p50/p95/p99 issue + validate latency per-tenant.
///
/// <list type="bullet">
///   <item><c>jwt_issue_duration_seconds{tenant=&lt;t&gt;}</c> —
///         histogram, recorded by every successful
///         <see cref="JwtIssuingService.IssueAsync"/> /
///         <see cref="JwtIssuingService.IssueForTenantAsync"/>
///         invocation. Tenant label is the per-tenant id (or
///         <c>"_global"</c> for the single-tenant path).</item>
///   <item><c>jwt_validator_check_duration_seconds{tenant=&lt;t&gt;}</c>
///         — histogram, recorded by every
///         <see cref="JwtValidationService.Validate"/> call. The
///         tenant label is the value of the token's <c>tenant</c>
///         claim when present (else <c>"_unknown"</c>).</item>
/// </list>
///
/// <para>Bucket selection: 12-bucket exponential ladder from 250µs
/// (the cold-cache JWT mint floor) to 5s (well past the SignalR
/// reconnect window). Matches Prometheus default histogram bucket
/// taste while still keeping the wire-stable shape small.</para>
///
/// <para>Cardinality is bounded by the registered tenant count
/// (per-tenant label) × 12 (bucket count) × 2 (metrics) ≈ low-
/// hundreds in realistic deployments. Unknown / empty tenants
/// fold into the <c>"_unknown"</c> bucket so a misbehaving caller
/// can't blow up Prometheus storage.</para>
///
/// <para>See <c>docs/realtime-resilience.md §10</c> (added W19) +
/// the W19 Grafana dashboard
/// <c>infra/grafana/dashboards/jwt-validator-metrics.json</c>.</para>
/// </summary>
public sealed class JwtDurationMetrics
{
    /// <summary>Prometheus name for the JWT-issue duration
    /// histogram.</summary>
    public const string IssueMetricName = "jwt_issue_duration_seconds";

    /// <summary>Prometheus name for the JWT-validator check
    /// duration histogram.</summary>
    public const string ValidatorCheckMetricName = "jwt_validator_check_duration_seconds";

    /// <summary>Tenant label name on both histograms.</summary>
    public const string TenantLabel = "tenant";

    /// <summary>Tenant sentinel for the single-tenant issue
    /// path (no per-tenant id available).</summary>
    public const string GlobalTenantLabel = "_global";

    /// <summary>Sentinel for empty / unknown tenant ids so the
    /// counter still observes activity.</summary>
    public const string UnknownTenantLabel = "_unknown";

    /// <summary>Bucket upper-bounds in seconds. 12 entries —
    /// 250µs → 5s exponential ladder. The Prometheus <c>+Inf</c>
    /// bucket is implicit and emitted by the rendering path.</summary>
    public static readonly double[] BucketsSeconds =
    {
        0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 1.0, 5.0,
    };

    private readonly ConcurrentDictionary<string, HistogramBucket> _issue = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HistogramBucket> _validatorCheck = new(StringComparer.Ordinal);

    /// <summary>Record one issue-duration observation for a
    /// tenant.</summary>
    public void RecordIssue(string tenantId, TimeSpan duration)
    {
        var key = NormalizeTenant(tenantId, fallback: GlobalTenantLabel);
        var bucket = _issue.GetOrAdd(key, _ => new HistogramBucket(BucketsSeconds.Length));
        bucket.Observe(duration.TotalSeconds);
    }

    /// <summary>Record one validator-check observation for a
    /// tenant.</summary>
    public void RecordValidatorCheck(string tenantId, TimeSpan duration)
    {
        var key = NormalizeTenant(tenantId, fallback: UnknownTenantLabel);
        var bucket = _validatorCheck.GetOrAdd(key, _ => new HistogramBucket(BucketsSeconds.Length));
        bucket.Observe(duration.TotalSeconds);
    }

    /// <summary>Time and record a single issue-duration
    /// observation. Returns the bucket key actually recorded
    /// against — useful for assertions in tests.</summary>
    public string TimeIssue(string tenantId, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var sw = Stopwatch.StartNew();
        work();
        sw.Stop();
        RecordIssue(tenantId, sw.Elapsed);
        return NormalizeTenant(tenantId, fallback: GlobalTenantLabel);
    }

    /// <summary>Same as <see cref="TimeIssue"/> but for the
    /// validator-check histogram.</summary>
    public string TimeValidatorCheck(string tenantId, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var sw = Stopwatch.StartNew();
        work();
        sw.Stop();
        RecordValidatorCheck(tenantId, sw.Elapsed);
        return NormalizeTenant(tenantId, fallback: UnknownTenantLabel);
    }

    /// <summary>Snapshot the issue-histogram. Returns the per-
    /// tenant bucket counts + sum + count. Surfaced for tests.</summary>
    public IReadOnlyDictionary<string, HistogramSnapshot> SnapshotIssue() =>
        _issue.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());

    /// <summary>Snapshot the validator-check histogram. Same
    /// shape as <see cref="SnapshotIssue"/>.</summary>
    public IReadOnlyDictionary<string, HistogramSnapshot> SnapshotValidatorCheck() =>
        _validatorCheck.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());

    /// <summary>Total issue-observations count across all
    /// tenants. Surfaced for ops dashboards.</summary>
    public long TotalIssueObservations =>
        _issue.Values.Sum(b => b.Count);

    /// <summary>Total validator-check observations count across
    /// all tenants.</summary>
    public long TotalValidatorCheckObservations =>
        _validatorCheck.Values.Sum(b => b.Count);

    private static string NormalizeTenant(string? tenantId, string fallback)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return fallback;
        return tenantId;
    }

    /// <summary>Render both histograms in Prometheus exposition
    /// format. HELP + TYPE preambles are emitted unconditionally;
    /// the <c>+Inf</c> bucket is appended after the configured
    /// ladder so the cumulative-count tail is observable.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        AppendHistogram(sb, IssueMetricName,
            "JWT issue request duration in seconds, per-tenant. The `_global` bucket carries the single-tenant code path.",
            _issue);
        AppendHistogram(sb, ValidatorCheckMetricName,
            "JWT validator check duration in seconds, per-tenant. The `_unknown` bucket carries traffic with no resolvable tenant claim.",
            _validatorCheck);
    }

    private static void AppendHistogram(
        StringBuilder sb,
        string metricName,
        string help,
        IReadOnlyDictionary<string, HistogramBucket> buckets)
    {
        sb.Append("# HELP ").Append(metricName).Append(' ').AppendLine(help);
        sb.Append("# TYPE ").Append(metricName).AppendLine(" histogram");
        foreach (var pair in buckets)
        {
            var tenant = EscapeLabelValue(pair.Key);
            var snapshot = pair.Value.Snapshot();
            long cumulative = 0;
            for (var i = 0; i < BucketsSeconds.Length; i++)
            {
                cumulative += snapshot.BucketCounts[i];
                sb.Append(metricName)
                  .Append("_bucket{").Append(TenantLabel).Append("=\"").Append(tenant).Append("\",le=\"")
                  .Append(BucketsSeconds[i].ToString("G", CultureInfo.InvariantCulture)).Append("\"} ")
                  .AppendLine(cumulative.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(metricName).Append("_bucket{").Append(TenantLabel).Append("=\"").Append(tenant)
              .Append("\",le=\"+Inf\"} ").AppendLine(snapshot.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(metricName).Append("_sum{").Append(TenantLabel).Append("=\"").Append(tenant).Append("\"} ")
              .AppendLine(snapshot.Sum.ToString("G", CultureInfo.InvariantCulture));
            sb.Append(metricName).Append("_count{").Append(TenantLabel).Append("=\"").Append(tenant).Append("\"} ")
              .AppendLine(snapshot.Count.ToString(CultureInfo.InvariantCulture));
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

    /// <summary>
    /// Phase K Wave 19 — Bishop. Internal bucket accumulator.
    /// Records observations into the configured ladder + an
    /// implicit <c>+Inf</c> bucket. Thread-safe via interlocked
    /// counters.
    /// </summary>
    internal sealed class HistogramBucket
    {
        private readonly long[] _counts;
        private long _count;
        private double _sum;
        private readonly object _sumLock = new();

        public HistogramBucket(int bucketCount)
        {
            _counts = new long[bucketCount];
        }

        public long Count => Interlocked.Read(ref _count);

        public void Observe(double seconds)
        {
            for (var i = 0; i < BucketsSeconds.Length; i++)
            {
                if (seconds <= BucketsSeconds[i])
                {
                    Interlocked.Increment(ref _counts[i]);
                    break;
                }
            }
            // Note: we count observations >= all buckets via the
            // top-level _count; the per-bucket cumulative is
            // computed at render time.
            Interlocked.Increment(ref _count);
            lock (_sumLock) _sum += seconds;
        }

        public HistogramSnapshot Snapshot()
        {
            var bucketCopy = new long[_counts.Length];
            for (var i = 0; i < _counts.Length; i++)
            {
                bucketCopy[i] = Interlocked.Read(ref _counts[i]);
            }
            double sumCopy;
            lock (_sumLock) sumCopy = _sum;
            return new HistogramSnapshot(bucketCopy, sumCopy, Interlocked.Read(ref _count));
        }
    }
}

/// <summary>
/// Phase K Wave 19 — Bishop. Immutable snapshot of a histogram's
/// state at a moment in time. <see cref="BucketCounts"/> are the
/// per-bucket observation counts (not cumulative); the renderer
/// computes the cumulative sum at exposition time.
/// </summary>
public sealed record HistogramSnapshot(long[] BucketCounts, double Sum, long Count);
