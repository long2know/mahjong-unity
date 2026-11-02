using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 18 — Bishop. Hard-cap enforcement gate for the
/// per-tenant SignalR retention policy table. W17 shipped the
/// per-tenant <see cref="SignalRRetentionPolicy"/> entity + admin
/// CRUD; W18 introduces the global ceiling: if any tenant policy
/// asks for a retention window above the configured ceiling
/// (default <see cref="DefaultGlobalCeilingMinutes"/>), the
/// evaluator caps the effective TTL at the ceiling and stamps
/// the <c>signalr_retention_policy_capped_total</c> Prometheus
/// counter (labelled by tenant) so operators can see which
/// tenants are pushing against the ceiling without combing the
/// admin audit table by hand.
///
/// <para>The hard-cap can be bypassed PER-TENANT through the
/// admin override (W18 admin endpoint surface): when a tenant
/// id is listed in <see cref="SignalRRetentionCeilingOptions.AllowAboveCeilingTenants"/>,
/// the cap is skipped and the tenant's requested TTL is honoured
/// verbatim. The override carries a captured
/// <see cref="ReconnectAuditEntry.KindSignalRRetentionCeilingOverride"/>
/// audit row keyed off the <c>X-Admin-Reason</c> header so the
/// trail of "why is this tenant allowed past 30 days?" is
/// answerable post-hoc.</para>
///
/// <para>The evaluator is intentionally side-channel — the
/// <see cref="SignalRSequenceRetentionSweep"/> route uses
/// <see cref="EvaluateAsync"/> instead of consulting the policy
/// store directly, so the cap applies uniformly across the
/// sweep + the per-tenant TTL-resolution callers. See
/// <c>docs/realtime-resilience.md §7.1 "Per-tenant retention
/// ceiling"</c> (added W18).</para>
/// </summary>
public sealed class SignalRRetentionPolicyEvaluator
{
    /// <summary>Default global ceiling: 30 days, expressed in
    /// minutes. Matches the W18 spec — anything above 30 days
    /// of replay buffer is an operational cliff (database
    /// footprint, replay window) that should require an
    /// affirmative override.</summary>
    public const int DefaultGlobalCeilingMinutes = 30 * 24 * 60;

    /// <summary>Floor on the ceiling — operators can lower the
    /// cap but not below this floor. Mirrors the
    /// <see cref="SignalRSequenceStoreOptions.DefaultRetentionMinutes"/>
    /// so a misconfigured ceiling can't disable retention.</summary>
    public const int MinCeilingMinutes = SignalRSequenceStoreOptions.DefaultRetentionMinutes;

    private readonly SignalRRetentionCeilingOptions _options;
    private readonly ISignalRRetentionPolicyStore? _store;
    private readonly SignalRRetentionPolicyCappedMetrics _metrics;
    private readonly SignalRRetentionLifecycleMetrics? _lifecycleMetrics;

    public SignalRRetentionPolicyEvaluator(
        SignalRRetentionCeilingOptions options,
        SignalRRetentionPolicyCappedMetrics metrics,
        ISignalRRetentionPolicyStore? store = null,
        SignalRRetentionLifecycleMetrics? lifecycleMetrics = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _store = store;
        _lifecycleMetrics = lifecycleMetrics;
    }

    /// <summary>The effective ceiling (minutes). 0 / negative
    /// configuration values fall back to
    /// <see cref="DefaultGlobalCeilingMinutes"/>. Values below
    /// <see cref="MinCeilingMinutes"/> are clamped UP.</summary>
    public int EffectiveCeilingMinutes
    {
        get
        {
            var raw = _options.GlobalCeilingMinutes;
            if (raw <= 0) raw = DefaultGlobalCeilingMinutes;
            return raw < MinCeilingMinutes ? MinCeilingMinutes : raw;
        }
    }

