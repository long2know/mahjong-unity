using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 14 — Bishop. Singleton metrics collector for the
/// W12 SignalR sequence store + W13 retention sweep. Surfaces three
/// metrics on the existing Prometheus endpoint:
///
/// <list type="bullet">
///   <item><c>signalr_seq_replay_from_ack_total{hub, result}</c> —
///         counter. Incremented every time a
///         <see cref="ISignalRSequenceStore.ReadFromAckAsync"/>
///         call resolves. <c>result</c> ∈
///         <c>{ hit, miss, expired }</c>:
///         <c>hit</c> when one or more rows are returned;
///         <c>miss</c> when zero rows are returned but the store
///         was queried successfully (typically a brand-new
///         connection / no entries newer than the ack);
///         <c>expired</c> when the requested ack pointer is older
///         than the sweep retention window (callers signal this
///         by passing <c>result = "expired"</c>).</item>
///   <item><c>signalr_seq_store_rows_active</c> — gauge sampled at
///         scrape time from <see cref="ISignalRSequenceStore.CountAsync"/>.</item>
///   <item><c>signalr_seq_retention_sweep_deleted_total</c> —
///         counter incremented by the
///         <see cref="SignalRSequenceRetentionSweep"/> hosted
///         service every tick (by the number of rows it
///         deleted).</item>
/// </list>
///
/// <para>The collector is intentionally side-channel — neither
/// the store nor the sweep takes a hard dependency on it. We
/// register a singleton in <c>Program.cs</c> and resolve it
/// optionally from the consumers so a test fixture that wires
/// only the store still works. See
/// <c>docs/realtime-resilience.md §8 "Metrics"</c>.</para>
/// </summary>
public sealed class SignalRSequenceMetrics
{
    public const string MetricReplayFromAckTotal = "signalr_seq_replay_from_ack_total";
    public const string MetricStoreRowsActive = "signalr_seq_store_rows_active";
    public const string MetricRetentionSweepDeletedTotal = "signalr_seq_retention_sweep_deleted_total";

    public const string ResultHit = "hit";
    public const string ResultMiss = "miss";
    public const string ResultExpired = "expired";

    private readonly ConcurrentDictionary<(string Hub, string Result), long> _replayCounter =
        new();
    private long _retentionDeleted;

    /// <summary>
    /// Phase K Wave 14 — Bishop. Increment the
    /// <c>signalr_seq_replay_from_ack_total{hub, result}</c>
    /// counter by one. Empty / null hub names collapse to the
    /// <c>"unknown"</c> label so the bucket is still observable.
    /// </summary>
    public void RecordReplayFromAck(string hub, string result)
    {
        var hubLabel = string.IsNullOrWhiteSpace(hub) ? "unknown" : hub;
        var resultLabel = string.IsNullOrWhiteSpace(result) ? ResultMiss : result;
        _replayCounter.AddOrUpdate((hubLabel, resultLabel), 1, (_, prev) => prev + 1);
    }

    /// <summary>
    /// Phase K Wave 14 — Bishop. Add the count of rows the
    /// retention sweep deleted on the last tick to the lifetime
    /// counter. Negative / zero values are no-ops.
    /// </summary>
    public void RecordRetentionSweepDeleted(int deleted)
    {
        if (deleted <= 0) return;
        System.Threading.Interlocked.Add(ref _retentionDeleted, deleted);
    }

    /// <summary>Snapshot of replay-from-ack counter buckets —
    /// surfaced for tests + the Prometheus rendering path.</summary>
    public IReadOnlyDictionary<(string Hub, string Result), long> ReplaySnapshot() =>
        _replayCounter.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Snapshot of the lifetime retention-sweep deletion
    /// counter.</summary>
    public long RetentionSweepDeletedTotal => System.Threading.Interlocked.Read(ref _retentionDeleted);

    /// <summary>
    /// Phase K Wave 14 — Bishop. Renders the three metrics in
    /// Prometheus exposition format. HELP + TYPE preambles are
    /// emitted unconditionally so a parser sees the schema even
    /// when no events have occurred yet. The
    /// <c>signalr_seq_store_rows_active</c> gauge is sampled
    /// against the supplied <paramref name="storeRowCount"/>
    /// argument so the scrape path can resolve the store row
    /// count once and inject it (avoids two trips into the EF
    /// store from inside the metrics renderer).
    /// </summary>
    public void AppendPrometheus(StringBuilder sb, long storeRowCount)
    {
        ArgumentNullException.ThrowIfNull(sb);

        sb.Append("# HELP ").Append(MetricReplayFromAckTotal)
          .AppendLine(" Total SignalR replay-from-ack queries resolved against the durable sequence store. Labelled by `hub` (target hub class) and `result` (`hit` / `miss` / `expired`).");
        sb.Append("# TYPE ").Append(MetricReplayFromAckTotal).AppendLine(" counter");
        foreach (var kv in _replayCounter)
        {
            sb.Append(MetricReplayFromAckTotal)
              .Append("{hub=\"").Append(EscapeLabelValue(kv.Key.Hub)).Append('"')
              .Append(",result=\"").Append(EscapeLabelValue(kv.Key.Result)).Append("\"} ")
              .AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append("# HELP ").Append(MetricStoreRowsActive)
          .AppendLine(" Currently retained SignalR sequence rows across every (hub, connection) tracked by the durable store.");
        sb.Append("# TYPE ").Append(MetricStoreRowsActive).AppendLine(" gauge");
        sb.Append(MetricStoreRowsActive).Append(' ')
          .AppendLine(Math.Max(0, storeRowCount).ToString(CultureInfo.InvariantCulture));

        sb.Append("# HELP ").Append(MetricRetentionSweepDeletedTotal)
          .AppendLine(" Total SignalR sequence rows deleted by the retention sweep since the process started.");
        sb.Append("# TYPE ").Append(MetricRetentionSweepDeletedTotal).AppendLine(" counter");
        sb.Append(MetricRetentionSweepDeletedTotal).Append(' ')
          .AppendLine(RetentionSweepDeletedTotal.ToString(CultureInfo.InvariantCulture));
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
