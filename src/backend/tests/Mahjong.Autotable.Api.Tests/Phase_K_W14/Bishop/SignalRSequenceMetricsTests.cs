using System.Text;
using Mahjong.Autotable.Api.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Bishop;

/// <summary>
/// Phase K Wave 14 — Bishop. Hard-asserted contract for the
/// new SignalR sequence Prometheus metrics collector.
///
/// <list type="number">
///   <item><see cref="SignalRSequenceMetrics"/> class exists.</item>
///   <item>Metric name constants are stable + match the docs.</item>
///   <item>Result label constants are stable (<c>hit</c>,
///         <c>miss</c>, <c>expired</c>).</item>
///   <item><see cref="SignalRSequenceMetrics.RecordReplayFromAck"/>
///         increments the counter by one per call.</item>
///   <item>Recording multiple results into the same
///         <c>(hub, result)</c> bucket accumulates.</item>
///   <item>Recording into different buckets keeps them
///         segregated.</item>
///   <item>Empty / null hub collapses to <c>"unknown"</c>.</item>
///   <item><see cref="SignalRSequenceMetrics.RecordRetentionSweepDeleted"/>
///         adds positive counts to the lifetime total.</item>
///   <item>Zero / negative counts are no-ops.</item>
///   <item><see cref="SignalRSequenceMetrics.AppendPrometheus"/>
///         emits the HELP + TYPE preambles for all three
///         metrics unconditionally.</item>
///   <item>The gauge sample reflects the supplied
///         <c>storeRowCount</c> argument.</item>
///   <item><see cref="SignalRSequenceRetentionSweep"/> ctor
///         accepts an optional collector + records sweep
///         deletions through it.</item>
///   <item><see cref="MetricsEndpoint.AppendSignalRSequenceMetrics"/>
///         renders a fallback envelope (preamble-only) when no
///         collector is wired.</item>
/// </list>
/// </summary>
public sealed class SignalRSequenceMetricsTests
{
    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Collector_TypeExists()
    {
        Assert.NotNull(typeof(SignalRSequenceMetrics));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void MetricName_ReplayFromAck_IsStable()
    {
        Assert.Equal("signalr_seq_replay_from_ack_total",
            SignalRSequenceMetrics.MetricReplayFromAckTotal);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void MetricName_StoreRowsActive_IsStable()
    {
        Assert.Equal("signalr_seq_store_rows_active",
            SignalRSequenceMetrics.MetricStoreRowsActive);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void MetricName_RetentionSweepDeleted_IsStable()
    {
        Assert.Equal("signalr_seq_retention_sweep_deleted_total",
            SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void ResultLabels_AreStable()
    {
        Assert.Equal("hit", SignalRSequenceMetrics.ResultHit);
        Assert.Equal("miss", SignalRSequenceMetrics.ResultMiss);
        Assert.Equal("expired", SignalRSequenceMetrics.ResultExpired);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RecordReplayFromAck_IncrementsBucketByOne()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordReplayFromAck("ChangshaHub", SignalRSequenceMetrics.ResultHit);
        var snap = m.ReplaySnapshot();
        Assert.Equal(1L, snap[("ChangshaHub", "hit")]);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RecordReplayFromAck_Accumulates()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordReplayFromAck("ChangshaHub", "hit");
        m.RecordReplayFromAck("ChangshaHub", "hit");
        m.RecordReplayFromAck("ChangshaHub", "hit");
        var snap = m.ReplaySnapshot();
        Assert.Equal(3L, snap[("ChangshaHub", "hit")]);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RecordReplayFromAck_BucketsSegregated()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordReplayFromAck("ChangshaHub", "hit");
        m.RecordReplayFromAck("ChangshaHub", "miss");
        m.RecordReplayFromAck("TournamentMatchHub", "hit");
        var snap = m.ReplaySnapshot();
        Assert.Equal(1L, snap[("ChangshaHub", "hit")]);
        Assert.Equal(1L, snap[("ChangshaHub", "miss")]);
        Assert.Equal(1L, snap[("TournamentMatchHub", "hit")]);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RecordReplayFromAck_EmptyHub_CollapsesToUnknown()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordReplayFromAck("", "hit");
        m.RecordReplayFromAck(null!, "hit");
        var snap = m.ReplaySnapshot();
        Assert.Equal(2L, snap[("unknown", "hit")]);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RecordRetentionSweepDeleted_Adds()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordRetentionSweepDeleted(5);
        m.RecordRetentionSweepDeleted(3);
        Assert.Equal(8L, m.RetentionSweepDeletedTotal);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RecordRetentionSweepDeleted_ZeroIsNoOp()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordRetentionSweepDeleted(0);
        Assert.Equal(0L, m.RetentionSweepDeletedTotal);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RecordRetentionSweepDeleted_NegativeIsNoOp()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordRetentionSweepDeleted(-10);
        Assert.Equal(0L, m.RetentionSweepDeletedTotal);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndTypePreambles_Unconditional()
    {
        var m = new SignalRSequenceMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb, storeRowCount: 0);
        var text = sb.ToString();
        // HELP + TYPE for each metric, even with zero samples.
        Assert.Contains($"# HELP {SignalRSequenceMetrics.MetricReplayFromAckTotal}", text);
        Assert.Contains($"# TYPE {SignalRSequenceMetrics.MetricReplayFromAckTotal} counter", text);
        Assert.Contains($"# HELP {SignalRSequenceMetrics.MetricStoreRowsActive}", text);
        Assert.Contains($"# TYPE {SignalRSequenceMetrics.MetricStoreRowsActive} gauge", text);
        Assert.Contains($"# HELP {SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal}", text);
        Assert.Contains($"# TYPE {SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal} counter", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_StoreRowCount_FeedsGauge()
    {
        var m = new SignalRSequenceMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb, storeRowCount: 4128);
        var text = sb.ToString();
        Assert.Contains($"{SignalRSequenceMetrics.MetricStoreRowsActive} 4128", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_NegativeStoreRowCount_ClampsToZero()
    {
        var m = new SignalRSequenceMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb, storeRowCount: -7);
        var text = sb.ToString();
        Assert.Contains($"{SignalRSequenceMetrics.MetricStoreRowsActive} 0", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_RetentionDeletedTotal_Surfaced()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordRetentionSweepDeleted(42);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb, storeRowCount: 0);
        var text = sb.ToString();
        Assert.Contains($"{SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal} 42", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_ReplayCounter_RendersLabelledSample()
    {
        var m = new SignalRSequenceMetrics();
        m.RecordReplayFromAck("ChangshaHub", "hit");
        m.RecordReplayFromAck("ChangshaHub", "hit");
        var sb = new StringBuilder();
        m.AppendPrometheus(sb, storeRowCount: 0);
        var text = sb.ToString();
        Assert.Contains(
            $"{SignalRSequenceMetrics.MetricReplayFromAckTotal}{{hub=\"ChangshaHub\",result=\"hit\"}} 2",
            text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void RetentionSweep_AcceptsOptionalMetrics()
    {
        var store = new InMemorySignalRSequenceStore(
            new SignalRSequenceStoreOptions { RetentionMinutes = 1 });
        var metrics = new SignalRSequenceMetrics();
        var sweep = new SignalRSequenceRetentionSweep(
            store,
            new SignalRSequenceRetentionSweepOptions { SweepIntervalMinutes = 5 },
            NullLogger<SignalRSequenceRetentionSweep>.Instance,
            metrics);
        Assert.NotNull(sweep);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task RetentionSweep_StampsCounter_OnNonZeroDeletion()
    {
        var store = new InMemorySignalRSequenceStore(
            new SignalRSequenceStoreOptions { RetentionMinutes = 1 });
        // Seed an expired row.
        await store.AppendAsync(new SignalRSequenceEntry
        {
            HubName = "TestHub",
            ConnectionId = "conn-A",
            GroupName = "g",
            Method = "M",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddHours(-1), // expired
            PayloadJson = "{}",
        });
        var metrics = new SignalRSequenceMetrics();
        var sweep = new SignalRSequenceRetentionSweep(
            store,
            new SignalRSequenceRetentionSweepOptions { SweepIntervalMinutes = 5 },
            NullLogger<SignalRSequenceRetentionSweep>.Instance,
            metrics);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, removed);
        Assert.Equal(1L, metrics.RetentionSweepDeletedTotal);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task RetentionSweep_NoExpiredRows_DoesNotBumpCounter()
    {
        var store = new InMemorySignalRSequenceStore(
            new SignalRSequenceStoreOptions { RetentionMinutes = 60 });
        await store.AppendAsync(new SignalRSequenceEntry
        {
            HubName = "TestHub",
            ConnectionId = "conn-B",
            GroupName = "g",
            Method = "M",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            PayloadJson = "{}",
        });
        var metrics = new SignalRSequenceMetrics();
        var sweep = new SignalRSequenceRetentionSweep(
            store,
            new SignalRSequenceRetentionSweepOptions { SweepIntervalMinutes = 5 },
            NullLogger<SignalRSequenceRetentionSweep>.Instance,
            metrics);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, removed);
        Assert.Equal(0L, metrics.RetentionSweepDeletedTotal);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void MetricsEndpoint_FallbackRenders_When_NoCollectorWired()
    {
        var sb = new StringBuilder();
        MetricsEndpoint.AppendSignalRSequenceMetrics(sb, metrics: null, store: null);
        var text = sb.ToString();
        // Even without a collector wired, HELP + TYPE preambles for
        // all three metrics must appear so the scrape schema is
        // stable.
        Assert.Contains($"# HELP {SignalRSequenceMetrics.MetricReplayFromAckTotal}", text);
        Assert.Contains($"# TYPE {SignalRSequenceMetrics.MetricReplayFromAckTotal} counter", text);
        Assert.Contains($"# HELP {SignalRSequenceMetrics.MetricStoreRowsActive}", text);
        Assert.Contains($"# TYPE {SignalRSequenceMetrics.MetricStoreRowsActive} gauge", text);
        Assert.Contains($"# HELP {SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal}", text);
        Assert.Contains($"# TYPE {SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal} counter", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void MetricsEndpoint_FallbackGauge_IsZero_When_NoStore()
    {
        var sb = new StringBuilder();
        MetricsEndpoint.AppendSignalRSequenceMetrics(sb, metrics: null, store: null);
        var text = sb.ToString();
        Assert.Contains($"{SignalRSequenceMetrics.MetricStoreRowsActive} 0", text);
        Assert.Contains($"{SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal} 0", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void MetricsEndpoint_WithCollector_Delegates_To_AppendPrometheus()
    {
        var metrics = new SignalRSequenceMetrics();
        metrics.RecordReplayFromAck("ChangshaHub", "hit");
        metrics.RecordRetentionSweepDeleted(3);
        var sb = new StringBuilder();
        MetricsEndpoint.AppendSignalRSequenceMetrics(sb, metrics, store: null);
        var text = sb.ToString();
        Assert.Contains(
            $"{SignalRSequenceMetrics.MetricReplayFromAckTotal}{{hub=\"ChangshaHub\",result=\"hit\"}} 1",
            text);
        Assert.Contains($"{SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal} 3", text);
    }
}
