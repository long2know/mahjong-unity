using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 18 — Bishop. Canonical alert-threshold constants
/// for the tournament-query / bracket-query / Swiss-pairing
/// histogram surfaces. The thresholds mirror the YAML rules in
/// <c>Observability/Alerts/tournament-query-duration.yaml</c>;
/// keeping a typed copy in the source tree lets the contract
/// tests pin both sides (YAML + C#) against each other so the
/// alert YAML and the runtime "expected envelope" can't drift
/// silently.
///
/// <para>The numbers are wire-stable — flipping a threshold
/// here must land alongside a corresponding YAML edit + a
/// runbook entry; the W17 contract tests
/// (<c>TournamentAlertsContractTests</c>) hard-assert the YAML
/// thresholds and the W18 contract tests
/// (<c>TournamentAlertsW18ContractTests</c>) hard-assert the
/// C# thresholds match the YAML literals.</para>
/// </summary>
public static class TournamentQueryAlertThresholds
{
    /// <summary>p99 threshold for the
    /// <c>TournamentQueryDurationP99HighPage</c> alert (seconds).
    /// W17 baseline = 500ms.</summary>
    public const double TournamentP99PageSeconds = 0.5;

    /// <summary>p95 threshold for the
    /// <c>TournamentQueryDurationP95HighTicket</c> alert (seconds).
    /// W17 baseline = 250ms.</summary>
    public const double TournamentP95TicketSeconds = 0.25;

    /// <summary>p99 threshold for the W18
    /// <c>BracketQueryDurationP99HighPage</c> alert (seconds).
    /// 1.0s — bracket-store joins are heavier than the parent
    /// tournament_query envelope.</summary>
    public const double BracketP99PageSeconds = 1.0;

    /// <summary>p99 threshold for the W18
    /// <c>SwissPairingDurationP99HighPage</c> alert (seconds).
    /// 1.0s — Swiss pairing is O(R*N^2); a sustained p99 above
    /// 1s suggests the pairing dataset has grown beyond the W14
    /// envelope.</summary>
    public const double SwissPairingP99PageSeconds = 1.0;

    /// <summary>Heartbeat window for the W18
    /// <c>TournamentQueryNoTrafficHeartbeat</c> alert.</summary>
    public static readonly TimeSpan HeartbeatNoTrafficWindow = TimeSpan.FromMinutes(10);

    /// <summary>Rate-window for the PAGE-class alerts.</summary>
    public static readonly TimeSpan PageRateWindow = TimeSpan.FromMinutes(5);

    /// <summary>Rate-window for the TICKET-class p95 alert.</summary>
    public static readonly TimeSpan TicketRateWindow = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Phase K Wave 18 — Bishop. Prometheus histogram for the
/// bracket-query endpoint latency. Sibling of
/// <see cref="TournamentQueryLatencyMetrics"/> — separate metric
/// name (<c>bracket_query_duration_seconds</c>) so the heavier
/// bracket-store join path can be alerted independently of the
/// parent tournament_query envelope. The W18
/// <c>BracketQueryDurationP99HighPage</c> alert wraps this
/// histogram.
///
/// <para>Labels: <c>endpoint</c> (logical endpoint id, stable
/// label set bounded by the call-site) +
/// <c>page_size_bucket</c> (small/medium/large, mirroring the
/// parent envelope). Bucket boundaries match the parent so the
/// dashboards can render side-by-side panels.</para>
/// </summary>
public sealed class BracketQueryLatencyMetrics
{
    public const string MetricName = "bracket_query_duration_seconds";
    public const string EndpointLabel = "endpoint";
    public const string PageSizeBucketLabel = "page_size_bucket";

    /// <summary>Canonical bucket boundaries (seconds). Identical
    /// to the parent <see cref="TournamentQueryLatencyMetrics"/>
    /// so the two histograms can be rendered side-by-side on
    /// the dashboard without per-panel rescaling.</summary>
    public static readonly double[] BucketBoundsSeconds = new[]
    {
        0.005, 0.010, 0.025, 0.050, 0.100, 0.250, 0.500,
        1.0, 2.5, 5.0, 10.0,
    };

    private readonly ConcurrentDictionary<(string Endpoint, string Bucket), HistogramSeries> _series =
        new();

