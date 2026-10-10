using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 21 — Bishop. Prometheus counter tracking
/// anomalous JWT validation outcomes. The W19 surface added
/// the per-tenant validator-duration histogram; W21 adds a
/// per-tenant per-reason counter so an operator can see a
/// spike in clock-skew / invalid-issuer / expired-too-soon
/// errors without scraping the duration histogram for failure
/// buckets.
///
/// <list type="bullet">
///   <item><c>jwt_validator_anomaly_total{tenant,reason}</c> —
///         counter, incremented when the validator detects an
///         anomalous outcome. The <c>reason</c> label is one of
///         <see cref="ReasonClockSkew"/> /
///         <see cref="ReasonInvalidIssuer"/> /
///         <see cref="ReasonExpiredTooSoon"/>.</item>
/// </list>
///
/// <para>The collector is intentionally side-channel — the
/// validator takes a nullable reference so a test fixture that
/// wires only the validator still works (the recording is a
/// no-op). The MetricsEndpoint renders the counter on every
/// scrape; HELP + TYPE preambles are emitted even when no
/// anomalies have been observed yet so the schema is visible.</para>
/// </summary>
public sealed class JwtValidatorAnomalyMetrics
{
    public const string MetricName = "jwt_validator_anomaly_total";
    public const string TenantLabel = "tenant";
    public const string ReasonLabel = "reason";

    /// <summary>Anomaly: token <c>iat</c> claim falls outside the
    /// 60-second clock-skew tolerance (in either direction).</summary>
    public const string ReasonClockSkew = "clock-skew";

    /// <summary>Anomaly: token's <c>iss</c> claim does not match
    /// the configured issuer for the tenant.</summary>
    public const string ReasonInvalidIssuer = "invalid-issuer";

    /// <summary>Anomaly: token's <c>exp</c> claim already passed
    /// at the moment of validation (a sub-second window).</summary>
    public const string ReasonExpiredTooSoon = "expired-too-soon";

    /// <summary>Wire-name for the empty-tenant bucket.</summary>
    public const string UnknownTenantBucket = "_unknown";

    private readonly ConcurrentDictionary<(string Tenant, string Reason), long> _counters = new();

    /// <summary>Record one anomaly observation.</summary>
    public void Record(string? tenantId, string reason)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? UnknownTenantBucket : tenantId;
        var label = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        _counters.AddOrUpdate((tenant, label), 1, (_, prev) => prev + 1);
    }

    /// <summary>Read the current value for the supplied
    /// (tenant, reason) bucket. 0 when no observations have
    /// been recorded.</summary>
    public long Get(string tenant, string reason) =>
        _counters.TryGetValue(
            (string.IsNullOrEmpty(tenant) ? UnknownTenantBucket : tenant, reason),
            out var v) ? v : 0;

    /// <summary>Snapshot the full counter map.</summary>
    public IReadOnlyDictionary<(string, string), long> Snapshot() =>
        new Dictionary<(string, string), long>(_counters);

    /// <summary>Render the Prometheus exposition.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Total anomalous JWT validation outcomes detected by JwtValidationService. Labelled by `tenant` and `reason` (`clock-skew`, `invalid-issuer`, `expired-too-soon`).");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" counter");
        foreach (var kv in _counters)
        {
            sb.Append(MetricName)
              .Append('{').Append(TenantLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key.Tenant)).Append("\",")
              .Append(ReasonLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key.Reason)).Append("\"} ")
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
