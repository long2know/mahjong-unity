using Mahjong.Autotable.Api.Auth;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Behaviour tests for the new
/// <see cref="JwtIssueBlockedMetrics"/> Prometheus counter
/// collector.
/// </summary>
public sealed class JwtIssueBlockedMetricsTests
{
    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void MetricName_IsWireStable()
    {
        Assert.Equal("jwt_issue_blocked_total", JwtIssueBlockedMetrics.MetricName);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReasonConstants_AreWireStable()
    {
        Assert.Equal("stale_per_tenant_policy", JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        Assert.Equal("per_tenant_store_missing", JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void FreshCollector_EmptySnapshot()
    {
        var c = new JwtIssueBlockedMetrics();
        var snap = c.Snapshot();
        Assert.NotNull(snap);
        Assert.Empty(snap);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void RecordBlocked_IncrementsSnapshot()
    {
        var c = new JwtIssueBlockedMetrics();
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        var snap = c.Snapshot();
        Assert.Equal(1, snap[JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy]);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void RecordBlocked_AccumulatesAcrossCalls()
    {
        var c = new JwtIssueBlockedMetrics();
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        var snap = c.Snapshot();
        Assert.Equal(3, snap[JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy]);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void RecordBlocked_SegregatesByReason()
    {
        var c = new JwtIssueBlockedMetrics();
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing);
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing);
        var snap = c.Snapshot();
        Assert.Equal(1, snap[JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy]);
        Assert.Equal(2, snap[JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing]);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void RecordBlocked_NullOrEmpty_CollapsesToUnknown()
    {
        var c = new JwtIssueBlockedMetrics();
        c.RecordBlocked(null!);
        c.RecordBlocked("");
        c.RecordBlocked("  ");
        var snap = c.Snapshot();
        Assert.Equal(3, snap["unknown"]);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_RendersTypePreamble()
    {
        var c = new JwtIssueBlockedMetrics();
        var sb = new System.Text.StringBuilder();
        c.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("# HELP " + JwtIssueBlockedMetrics.MetricName, text);
        Assert.Contains("# TYPE " + JwtIssueBlockedMetrics.MetricName + " counter", text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_RendersStampedReasons()
    {
        var c = new JwtIssueBlockedMetrics();
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing);
        var sb = new System.Text.StringBuilder();
        c.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains(
            $"{JwtIssueBlockedMetrics.MetricName}{{reason=\"{JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy}\"}}",
            text);
        Assert.Contains(
            $"{JwtIssueBlockedMetrics.MetricName}{{reason=\"{JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing}\"}}",
            text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_HandlesEmptyCollector()
    {
        var c = new JwtIssueBlockedMetrics();
        var sb = new System.Text.StringBuilder();
        c.AppendPrometheus(sb);
        var text = sb.ToString();
        // Schema preamble always renders so dashboards don't break.
        Assert.Contains("# TYPE " + JwtIssueBlockedMetrics.MetricName, text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Snapshot_IsImmutable()
    {
        var c = new JwtIssueBlockedMetrics();
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        var snap1 = c.Snapshot();
        c.RecordBlocked(JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
        var snap2 = c.Snapshot();
        // Snapshots are point-in-time captures.
        Assert.Equal(1, snap1[JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy]);
        Assert.Equal(2, snap2[JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy]);
    }
}