    /// <summary>Record one observation for a bracket-store
    /// query.</summary>
    public void ObserveDuration(string endpoint, int pageSize, double seconds)
    {
        var ep = string.IsNullOrWhiteSpace(endpoint) ? "unknown" : endpoint;
        var bucket = TournamentQueryLatencyMetrics.BucketLabel(pageSize);
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

    /// <summary>Snapshot of the histogram series — surfaced for
    /// contract tests.</summary>
    public IReadOnlyDictionary<(string Endpoint, string Bucket), HistogramSnapshot> Snapshot() =>
        _series.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());

    /// <summary>Render the histogram in Prometheus exposition
    /// format.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Bracket-store query latency in seconds. Sibling of tournament_query_duration_seconds. See docs/tournament-query-duration-runbook.md#bracket-p99-page.");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" histogram");
        foreach (var entry in _series)
        {
            var ep = entry.Key.Endpoint;
            var bucket = entry.Key.Bucket;
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
}

/// <summary>
/// Phase K Wave 18 — Bishop. Prometheus histogram for the
/// per-stage Swiss-pairing computation latency. Wraps the
/// <c>swiss_pairing_duration_seconds</c> metric with a single
/// <c>stage</c> label distinguishing
/// <c>round-robin</c>/<c>swiss</c>/<c>single-elim-cutover</c>.
/// The W18 <c>SwissPairingDurationP99HighPage</c> alert wraps
/// this histogram.
/// </summary>
public sealed class SwissPairingLatencyMetrics
{
    public const string MetricName = "swiss_pairing_duration_seconds";
    public const string StageLabel = "stage";

    public const string StageRoundRobin = "round-robin";
    public const string StageSwiss = "swiss";
    public const string StageSingleElimCutover = "single-elim-cutover";

    /// <summary>Allowed stage labels. The collector folds
    /// unknown stages into <c>"unknown"</c> so the wire
    /// cardinality stays bounded.</summary>
    public static readonly IReadOnlyCollection<string> AllowedStages = new[]
    {
        StageRoundRobin,
        StageSwiss,
        StageSingleElimCutover,
    };

    public static readonly double[] BucketBoundsSeconds = new[]
    {
        0.010, 0.025, 0.050, 0.100, 0.250, 0.500,
        1.0, 2.5, 5.0, 10.0,
    };

    private readonly ConcurrentDictionary<string, HistogramSeries> _series =
        new(StringComparer.Ordinal);

    public void ObserveDuration(string stage, double seconds)
    {
        var s = ResolveStage(stage);
        var series = _series.GetOrAdd(s, _ => new HistogramSeries(BucketBoundsSeconds));
        series.Observe(seconds);
    }

    public void ObserveTimestamp(string stage, long startTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        ObserveDuration(stage, elapsed.TotalSeconds);
    }

    /// <summary>Resolve a stage label to a canonical value — out
    /// of range collapses to <c>"unknown"</c>.</summary>
    public static string ResolveStage(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return "unknown";
        foreach (var allowed in AllowedStages)
        {
            if (string.Equals(stage, allowed, StringComparison.Ordinal)) return allowed;
        }
        return "unknown";
    }

    public IReadOnlyDictionary<string, HistogramSnapshot> Snapshot() =>
        _series.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());

    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Per-stage Swiss-pairing computation latency in seconds. Wraps the O(R*N^2) pairing loop. See docs/tournament-query-duration-runbook.md#swiss-pairing-p99.");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" histogram");
        foreach (var entry in _series)
        {
            var stage = entry.Key;
            var snap = entry.Value.Snapshot();
            for (var i = 0; i < BucketBoundsSeconds.Length; i++)
            {
                sb.Append(MetricName)
                  .Append("_bucket{").Append(StageLabel).Append("=\"").Append(stage).Append('"')
                  .Append(",le=\"").Append(BucketBoundsSeconds[i].ToString("0.###", CultureInfo.InvariantCulture))
                  .Append("\"} ")
                  .AppendLine(snap.BucketCounts[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(MetricName)
              .Append("_bucket{").Append(StageLabel).Append("=\"").Append(stage).Append('"')
              .Append(",le=\"+Inf\"} ")
              .AppendLine(snap.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(MetricName).Append("_sum{").Append(StageLabel).Append("=\"").Append(stage).Append("\"} ")
              .AppendLine(snap.Sum.ToString("0.######", CultureInfo.InvariantCulture));
            sb.Append(MetricName).Append("_count{").Append(StageLabel).Append("=\"").Append(stage).Append("\"} ")
              .AppendLine(snap.Count.ToString(CultureInfo.InvariantCulture));
        }
    }
}
