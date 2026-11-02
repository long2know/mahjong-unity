using Mahjong.Autotable.Api.Auth;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Pure-unit pins of the
/// <see cref="JwtDurationMetrics"/> bucket ladder bounds that
/// match the SLO target the W19 Grafana dashboard alerts on.
/// </summary>
public sealed class JwtDurationMetricsBucketLadderTests
{
    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket0_Is250Microseconds()
    {
        Assert.Equal(0.00025, JwtDurationMetrics.BucketsSeconds[0], 6);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket1_Is500Microseconds()
    {
        Assert.Equal(0.0005, JwtDurationMetrics.BucketsSeconds[1], 6);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_TopIs5Seconds()
    {
        Assert.Equal(5.0, JwtDurationMetrics.BucketsSeconds[^1], 6);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_CoversP99Slo50Milliseconds()
    {
        Assert.Contains(0.05, JwtDurationMetrics.BucketsSeconds);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_CoversP95Slo25Milliseconds()
    {
        Assert.Contains(0.025, JwtDurationMetrics.BucketsSeconds);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_CoversP50Target10Milliseconds()
    {
        Assert.Contains(0.01, JwtDurationMetrics.BucketsSeconds);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_HasMillisecondCoverage()
    {
        Assert.Contains(0.001, JwtDurationMetrics.BucketsSeconds);
        Assert.Contains(0.0025, JwtDurationMetrics.BucketsSeconds);
        Assert.Contains(0.005, JwtDurationMetrics.BucketsSeconds);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_HasHundredMillisecondAndSecondCoverage()
    {
        Assert.Contains(0.1, JwtDurationMetrics.BucketsSeconds);
        Assert.Contains(0.25, JwtDurationMetrics.BucketsSeconds);
        Assert.Contains(1.0, JwtDurationMetrics.BucketsSeconds);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_OnTheBoundary_FallsIntoOwnBucket()
    {
        var m = new JwtDurationMetrics();
        // Exactly 1ms — should be in the 0.001 bucket (index 2)
        m.RecordIssue("t", TimeSpan.FromSeconds(0.001));
        var snap = m.SnapshotIssue();
        Assert.Equal(1, snap["t"].BucketCounts[2]);
        Assert.Equal(0, snap["t"].BucketCounts[1]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Bucket_BarelyAboveBoundary_FallsIntoNextBucket()
    {
        var m = new JwtDurationMetrics();
        m.RecordIssue("t", TimeSpan.FromTicks(10001)); // ~ 1.0001ms
        var snap = m.SnapshotIssue();
        // 1.0001ms > 0.001 bucket boundary => goes to 0.0025
        Assert.Equal(0, snap["t"].BucketCounts[2]);
        Assert.Equal(1, snap["t"].BucketCounts[3]);
    }
}
