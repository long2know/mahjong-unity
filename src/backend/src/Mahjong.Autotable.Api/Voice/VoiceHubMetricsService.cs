using System.Collections.Concurrent;

namespace Mahjong.Autotable.Api.Voice;

// Phase K Wave 3 — Bishop (backend). Lightweight per-connection
// signalling-relay counter used by the VoiceHub. The hub still gates
// per-second chatter via VoiceRateLimiter; this service exposes a
// rolling 60-second window count per connection so that the /metrics
// surface (and future ops dashboards) can observe relay pressure
// without flipping on the rate-limiter's internal token-bucket
// instrumentation.
//
// Phase K Wave 4 — Bishop. Adds rate-limit-rejection +
// join-unauthorized counters to feed the VoiceHubMetrics-named
// Prometheus surfaces (voice_rate_limit_rejection_total,
// voice_join_unauthorized_total). Both are simple monotonic counters
// — the per-connection rolling window only applies to the relay
// counter where per-connection observability is actually useful.
//
// Phase K Wave 5 — Bishop. Adds labeled monotonic counters keyed by
// (table, reason) so the /metrics endpoint can render
// `voice_join_unauthorized_total{table="…",reason="…"}` series.
// Callers pass null / empty / unknown table or reason → these
// collapse to canonical "unknown" / VoiceHubMetrics.ReasonUnknown
// constants so a noisy missing label never sprays Prometheus
// cardinality. Snapshot() returns a forward-only point-in-time view
// of every active label combination so the exposition format is
// stable across scrapes (no torn reads).
//
// Implementation notes:
// * Records are kept in-memory only — connection-scoped state is
//   meaningless across process restarts (SignalR re-issues connection
//   ids anyway).
// * `GetRelayCount(connectionId)` and `GetTotalRelayCount()` walk the
//   bucket list lazily, dropping expired ticks (>60s ago) on read.
//   Callers that fan-out on every relay still pay O(N) per hub method
//   so we keep the hot path branchless and defer expiry to readers.
// * The Wave-5 labeled counters are MONOTONIC — they never decrement
//   or expire. Prometheus counters MUST behave this way (the
//   `rate()` function depends on counter monotonicity); the rolling
//   60s window stays on the per-connection relay surface where
//   per-call introspection is the useful affordance.
public sealed class VoiceHubMetricsService
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, ConnectionMetrics> _byConnection = new();
    private long _rateLimitRejections;
    private long _joinUnauthorized;

    // Phase K Wave 5 — labeled Prometheus counters. Key is the
    // composite (table, reason) tuple stored as a single string so
    // the ConcurrentDictionary's per-key locking matches the
    // exposition cardinality 1:1.
    private readonly ConcurrentDictionary<LabelKey, long> _relayByTable = new();
    private readonly ConcurrentDictionary<LabelKey, long> _rejectionByTableReason = new();
    private readonly ConcurrentDictionary<LabelKey, long> _joinUnauthorizedByTableReason = new();

    public void RecordRelay(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        var metrics = _byConnection.GetOrAdd(connectionId, _ => new ConnectionMetrics());
        metrics.Record(DateTime.UtcNow);
    }

    /// <summary>Phase K Wave 5 — labeled overload of <see cref="RecordRelay(string)"/>.
    /// Increments the per-table monotonic counter for the Prometheus
    /// surface in addition to the rolling per-connection window.</summary>
    public void RecordRelay(string connectionId, string? tableId)
    {
        RecordRelay(connectionId);
        var key = new LabelKey(NormalizeTable(tableId), null);
        _relayByTable.AddOrUpdate(key, 1L, static (_, v) => v + 1L);
    }

    /// <summary>Phase K Wave 4 — increments the
    /// <see cref="VoiceHubMetrics.MetricRateLimitRejection"/> counter
    /// whenever the rate limiter denies a relay.</summary>
    public void RecordRateLimitRejection() => System.Threading.Interlocked.Increment(ref _rateLimitRejections);

    /// <summary>Phase K Wave 5 — labeled overload of
    /// <see cref="RecordRateLimitRejection()"/>. Increments the
    /// per-table-per-reason monotonic counter for the Prometheus
    /// surface in addition to the unlabeled total.</summary>
    public void RecordRateLimitRejection(string? tableId, string? reason)
    {
        RecordRateLimitRejection();
        var key = new LabelKey(NormalizeTable(tableId), NormalizeReason(reason));
        _rejectionByTableReason.AddOrUpdate(key, 1L, static (_, v) => v + 1L);
    }

    /// <summary>Phase K Wave 4 — increments the
    /// <see cref="VoiceHubMetrics.MetricJoinUnauthorized"/> counter
    /// whenever JoinVoice is rejected by the per-table auth gate.</summary>
    public void RecordJoinUnauthorized() => System.Threading.Interlocked.Increment(ref _joinUnauthorized);

    /// <summary>Phase K Wave 5 — labeled overload of
    /// <see cref="RecordJoinUnauthorized()"/>. Increments the
    /// per-table-per-reason monotonic counter for the Prometheus
    /// surface in addition to the unlabeled total.</summary>
    public void RecordJoinUnauthorized(string? tableId, string? reason)
    {
        RecordJoinUnauthorized();
        var key = new LabelKey(NormalizeTable(tableId), NormalizeReason(reason));
        _joinUnauthorizedByTableReason.AddOrUpdate(key, 1L, static (_, v) => v + 1L);
    }

    public long GetRateLimitRejectionCount() => System.Threading.Interlocked.Read(ref _rateLimitRejections);

    public long GetJoinUnauthorizedCount() => System.Threading.Interlocked.Read(ref _joinUnauthorized);

    public int GetRelayCount(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return 0;
        if (!_byConnection.TryGetValue(connectionId, out var metrics)) return 0;
        return metrics.CountWithin(DateTime.UtcNow - Window);
    }

    public int GetTotalRelayCount()
    {
        var cutoff = DateTime.UtcNow - Window;
        var total = 0;
        foreach (var metrics in _byConnection.Values)
        {
            total += metrics.CountWithin(cutoff);
        }
        return total;
    }

    /// <summary>
    /// Phase K Wave 5 — point-in-time snapshot of every labeled
    /// counter. Returned in a stable ordering (metric name → table →
    /// reason) so the Prometheus exposition is byte-stable across
    /// scrapes when no new events have happened in between.
    /// </summary>
    public IReadOnlyList<LabeledMetricSample> Snapshot()
    {
        var rows = new List<LabeledMetricSample>(
            _relayByTable.Count
            + _rejectionByTableReason.Count
            + _joinUnauthorizedByTableReason.Count);
        foreach (var kvp in _relayByTable)
        {
            rows.Add(new LabeledMetricSample(
                VoiceHubMetrics.MetricRelayCount, kvp.Key.Table, null, kvp.Value));
        }
        foreach (var kvp in _rejectionByTableReason)
        {
            rows.Add(new LabeledMetricSample(
                VoiceHubMetrics.MetricRateLimitRejection, kvp.Key.Table, kvp.Key.Reason, kvp.Value));
        }
        foreach (var kvp in _joinUnauthorizedByTableReason)
        {
            rows.Add(new LabeledMetricSample(
                VoiceHubMetrics.MetricJoinUnauthorized, kvp.Key.Table, kvp.Key.Reason, kvp.Value));
        }
        // Stable order: metric name → table → reason. Prometheus
        // doesn't require ordering, but byte-stable output makes
        // scrape diffs trivially diffable.
        rows.Sort(static (a, b) =>
        {
            var byMetric = string.CompareOrdinal(a.Metric, b.Metric);
            if (byMetric != 0) return byMetric;
            var byTable = string.CompareOrdinal(a.Table, b.Table);
            if (byTable != 0) return byTable;
            return string.CompareOrdinal(a.Reason ?? string.Empty, b.Reason ?? string.Empty);
        });
        return rows;
    }

    public void Forget(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        _byConnection.TryRemove(connectionId, out _);
    }

    private static string NormalizeTable(string? tableId) =>
        string.IsNullOrWhiteSpace(tableId) ? "unknown" : tableId;

    private static string NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? VoiceHubMetrics.ReasonUnknown : reason;

    private readonly record struct LabelKey(string Table, string? Reason);

    private sealed class ConnectionMetrics
    {
        private readonly object _lock = new();
        // Ring-style queue of relay timestamps. Pruned lazily on read.
        private readonly Queue<DateTime> _ticks = new();

        public void Record(DateTime atUtc)
        {
            lock (_lock)
            {
                _ticks.Enqueue(atUtc);
                Prune(atUtc - Window);
            }
        }

        public int CountWithin(DateTime cutoffUtc)
        {
            lock (_lock)
            {
                Prune(cutoffUtc);
                return _ticks.Count;
            }
        }

        private void Prune(DateTime cutoffUtc)
        {
            while (_ticks.Count > 0 && _ticks.Peek() < cutoffUtc)
            {
                _ticks.Dequeue();
            }
        }
    }
}

/// <summary>
/// Phase K Wave 5 — Bishop. Single labeled-counter point read returned
/// by <see cref="VoiceHubMetricsService.Snapshot"/>. The metric name
/// is the canonical Prometheus identifier (see
/// <see cref="VoiceHubMetrics"/>); <see cref="Table"/> is always
/// non-empty (callers pass "unknown" when the table id can't be
/// resolved); <see cref="Reason"/> is null when the metric doesn't
/// carry a reason dimension (the relay-count surface, for example).
/// </summary>
public sealed record LabeledMetricSample(string Metric, string Table, string? Reason, long Value);
