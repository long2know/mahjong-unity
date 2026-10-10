using System.Text;
using Mahjong.Autotable.Api.Observability;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Hard-asserted contract for the
/// <see cref="TournamentQueryLatencyMetrics"/> Prometheus
/// histogram collector.
///
/// <list type="number">
///   <item>BucketLabel: 1 → small.</item>
///   <item>BucketLabel: 25 → small (upper bound inclusive).</item>
///   <item>BucketLabel: 26 → medium.</item>
///   <item>BucketLabel: 75 → medium (upper bound inclusive).</item>
///   <item>BucketLabel: 76 → large.</item>
///   <item>BucketLabel: 100 → large.</item>
///   <item>BucketLabel: 1000 → large (clamped).</item>
///   <item>ObserveDuration increments the count for the labelled series.</item>
///   <item>ObserveDuration records into the canonical histogram bucket.</item>
///   <item>AppendPrometheus emits the canonical metric name.</item>
///   <item>AppendPrometheus emits HELP + TYPE preambles.</item>
///   <item>AppendPrometheus emits _sum, _count, _bucket series.</item>
///   <item>Snapshot returns recorded counts.</item>
///   <item>Empty / null endpoint label collapses to "unknown".</item>
/// </list>
/// </summary>
public sealed class TournamentQueryLatencyMetricsTests
{
    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketLabel_1_Small()
    {
        Assert.Equal(TournamentQueryLatencyMetrics.BucketSmall,
            TournamentQueryLatencyMetrics.BucketLabel(1));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketLabel_25_Small()
    {
        Assert.Equal(TournamentQueryLatencyMetrics.BucketSmall,
            TournamentQueryLatencyMetrics.BucketLabel(25));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketLabel_26_Medium()
    {
        Assert.Equal(TournamentQueryLatencyMetrics.BucketMedium,
            TournamentQueryLatencyMetrics.BucketLabel(26));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketLabel_75_Medium()
    {
        Assert.Equal(TournamentQueryLatencyMetrics.BucketMedium,
            TournamentQueryLatencyMetrics.BucketLabel(75));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketLabel_76_Large()
    {
        Assert.Equal(TournamentQueryLatencyMetrics.BucketLarge,
            TournamentQueryLatencyMetrics.BucketLabel(76));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketLabel_100_Large()
    {
        Assert.Equal(TournamentQueryLatencyMetrics.BucketLarge,
            TournamentQueryLatencyMetrics.BucketLabel(100));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketLabel_OutOfRange_ClampsToLarge()
    {
        Assert.Equal(TournamentQueryLatencyMetrics.BucketLarge,
            TournamentQueryLatencyMetrics.BucketLabel(1000));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void ObserveDuration_IncrementsCount()
    {
        var m = new TournamentQueryLatencyMetrics();
        m.ObserveDuration("bracket-records", 25, 0.012);
        m.ObserveDuration("bracket-records", 25, 0.045);
        var snap = m.Snapshot();
        Assert.True(snap.TryGetValue(("bracket-records", TournamentQueryLatencyMetrics.BucketSmall),
            out var series));
        Assert.Equal(2, series.Count);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void ObserveDuration_RecordsCanonicalBucket()
    {
        var m = new TournamentQueryLatencyMetrics();
        m.ObserveDuration("bracket-records", 25, 0.012);
        var snap = m.Snapshot();
        var series = snap[("bracket-records", TournamentQueryLatencyMetrics.BucketSmall)];
        // 12ms ≤ 25ms, so the 0.025 bucket and beyond all increment.
        var idx25 = Array.IndexOf(TournamentQueryLatencyMetrics.BucketBoundsSeconds, 0.025);
        Assert.True(series.BucketCounts[idx25] >= 1);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsMetricName()
    {
        var m = new TournamentQueryLatencyMetrics();
        m.ObserveDuration("bracket-records", 25, 0.012);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("tournament_query_duration_seconds", text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndType()
    {
        var m = new TournamentQueryLatencyMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("# HELP tournament_query_duration_seconds", text);
        Assert.Contains("# TYPE tournament_query_duration_seconds histogram", text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsSumCountBucket()
    {
        var m = new TournamentQueryLatencyMetrics();
        m.ObserveDuration("bracket-records", 25, 0.030);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("tournament_query_duration_seconds_bucket{", text);
        Assert.Contains("tournament_query_duration_seconds_sum{", text);
        Assert.Contains("tournament_query_duration_seconds_count{", text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsInfBucket()
    {
        var m = new TournamentQueryLatencyMetrics();
        m.ObserveDuration("bracket-records", 25, 0.030);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("le=\"+Inf\"", text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_NullArg_Throws()
    {
        var m = new TournamentQueryLatencyMetrics();
        Assert.Throws<ArgumentNullException>(() => m.AppendPrometheus(null!));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Snapshot_ReturnsRecordedSeries()
    {
        var m = new TournamentQueryLatencyMetrics();
        m.ObserveDuration("replay-list", 50, 0.100);
        m.ObserveDuration("replay-list", 50, 0.250);
        var snap = m.Snapshot();
        Assert.Contains(("replay-list", TournamentQueryLatencyMetrics.BucketMedium), snap.Keys);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void EmptyEndpoint_CollapsesToUnknown()
    {
        var m = new TournamentQueryLatencyMetrics();
        m.ObserveDuration("", 25, 0.005);
        var snap = m.Snapshot();
        Assert.Contains(("unknown", TournamentQueryLatencyMetrics.BucketSmall), snap.Keys);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void ObserveTimestamp_RecordsObservation()
    {
        var m = new TournamentQueryLatencyMetrics();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Thread.Sleep(5);
        m.ObserveTimestamp("bracket-records", 25, t0);
        var snap = m.Snapshot();
        Assert.True(snap.TryGetValue(("bracket-records", TournamentQueryLatencyMetrics.BucketSmall),
            out var series));
        Assert.Equal(1, series.Count);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void BucketBoundsSeconds_HasCanonicalShape()
    {
        var bounds = TournamentQueryLatencyMetrics.BucketBoundsSeconds;
        Assert.Contains(0.005, bounds);
        Assert.Contains(0.025, bounds);
        Assert.Contains(0.100, bounds);
        Assert.Contains(1.0, bounds);
        Assert.Contains(10.0, bounds);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void MetricNameConstant_IsTournamentQueryDurationSeconds()
    {
        Assert.Equal("tournament_query_duration_seconds", TournamentQueryLatencyMetrics.MetricName);
    }
}
