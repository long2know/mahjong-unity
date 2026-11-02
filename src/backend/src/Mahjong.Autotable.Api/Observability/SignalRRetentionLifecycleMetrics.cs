using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 19 — Bishop. Event-bus emission for the per-tenant
/// SignalR retention policy surface. W17 + W18 shipped the
/// per-tenant <see cref="SignalRRetentionPolicy"/> entity + the
/// W18 hard-cap <see cref="SignalRRetentionPolicyCappedMetrics"/>;
/// W19 lands two complementary counters so operators can graph
/// the per-tenant retention lifecycle without scraping the audit
/// table.
///
/// <list type="bullet">
///   <item><c>signalr_retention_applied{tenant=&lt;t&gt;}</c> —
///         incremented every time the per-tenant policy is
///         consulted (the policy applies, no cap fires).</item>
///   <item><c>signalr_retention_cap_triggered{tenant=&lt;t&gt;}</c>
///         — incremented every time the W18 hard-cap fires for a
///         tenant (the requested TTL exceeded the ceiling and was
///         clipped DOWN). DISTINCT from the W18
///         <c>signalr_retention_policy_capped_total</c> — that
///         counter is the historical cap-event count; this one is
///         a forward-looking, lifecycle-grain counter the new
///         W19 dashboard uses.</item>
/// </list>
///
/// <para>Cardinality is bounded by the registered tenant count;
/// the evaluator threads its per-tenant ids through these
/// counters so the wire-stable label set matches the per-tenant
/// policy table. Unknown / empty tenant ids fold into the
/// <c>"_unknown"</c> bucket so a misbehaving caller can't blow
/// up Prometheus storage.</para>
///
/// <para>See <c>docs/realtime-resilience.md §7.2</c> (added W19).</para>
/// </summary>
public sealed class SignalRRetentionLifecycleMetrics
{
    /// <summary>Prometheus name for the
    /// <c>signalr_retention_applied{tenant}</c> counter.</summary>
    public const string MetricAppliedName = "signalr_retention_applied";

    /// <summary>Prometheus name for the
    /// <c>signalr_retention_cap_triggered{tenant}</c> counter.</summary>
    public const string MetricCapTriggeredName = "signalr_retention_cap_triggered";

    /// <summary>Label name carrying the per-tenant id on both
    /// counters.</summary>
    public const string TenantLabel = "tenant";

    private readonly ConcurrentDictionary<string, long> _applied = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _capTriggered = new(StringComparer.Ordinal);
    private long _totalApplied;
    private long _totalCapTriggered;

    /// <summary>Stamp a retention-applied event for a tenant.
    /// Empty / null tenants collapse to the <c>"_unknown"</c>
    /// bucket so the counter still observes activity from
    /// unregistered traffic.</summary>
    public void RecordApplied(string tenantId)
    {
        var key = string.IsNullOrWhiteSpace(tenantId) ? "_unknown" : tenantId;
        _applied.AddOrUpdate(key, 1, (_, prev) => prev + 1);
        Interlocked.Increment(ref _totalApplied);
    }

    /// <summary>Stamp a retention-cap-triggered event for a
    /// tenant. Empty / null tenants collapse to the
    /// <c>"_unknown"</c> bucket.</summary>
    public void RecordCapTriggered(string tenantId)
    {
        var key = string.IsNullOrWhiteSpace(tenantId) ? "_unknown" : tenantId;
        _capTriggered.AddOrUpdate(key, 1, (_, prev) => prev + 1);
        Interlocked.Increment(ref _totalCapTriggered);
    }

    /// <summary>Total retention-applied count across all
    /// tenants.</summary>
    public long TotalApplied => Interlocked.Read(ref _totalApplied);

    /// <summary>Total cap-triggered count across all
    /// tenants.</summary>
    public long TotalCapTriggered => Interlocked.Read(ref _totalCapTriggered);

    /// <summary>Per-tenant applied snapshot.</summary>
    public IReadOnlyDictionary<string, long> SnapshotApplied() =>
        _applied.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Per-tenant cap-triggered snapshot.</summary>
    public IReadOnlyDictionary<string, long> SnapshotCapTriggered() =>
        _capTriggered.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Render both counters in Prometheus exposition
    /// format. HELP + TYPE preambles are emitted unconditionally
    /// so a Prometheus parser sees the schema before any events
    /// have been observed.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);

        sb.Append("# HELP ").Append(MetricAppliedName)
          .AppendLine(" Total per-tenant SignalR retention-policy APPLY events. Stamped every time the per-tenant policy resolves for a tenant (the policy applies, no cap fires).");
        sb.Append("# TYPE ").Append(MetricAppliedName).AppendLine(" counter");
        foreach (var kv in _applied)
        {
            sb.Append(MetricAppliedName).Append('{').Append(TenantLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key)).Append("\"} ")
              .AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append("# HELP ").Append(MetricCapTriggeredName)
          .AppendLine(" Total per-tenant SignalR retention-policy CAP-TRIGGERED events (distinct from signalr_retention_policy_capped_total). Forward-looking, lifecycle-grain counter the W19 dashboard alerts on.");
        sb.Append("# TYPE ").Append(MetricCapTriggeredName).AppendLine(" counter");
        foreach (var kv in _capTriggered)
        {
            sb.Append(MetricCapTriggeredName).Append('{').Append(TenantLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key)).Append("\"} ")
              .AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
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