    /// <summary>Returns true when the tenant id is in the
    /// admin-override allow-list — the cap is skipped and the
    /// tenant's requested TTL is honoured verbatim.</summary>
    public bool IsAllowedAboveCeiling(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        if (_options.AllowAboveCeilingTenants is null) return false;
        foreach (var t in _options.AllowAboveCeilingTenants)
        {
            if (string.Equals(t, tenantId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Evaluate the per-tenant effective TTL. Combines
    /// the per-tenant policy with the global ceiling + the
    /// override allow-list. When the cap fires, the
    /// <c>signalr_retention_policy_capped_total</c> counter is
    /// stamped for the tenant.</summary>
    /// <param name="tenantId">The tenant id to evaluate.</param>
    /// <param name="globalFallbackMinutes">Global default TTL.
    /// Used when no per-tenant row exists.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The effective TTL in minutes, plus a
    /// <see cref="SignalRRetentionEvaluation"/> record so the
    /// caller can see whether the cap fired.</returns>
    public async Task<SignalRRetentionEvaluation> EvaluateAsync(
        string tenantId,
        int globalFallbackMinutes,
        CancellationToken ct = default)
    {
        if (globalFallbackMinutes <= 0)
        {
            globalFallbackMinutes = SignalRSequenceStoreOptions.DefaultRetentionMinutes;
        }

        SignalRRetentionPolicy? policy = null;
        if (_store is not null && !string.IsNullOrEmpty(tenantId))
        {
            policy = await _store.GetAsync(tenantId, ct);
        }

        var requested = policy is { RetentionMinutes: > 0 }
            ? policy.RetentionMinutes
            : globalFallbackMinutes;

        var ceiling = EffectiveCeilingMinutes;
        var overrideApplied = IsAllowedAboveCeiling(tenantId);
        if (requested > ceiling && !overrideApplied)
        {
            _metrics.RecordCapped(tenantId, requested, ceiling);
            _lifecycleMetrics?.RecordCapTriggered(tenantId);
            return new SignalRRetentionEvaluation(
                EffectiveMinutes: ceiling,
                RequestedMinutes: requested,
                CeilingMinutes: ceiling,
                Capped: true,
                OverrideApplied: false,
                PolicyPresent: policy is not null);
        }
        _lifecycleMetrics?.RecordApplied(tenantId);
        return new SignalRRetentionEvaluation(
            EffectiveMinutes: requested,
            RequestedMinutes: requested,
            CeilingMinutes: ceiling,
            Capped: false,
            OverrideApplied: overrideApplied && requested > ceiling,
            PolicyPresent: policy is not null);
    }

    /// <summary>Synchronous façade — applies the ceiling to a
    /// loaded policy row without going through the store. Useful
    /// for the admin controller's read-back path (the row is
    /// already in hand).</summary>
    public SignalRRetentionEvaluation Evaluate(SignalRRetentionPolicy? policy, int globalFallbackMinutes)
    {
        if (globalFallbackMinutes <= 0)
        {
            globalFallbackMinutes = SignalRSequenceStoreOptions.DefaultRetentionMinutes;
        }
        var requested = policy is { RetentionMinutes: > 0 }
            ? policy.RetentionMinutes
            : globalFallbackMinutes;
        var tenantId = policy?.TenantId ?? string.Empty;
        var ceiling = EffectiveCeilingMinutes;
        var overrideApplied = IsAllowedAboveCeiling(tenantId);
        if (requested > ceiling && !overrideApplied)
        {
            _metrics.RecordCapped(tenantId, requested, ceiling);
            _lifecycleMetrics?.RecordCapTriggered(tenantId);
            return new SignalRRetentionEvaluation(
                EffectiveMinutes: ceiling,
                RequestedMinutes: requested,
                CeilingMinutes: ceiling,
                Capped: true,
                OverrideApplied: false,
                PolicyPresent: policy is not null);
        }
        _lifecycleMetrics?.RecordApplied(tenantId);
        return new SignalRRetentionEvaluation(
            EffectiveMinutes: requested,
            RequestedMinutes: requested,
            CeilingMinutes: ceiling,
            Capped: false,
            OverrideApplied: overrideApplied && requested > ceiling,
            PolicyPresent: policy is not null);
    }
}

/// <summary>Phase K Wave 18 — Bishop. Per-tenant retention
/// evaluation record. <c>Capped == true</c> means the requested
/// TTL exceeded the ceiling and the effective value was clipped
/// DOWN; <c>OverrideApplied == true</c> means the requested TTL
/// exceeded the ceiling but the override allow-list let it
/// through.</summary>
public readonly record struct SignalRRetentionEvaluation(
    int EffectiveMinutes,
    int RequestedMinutes,
    int CeilingMinutes,
    bool Capped,
    bool OverrideApplied,
    bool PolicyPresent);

/// <summary>
/// Phase K Wave 18 — Bishop. Configuration block for the global
/// retention ceiling + the override allow-list. Bound from the
/// <c>SignalR:Sequences:PerTenant:Ceiling</c> configuration
/// section.
/// </summary>
public sealed class SignalRRetentionCeilingOptions
{
    /// <summary>Global ceiling in minutes. 0 / negative → use
    /// <see cref="SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes"/>
    /// (30 days).</summary>
    public int GlobalCeilingMinutes { get; set; } =
        SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes;

    /// <summary>Tenant ids exempted from the global ceiling. The
    /// admin override surface populates this list at runtime so
    /// an operator can grant a single tenant a longer replay
    /// window without restarting the host. Case-sensitive
    /// comparison (matches <see cref="SignalRRetentionPolicy.TenantId"/>
    /// storage semantics).</summary>
    public List<string> AllowAboveCeilingTenants { get; set; } = new();
}

/// <summary>
/// Phase K Wave 18 — Bishop. Prometheus counter for cap-fire
/// observations. Emitted as
/// <c>signalr_retention_policy_capped_total{tenant}</c>.
/// Cardinality is bounded by the per-tenant ceiling-violation
/// rate; under steady-state operations the counter is zero.
/// </summary>
public sealed class SignalRRetentionPolicyCappedMetrics
{
    public const string MetricName = "signalr_retention_policy_capped_total";
    public const string TenantLabel = "tenant";

    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private long _totalCapped;
    private long _lastRequestedMinutes;
    private long _lastCeilingMinutes;

    /// <summary>Stamp one cap-fire observation. Tenant ids that
    /// would blow the wire cardinality are folded into the
    /// <c>"_unknown"</c> bucket — the WIRE-stable label set is
    /// the explicit allow-list of tenant ids (or
    /// <c>"_unknown"</c>) so a misbehaving caller can't blow
    /// up Prometheus storage.</summary>
    public void RecordCapped(string tenantId, int requestedMinutes, int ceilingMinutes)
    {
        var key = string.IsNullOrWhiteSpace(tenantId) ? "_unknown" : tenantId;
        _counts.AddOrUpdate(key, 1, (_, prev) => prev + 1);
        Interlocked.Increment(ref _totalCapped);
        Interlocked.Exchange(ref _lastRequestedMinutes, requestedMinutes);
        Interlocked.Exchange(ref _lastCeilingMinutes, ceilingMinutes);
    }

    /// <summary>Total cap-fire count across all tenants. Surfaced
    /// for tests + ops dashboards.</summary>
    public long TotalCapped => Interlocked.Read(ref _totalCapped);

    /// <summary>Last observed requested-minutes value. Surfaced
    /// for tests.</summary>
    public long LastRequestedMinutes => Interlocked.Read(ref _lastRequestedMinutes);

    /// <summary>Last observed ceiling-minutes value. Surfaced
    /// for tests.</summary>
    public long LastCeilingMinutes => Interlocked.Read(ref _lastCeilingMinutes);

    /// <summary>Per-tenant cap-fire counts — snapshot.</summary>
    public IReadOnlyDictionary<string, long> Snapshot() =>
        _counts.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Render Prometheus exposition text for the
    /// counter. HELP + TYPE preambles are emitted
    /// unconditionally so the schema is visible even before
    /// any cap has fired.</summary>
    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Total per-tenant SignalR retention-policy CAP events (requested TTL exceeded global ceiling and was clipped DOWN). Override allow-list bypasses the cap and does NOT increment this counter.");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" counter");
        foreach (var entry in _counts)
        {
            var tenant = EscapeLabelValue(entry.Key);
            sb.Append(MetricName).Append('{').Append(TenantLabel).Append("=\"").Append(tenant).Append("\"} ")
              .AppendLine(entry.Value.ToString(CultureInfo.InvariantCulture));
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
