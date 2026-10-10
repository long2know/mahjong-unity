using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 17 — Bishop. Prometheus counter collector for
/// JWT-issue calls that were BLOCKED before signing. W16 landed
/// <see cref="PerTenantJwksRotationValidator"/> as a side-channel
/// gate but the call site never recorded a metric — operators
/// could not graph the volume of stale-policy blocks. W17 wires
/// the validator into <see cref="JwtIssuingService.IssueAsync"/>
/// and stamps this counter on every short-circuit.
///
/// <list type="bullet">
///   <item><c>jwt_issue_blocked_total{reason}</c> — counter,
///         incremented when a token-issue call is rejected before
///         signing. The <c>reason</c> label uses the canonical
///         wire-stable constants
///         (<see cref="ReasonStalePerTenantPolicy"/> /
///         <see cref="ReasonPerTenantStoreMissing"/>) so client
///         dashboards branch on the constant rather than the
///         human-readable message.</item>
/// </list>
///
/// <para>The collector is intentionally side-channel — the
/// issuing service takes a nullable reference so a test fixture
/// that wires only the issuer still works (the recording is a
/// no-op). The <c>MetricsEndpoint</c> renders the counter on
/// every scrape; HELP + TYPE preambles are emitted even when no
/// blocks have been observed yet so the schema is visible.</para>
/// </summary>
public sealed class JwtIssueBlockedMetrics
{
    public const string MetricName = "jwt_issue_blocked_total";
    public const string ReasonLabel = "reason";

    /// <summary>Wire-stable reason emitted when
    /// <see cref="PerTenantJwksRotationValidator.EnforceSigningAsync"/>
    /// blocks signing because the tenant's policy has aged past
    /// the configured overlap window.</summary>
    public const string ReasonStalePerTenantPolicy = "stale_per_tenant_policy";

    /// <summary>Wire-stable reason emitted when the validator
    /// has the toggle on but no store registration is present —
    /// the call site treats this as a hard fault and refuses to
    /// sign.</summary>
    public const string ReasonPerTenantStoreMissing = "per_tenant_store_missing";

    private readonly ConcurrentDictionary<string, long> _counter = new(StringComparer.Ordinal);

    /// <summary>Increment the
    /// <c>jwt_issue_blocked_total{reason=&lt;reason&gt;}</c>
    /// counter by one. Null / empty reasons collapse to
    /// <c>"unknown"</c> so the bucket is still observable.</summary>
    public void RecordBlocked(string reason)
    {
        var label = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        _counter.AddOrUpdate(label, 1, (_, prev) => prev + 1);
    }

    /// <summary>Snapshot of the counter buckets — surfaced for
    /// tests + the Prometheus rendering path.</summary>
    public IReadOnlyDictionary<string, long> Snapshot() =>
        _counter.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>Renders the counter in Prometheus exposition
    /// format. HELP + TYPE preambles are emitted unconditionally
    /// so a parser sees the schema even when no blocks have been
    /// recorded yet.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Total JWT-issue calls rejected before signing by the per-tenant JWKS rotation validator. Labelled by `reason` (`stale_per_tenant_policy` / `per_tenant_store_missing`).");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" counter");
        foreach (var kv in _counter)
        {
            sb.Append(MetricName)
              .Append('{').Append(ReasonLabel).Append("=\"")
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
