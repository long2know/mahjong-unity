using System.Text;
using Mahjong.Autotable.Api.Auth;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Contract tests for the
/// <see cref="JwtDurationMetrics"/> histogram collector. Covers:
/// observation recording, bucket-boundary semantics, Prometheus
/// rendering (HELP/TYPE preambles + +Inf bucket + label
/// escaping), and total-count aggregation.
/// </summary>
public sealed class JwtDurationMetricsTests
{
    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordIssue_NormalisesEmptyTenantToGlobal()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("", TimeSpan.FromMilliseconds(5));
        var snap = m.SnapshotIssue();
        Assert.True(snap.ContainsKey(JwtDurationMetrics.GlobalTenantLabel));
        Assert.Equal(1, snap[JwtDurationMetrics.GlobalTenantLabel].Count);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordValidatorCheck_NormalisesEmptyTenantToUnknown()
    {
        var m = new JwtDurationMetrics();
        m.RecordValidatorCheck("   ", TimeSpan.FromMilliseconds(2));
        var snap = m.SnapshotValidatorCheck();
        Assert.True(snap.ContainsKey(JwtDurationMetrics.UnknownTenantLabel));
        Assert.Equal(1, snap[JwtDurationMetrics.UnknownTenantLabel].Count);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordIssue_AssignsToSmallestBucketCoveringObservation()
    {
        var m = new JwtDurationMetrics();
        // 0.5 ms => fits in the 0.0005 bucket (index 1).
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(0.5));
        var snap = m.SnapshotIssue();
        Assert.Equal(1, snap["tenant-x"].BucketCounts[1]);
        Assert.Equal(0, snap["tenant-x"].BucketCounts[0]);
        Assert.Equal(1, snap["tenant-x"].Count);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordIssue_LargerThanLastBucket_StillStampsCount()
    {
        var m = new JwtDurationMetrics();
        // 10s > 5s top bucket => no per-bucket increment but
        // overall count still advances (the +Inf bucket
        // implicitly carries the overflow).
        m.RecordIssue("tenant-y", TimeSpan.FromSeconds(10));
        var snap = m.SnapshotIssue();
        Assert.Equal(1, snap["tenant-y"].Count);
        // All explicit buckets are zero.
        foreach (var b in snap["tenant-y"].BucketCounts)
        {
            Assert.Equal(0, b);
        }
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void TotalIssueObservations_AggregatesAcrossTenants()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("a", TimeSpan.FromMilliseconds(1));
        m.RecordIssue("b", TimeSpan.FromMilliseconds(2));
        m.RecordIssue("a", TimeSpan.FromMilliseconds(3));
        Assert.Equal(3, m.TotalIssueObservations);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void TotalValidatorCheckObservations_AggregatesAcrossTenants()
    {
        var m = new JwtDurationMetrics();
        m.RecordValidatorCheck("a", TimeSpan.FromMilliseconds(1));
        m.RecordValidatorCheck("b", TimeSpan.FromMilliseconds(2));
        Assert.Equal(2, m.TotalValidatorCheckObservations);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndTypeForBothHistograms()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(1));
        m.RecordValidatorCheck("tenant-x", TimeSpan.FromMilliseconds(2));
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("# HELP " + JwtDurationMetrics.IssueMetricName, text);
        Assert.Contains("# TYPE " + JwtDurationMetrics.IssueMetricName + " histogram", text);
        Assert.Contains("# HELP " + JwtDurationMetrics.ValidatorCheckMetricName, text);
        Assert.Contains("# TYPE " + JwtDurationMetrics.ValidatorCheckMetricName + " histogram", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsPlusInfBucketForEachTenant()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(1));
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains($"{JwtDurationMetrics.IssueMetricName}_bucket{{tenant=\"tenant-x\",le=\"+Inf\"}} 1", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsCumulativeBucketCountsInAscendingLeOrder()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(0.4));
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(0.6));
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(50));
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        // le=0.00025 sees 0, le=0.0005 sees 1, le=0.001 sees 2,
        // le=0.005 sees 2, le=0.05 sees 3, le=+Inf sees 3.
        Assert.Contains("le=\"0.0005\"} 1", text);
        Assert.Contains("le=\"0.001\"} 2", text);
        Assert.Contains("le=\"0.05\"} 3", text);
        Assert.Contains("le=\"+Inf\"} 3", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsSumAndCountSeries()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(1));
        m.RecordIssue("tenant-x", TimeSpan.FromMilliseconds(2));
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains($"{JwtDurationMetrics.IssueMetricName}_sum{{tenant=\"tenant-x\"}}", text);
        Assert.Contains($"{JwtDurationMetrics.IssueMetricName}_count{{tenant=\"tenant-x\"}} 2", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EscapesLabelMetacharacters()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("tenant\\\"x\nbad", TimeSpan.FromMilliseconds(1));
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("tenant=\"tenant\\\\\\\"x\\nbad\"", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void BucketLadder_HasTwelveEntries()
    {
        Assert.Equal(12, JwtDurationMetrics.BucketsSeconds.Length);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void BucketLadder_IsStrictlyAscending()
    {
        for (var i = 1; i < JwtDurationMetrics.BucketsSeconds.Length; i++)
        {
            Assert.True(
                JwtDurationMetrics.BucketsSeconds[i] > JwtDurationMetrics.BucketsSeconds[i - 1],
                $"Bucket ladder must be strictly ascending: index {i}");
        }
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void TimeIssue_ReturnsNormalisedTenantKey()
    {
        var m = new JwtDurationMetrics();
        var key = m.TimeIssue("tenant-z", () => Thread.Sleep(1));
        Assert.Equal("tenant-z", key);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void TimeValidatorCheck_FoldsEmptyTenantIntoUnknown()
    {
        var m = new JwtDurationMetrics();
        var key = m.TimeValidatorCheck("", () => Thread.Sleep(1));
        Assert.Equal(JwtDurationMetrics.UnknownTenantLabel, key);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordIssue_AccumulatesSum()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("t", TimeSpan.FromMilliseconds(1));
        m.RecordIssue("t", TimeSpan.FromMilliseconds(2));
        var snap = m.SnapshotIssue();
        Assert.Equal(0.003, snap["t"].Sum, 6);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void IssueAndValidatorCheck_AreIndependentCollectors()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("t", TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, m.TotalIssueObservations);
        Assert.Equal(0, m.TotalValidatorCheckObservations);
        m.RecordValidatorCheck("t", TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, m.TotalIssueObservations);
        Assert.Equal(1, m.TotalValidatorCheckObservations);
    }
}
