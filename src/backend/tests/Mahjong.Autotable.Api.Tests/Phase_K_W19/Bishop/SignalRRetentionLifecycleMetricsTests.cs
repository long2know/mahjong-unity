using System.Text;
using Mahjong.Autotable.Api.Observability;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Contract tests for the new
/// <see cref="SignalRRetentionLifecycleMetrics"/> counter.
/// Covers: per-tenant accumulation, empty-tenant folding,
/// total-count rollups, Prometheus rendering, and label
/// escaping. Distinct from the W18 cap counter so the W19
/// dashboard can graph lifecycle counts WITHOUT joining
/// historical W18 data.
/// </summary>
public sealed class SignalRRetentionLifecycleMetricsTests
{
    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordApplied_IncrementsPerTenantBucket()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordApplied("tenant-a");
        m.RecordApplied("tenant-a");
        m.RecordApplied("tenant-b");
        var snap = m.SnapshotApplied();
        Assert.Equal(2, snap["tenant-a"]);
        Assert.Equal(1, snap["tenant-b"]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordCapTriggered_IncrementsPerTenantBucket()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordCapTriggered("tenant-a");
        m.RecordCapTriggered("tenant-b");
        m.RecordCapTriggered("tenant-a");
        var snap = m.SnapshotCapTriggered();
        Assert.Equal(2, snap["tenant-a"]);
        Assert.Equal(1, snap["tenant-b"]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordApplied_EmptyTenantFoldsIntoUnknown()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordApplied("");
        m.RecordApplied("   ");
        m.RecordApplied(null!);
        var snap = m.SnapshotApplied();
        Assert.Equal(3, snap["_unknown"]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void RecordCapTriggered_EmptyTenantFoldsIntoUnknown()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordCapTriggered(null!);
        var snap = m.SnapshotCapTriggered();
        Assert.Equal(1, snap["_unknown"]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Totals_AggregateAcrossTenants()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordApplied("a");
        m.RecordApplied("b");
        m.RecordApplied("a");
        m.RecordCapTriggered("a");
        Assert.Equal(3, m.TotalApplied);
        Assert.Equal(1, m.TotalCapTriggered);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndTypeForBothCounters()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordApplied("a");
        m.RecordCapTriggered("b");
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("# HELP " + SignalRRetentionLifecycleMetrics.MetricAppliedName, text);
        Assert.Contains("# TYPE " + SignalRRetentionLifecycleMetrics.MetricAppliedName + " counter", text);
        Assert.Contains("# HELP " + SignalRRetentionLifecycleMetrics.MetricCapTriggeredName, text);
        Assert.Contains("# TYPE " + SignalRRetentionLifecycleMetrics.MetricCapTriggeredName + " counter", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsPerTenantLines()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordApplied("tenant-x");
        m.RecordApplied("tenant-x");
        m.RecordCapTriggered("tenant-y");
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains($"{SignalRRetentionLifecycleMetrics.MetricAppliedName}{{tenant=\"tenant-x\"}} 2", text);
        Assert.Contains($"{SignalRRetentionLifecycleMetrics.MetricCapTriggeredName}{{tenant=\"tenant-y\"}} 1", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsZeroEnvelopeEvenWithNoSamples()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("# HELP " + SignalRRetentionLifecycleMetrics.MetricAppliedName, text);
        Assert.Contains("# HELP " + SignalRRetentionLifecycleMetrics.MetricCapTriggeredName, text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EscapesLabelMetacharacters()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordApplied("ten\"\\\nbad");
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("tenant=\"ten\\\"\\\\\\nbad\"", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void MetricNames_MatchSpec()
    {
        Assert.Equal("signalr_retention_applied", SignalRRetentionLifecycleMetrics.MetricAppliedName);
        Assert.Equal("signalr_retention_cap_triggered", SignalRRetentionLifecycleMetrics.MetricCapTriggeredName);
        Assert.Equal("tenant", SignalRRetentionLifecycleMetrics.TenantLabel);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Snapshot_IsAStableCopy()
    {
        var m = new SignalRRetentionLifecycleMetrics();
        m.RecordApplied("a");
        var snap1 = m.SnapshotApplied();
        m.RecordApplied("a");
        var snap2 = m.SnapshotApplied();
        Assert.Equal(1, snap1["a"]);
        Assert.Equal(2, snap2["a"]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void DistinctFromW18CounterName()
    {
        // Sanity: ensure W19 name is NOT a substring or
        // alias of the W18 counter so dashboards don't
        // collide.
        var w19Applied = SignalRRetentionLifecycleMetrics.MetricAppliedName;
        var w19Cap = SignalRRetentionLifecycleMetrics.MetricCapTriggeredName;
        var w18Name = "signalr_retention_policy_capped_total";
        Assert.NotEqual(w18Name, w19Applied);
        Assert.NotEqual(w18Name, w19Cap);
    }
}
