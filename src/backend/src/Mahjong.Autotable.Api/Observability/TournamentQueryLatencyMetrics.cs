using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 15 — Bishop. Prometheus histogram collector for
/// tournament-scale query endpoint latency, bucketed by page-size.
/// The W14 endpoints landed configurable page sizes (bracket
/// query, replay listing, spectator audit query) but no latency
/// observability — operators couldn't see whether a 100-row page
/// took 10× longer than a 25-row page until production users
/// reported it.
///
/// <para>The metric is emitted as
/// <c>tournament_query_duration_seconds{endpoint, page_size_bucket}</c>
/// — a Prometheus histogram with the canonical bucket set
/// (5ms, 10ms, 25ms, 50ms, 100ms, 250ms, 500ms, 1s, 2.5s, 5s,
/// 10s, +Inf). The <c>page_size_bucket</c> label collapses the
/// effective page size into one of <c>{small, medium, large}</c>
/// so cardinality stays bounded: small = 1–25, medium = 26–75,
/// large = 76–100 (the hard upper cap across all endpoints).</para>
///
/// <para>The collector is intentionally side-channel — endpoints
/// optionally resolve it from DI and record observations through
/// <see cref="ObserveDuration"/>. A test fixture that wires only
/// the controller still works (the collector is null and the
/// recording is a no-op). See
/// <c>docs/bracket-shape.md §6 "Page-size tuning"</c>.</para>
/// </summary>
public sealed class TournamentQueryLatencyMetrics
{
    public const string MetricName = "tournament_query_duration_seconds";
    public const string EndpointLabel = "endpoint";
    public const string PageSizeBucketLabel = "page_size_bucket";

    public const string BucketSmall = "small";
    public const string BucketMedium = "medium";
    public const string BucketLarge = "large";

    public const int SmallUpperBound = 25;
    public const int MediumUpperBound = 75;

    /// <summary>Canonical bucket boundaries (seconds), modelled
    /// after the default Prometheus histogram bucket set + extra
    /// tails for slow tournament queries.</summary>
    public static readonly double[] BucketBoundsSeconds = new[]
    {
        0.005, 0.010, 0.025, 0.050, 0.100, 0.250, 0.500,
        1.0, 2.5, 5.0, 10.0,
    };

    private readonly ConcurrentDictionary<(string Endpoint, string Bucket), HistogramSeries> _series =
        new();

    /// <summary>Record one observation. <paramref name="endpoint"/>
    /// SHOULD be a stable label like <c>"bracket-records"</c> or
    /// <c>"replay-list"</c>. <paramref name="pageSize"/> is the
    /// effective page size — the collector buckets it into the
    /// canonical small/medium/large label so the wire cardinality
    /// is bounded.</summary>
    public void ObserveDuration(string endpoint, int pageSize, double seconds)
    {
        var ep = string.IsNullOrWhiteSpace(endpoint) ? "unknown" : endpoint;
        var bucket = BucketLabel(pageSize);
        var series = _series.GetOrAdd((ep, bucket), _ => new HistogramSeries(BucketBoundsSeconds));
        series.Observe(seconds);
    }

    /// <summary>Convenience overload — accepts a
    /// <see cref="Stopwatch.GetTimestamp"/> delta in ticks.</summary>
    public void ObserveTimestamp(string endpoint, int pageSize, long startTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        ObserveDuration(endpoint, pageSize, elapsed.TotalSeconds);
    }

    /// <summary>
    /// Resolve the canonical page-size bucket label
    /// (<c>small</c> / <c>medium</c> / <c>large</c>) for the
    /// supplied effective page size. Out-of-range values collapse
    /// to <c>large</c> so the cardinality remains bounded.
    /// </summary>
    public static string BucketLabel(int pageSize)
    {
        if (pageSize <= SmallUpperBound) return BucketSmall;
        if (pageSize <= MediumUpperBound) return BucketMedium;
        return BucketLarge;
    }

    /// <summary>Snapshot of the histogram series — surfaced for
    /// contract tests that hard-assert recorded counts /
    /// percentile band membership.</summary>
    public IReadOnlyDictionary<(string Endpoint, string Bucket), HistogramSnapshot> Snapshot() =>
        _series.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());

    /// <summary>Renders the histogram in Prometheus exposition
    /// format. HELP + TYPE preambles are emitted unconditionally
    /// so the schema is visible even when zero observations have
    /// been recorded.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Tournament-scale query endpoint latency in seconds. Labels: `endpoint` (logical endpoint id), `page_size_bucket` (small ≤25, medium ≤75, large ≤100). See docs/bracket-shape.md §6 \"Page-size tuning\".");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" histogram");

        foreach (var entry in _series)
        {
            var ep = EscapeLabelValue(entry.Key.Endpoint);
            var bucket = EscapeLabelValue(entry.Key.Bucket);
            var snap = entry.Value.Snapshot();
            for (var i = 0; i < BucketBoundsSeconds.Length; i++)
            {
                sb.Append(MetricName)
                  .Append("_bucket{").Append(EndpointLabel).Append("=\"").Append(ep).Append('"')
                  .Append(',').Append(PageSizeBucketLabel).Append("=\"").Append(bucket).Append('"')
                  .Append(",le=\"").Append(BucketBoundsSeconds[i].ToString("0.###", CultureInfo.InvariantCulture))
                  .Append("\"} ")
                  .AppendLine(snap.BucketCounts[i].ToString(CultureInfo.InvariantCulture));
            }
            // +Inf bucket — total count.
            sb.Append(MetricName)
              .Append("_bucket{").Append(EndpointLabel).Append("=\"").Append(ep).Append('"')
              .Append(',').Append(PageSizeBucketLabel).Append("=\"").Append(bucket).Append('"')
              .Append(",le=\"+Inf\"} ")
              .AppendLine(snap.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(MetricName).Append("_sum{").Append(EndpointLabel).Append("=\"").Append(ep).Append('"')
              .Append(',').Append(PageSizeBucketLabel).Append("=\"").Append(bucket).Append("\"} ")
              .AppendLine(snap.Sum.ToString("0.######", CultureInfo.InvariantCulture));
            sb.Append(MetricName).Append("_count{").Append(EndpointLabel).Append("=\"").Append(ep).Append('"')
              .Append(',').Append(PageSizeBucketLabel).Append("=\"").Append(bucket).Append("\"} ")
              .AppendLine(snap.Count.ToString(CultureInfo.InvariantCulture));
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
/// Phase K Wave 15 — Bishop. Lightweight thread-safe histogram
/// series — counts per bucket + running sum. The implementation
/// avoids any external dependency (System.Diagnostics.Metrics
/// histograms exist but their exposition path requires the
/// OpenTelemetry exporter; we want a self-contained Prometheus
/// render so the surface stays consistent with the existing
/// SignalRSequenceMetrics + CommentaryCostMetrics renderers).
/// </summary>
public sealed class HistogramSeries
{
    private readonly double[] _bounds;
    private readonly long[] _bucketCounts;
    private long _count;
    private double _sum;
    private readonly object _gate = new();

    public HistogramSeries(double[] bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        _bounds = bounds;
        _bucketCounts = new long[bounds.Length];
    }

    public void Observe(double value)
    {
        lock (_gate)
        {
            _count++;
            _sum += value;
            for (var i = 0; i < _bounds.Length; i++)
            {
                if (value <= _bounds[i])
                {
                    _bucketCounts[i]++;
                }
            }
        }
    }

    public HistogramSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HistogramSnapshot(_count, _sum, _bucketCounts.ToArray());
        }
    }
}

/// <summary>Read-only snapshot of a <see cref="HistogramSeries"/>.</summary>
public readonly record struct HistogramSnapshot(long Count, double Sum, long[] BucketCounts);
