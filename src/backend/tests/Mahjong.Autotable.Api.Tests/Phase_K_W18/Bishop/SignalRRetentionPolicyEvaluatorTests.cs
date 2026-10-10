using Mahjong.Autotable.Api.Observability;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Bishop;

/// <summary>
/// Phase K Wave 18 — Bishop. Contract tests for the W18 hard-cap
/// enforcement gate (<see cref="SignalRRetentionPolicyEvaluator"/>)
/// + its companion Prometheus counter
/// (<see cref="SignalRRetentionPolicyCappedMetrics"/>). Covers
/// the cap-fires-when-requested-above-ceiling path, the
/// override-allow-list bypass, the synchronous + async façades,
/// the default ceiling (30 days), the configurable ceiling, the
/// floor clamp, the metric snapshot + Prometheus rendering, and
/// the per-tenant cardinality fold.
/// </summary>
public sealed class SignalRRetentionPolicyEvaluatorTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static SignalRRetentionPolicyEvaluator MakeEvaluator(
        out SignalRRetentionPolicyCappedMetrics metrics,
        int? ceilingMinutes = null,
        List<string>? overrides = null,
        ISignalRRetentionPolicyStore? store = null)
    {
        var opts = new SignalRRetentionCeilingOptions
        {
            GlobalCeilingMinutes = ceilingMinutes ?? SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes,
            AllowAboveCeilingTenants = overrides ?? new List<string>(),
        };
        metrics = new SignalRRetentionPolicyCappedMetrics();
        return new SignalRRetentionPolicyEvaluator(opts, metrics, store);
    }

    private static SignalRRetentionPolicy MakePolicy(string tenantId, int minutes) =>
        new() { TenantId = tenantId, RetentionMinutes = minutes };

    // ─── ceiling resolution ────────────────────────────────────

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void DefaultCeiling_Is30Days()
    {
        Assert.Equal(30 * 24 * 60, SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void ConfiguredCeiling_IsRespected()
    {
        var ev = MakeEvaluator(out _, ceilingMinutes: 7 * 24 * 60);
        Assert.Equal(7 * 24 * 60, ev.EffectiveCeilingMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void ZeroCeiling_FallsBackToDefault()
    {
        var ev = MakeEvaluator(out _, ceilingMinutes: 0);
        Assert.Equal(SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes, ev.EffectiveCeilingMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void NegativeCeiling_FallsBackToDefault()
    {
        var ev = MakeEvaluator(out _, ceilingMinutes: -1);
        Assert.Equal(SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes, ev.EffectiveCeilingMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void CeilingBelowFloor_IsClampedUp()
    {
        var ev = MakeEvaluator(out _, ceilingMinutes: 1);
        Assert.Equal(SignalRRetentionPolicyEvaluator.MinCeilingMinutes, ev.EffectiveCeilingMinutes);
    }

    // ─── cap firing ────────────────────────────────────────────

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_PolicyAtCeiling_DoesNotCap()
    {
        var ev = MakeEvaluator(out var m);
        var p = MakePolicy(TenantA, SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes);
        var r = ev.Evaluate(p, SignalRSequenceStoreOptions.DefaultRetentionMinutes);
        Assert.False(r.Capped);
        Assert.Equal(0, m.TotalCapped);
        Assert.Equal(SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes, r.EffectiveMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_PolicyAboveCeiling_Caps()
    {
        var ev = MakeEvaluator(out var m);
        var requested = SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes + 1440;
        var p = MakePolicy(TenantA, requested);
        var r = ev.Evaluate(p, SignalRSequenceStoreOptions.DefaultRetentionMinutes);
        Assert.True(r.Capped);
        Assert.False(r.OverrideApplied);
        Assert.Equal(SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes, r.EffectiveMinutes);
        Assert.Equal(requested, r.RequestedMinutes);
        Assert.Equal(1, m.TotalCapped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_OverrideAllowsAboveCeiling()
    {
        var ev = MakeEvaluator(
            out var m,
            overrides: new List<string> { TenantA });
        var requested = SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes + 7200;
        var p = MakePolicy(TenantA, requested);
        var r = ev.Evaluate(p, SignalRSequenceStoreOptions.DefaultRetentionMinutes);
        Assert.False(r.Capped);
        Assert.True(r.OverrideApplied);
        Assert.Equal(requested, r.EffectiveMinutes);
        Assert.Equal(0, m.TotalCapped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_OverrideOnDifferentTenant_DoesNotBypass()
    {
        var ev = MakeEvaluator(
            out var m,
            overrides: new List<string> { TenantB });
        var requested = SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes + 60;
        var p = MakePolicy(TenantA, requested);
        var r = ev.Evaluate(p, SignalRSequenceStoreOptions.DefaultRetentionMinutes);
        Assert.True(r.Capped);
        Assert.Equal(1, m.TotalCapped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_NullPolicy_UsesGlobalFallback()
    {
        var ev = MakeEvaluator(out _);
        var r = ev.Evaluate(null, 120);
        Assert.Equal(120, r.EffectiveMinutes);
        Assert.False(r.PolicyPresent);
        Assert.False(r.Capped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_NullPolicy_ZeroFallback_UsesDefault()
    {
        var ev = MakeEvaluator(out _);
        var r = ev.Evaluate(null, 0);
        Assert.Equal(SignalRSequenceStoreOptions.DefaultRetentionMinutes, r.EffectiveMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_PolicyZeroRetention_UsesGlobalFallback()
    {
        var ev = MakeEvaluator(out _);
        var p = MakePolicy(TenantA, 0);
        var r = ev.Evaluate(p, 999);
        Assert.Equal(999, r.EffectiveMinutes);
        Assert.True(r.PolicyPresent);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_ConsultsStore()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(MakePolicy(TenantA, 720));
        var ev = MakeEvaluator(out _, store: store);
        var r = await ev.EvaluateAsync(TenantA, 60, CancellationToken.None);
        Assert.Equal(720, r.EffectiveMinutes);
        Assert.True(r.PolicyPresent);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_NoStorePolicy_UsesGlobalFallback()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        var ev = MakeEvaluator(out _, store: store);
        var r = await ev.EvaluateAsync(TenantA, 60, CancellationToken.None);
        Assert.Equal(60, r.EffectiveMinutes);
        Assert.False(r.PolicyPresent);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_StoreAboveCeiling_Caps()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        var requested = SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes + 5760;
        await store.UpsertAsync(MakePolicy(TenantA, requested));
        var ev = MakeEvaluator(out var m, store: store);
        var r = await ev.EvaluateAsync(TenantA, 60, CancellationToken.None);
        Assert.True(r.Capped);
        Assert.Equal(SignalRRetentionPolicyEvaluator.DefaultGlobalCeilingMinutes, r.EffectiveMinutes);
        Assert.Equal(1, m.TotalCapped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluate_NoStoreNoPolicy_NoCap()
    {
        var ev = MakeEvaluator(out var m);
        var r = ev.Evaluate(null, 60);
        Assert.False(r.Capped);
        Assert.Equal(0, m.TotalCapped);
    }

    // ─── override allow-list semantics ─────────────────────────

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void IsAllowedAboveCeiling_EmptyTenant_False()
    {
        var ev = MakeEvaluator(out _);
        Assert.False(ev.IsAllowedAboveCeiling(""));
        Assert.False(ev.IsAllowedAboveCeiling(null!));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void IsAllowedAboveCeiling_CaseSensitive()
    {
        var ev = MakeEvaluator(
            out _,
            overrides: new List<string> { TenantA });
        Assert.True(ev.IsAllowedAboveCeiling(TenantA));
        Assert.False(ev.IsAllowedAboveCeiling("TENANT-A"));
    }

    // ─── metric rendering ──────────────────────────────────────

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Metrics_Snapshot_BreaksDownByTenant()
    {
        var m = new SignalRRetentionPolicyCappedMetrics();
        m.RecordCapped(TenantA, 100, 50);
        m.RecordCapped(TenantA, 200, 50);
        m.RecordCapped(TenantB, 300, 50);
        var snap = m.Snapshot();
        Assert.Equal(2, snap[TenantA]);
        Assert.Equal(1, snap[TenantB]);
        Assert.Equal(3, m.TotalCapped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Metrics_EmptyTenant_FoldsToUnknown()
    {
        var m = new SignalRRetentionPolicyCappedMetrics();
        m.RecordCapped("", 100, 50);
        m.RecordCapped("   ", 200, 50);
        var snap = m.Snapshot();
        Assert.Equal(2, snap["_unknown"]);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Metrics_AppendPrometheus_EmitsHelpType()
    {
        var m = new SignalRRetentionPolicyCappedMetrics();
        var sb = new System.Text.StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("# HELP signalr_retention_policy_capped_total", text);
        Assert.Contains("# TYPE signalr_retention_policy_capped_total counter", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Metrics_AppendPrometheus_EmitsPerTenantRows()
    {
        var m = new SignalRRetentionPolicyCappedMetrics();
        m.RecordCapped(TenantA, 100, 50);
        m.RecordCapped(TenantB, 200, 50);
        var sb = new System.Text.StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("tenant=\"tenant-a\"", text);
        Assert.Contains("tenant=\"tenant-b\"", text);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Metrics_LastRequestedAndCeiling_AreRecorded()
    {
        var m = new SignalRRetentionPolicyCappedMetrics();
        m.RecordCapped(TenantA, 7777, 1234);
        Assert.Equal(7777, m.LastRequestedMinutes);
        Assert.Equal(1234, m.LastCeilingMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void MetricName_IsWireStable()
    {
        Assert.Equal("signalr_retention_policy_capped_total",
            SignalRRetentionPolicyCappedMetrics.MetricName);
        Assert.Equal("tenant", SignalRRetentionPolicyCappedMetrics.TenantLabel);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluator_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SignalRRetentionPolicyEvaluator(
                null!,
                new SignalRRetentionPolicyCappedMetrics()));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Evaluator_NullMetrics_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SignalRRetentionPolicyEvaluator(
                new SignalRRetentionCeilingOptions(),
                null!));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Metrics_Snapshot_IsImmutableSnapshot()
    {
        var m = new SignalRRetentionPolicyCappedMetrics();
        m.RecordCapped(TenantA, 100, 50);
        var snap1 = m.Snapshot();
        m.RecordCapped(TenantA, 100, 50);
        Assert.Equal(1, snap1[TenantA]);
        Assert.Equal(2, m.Snapshot()[TenantA]);
    }
}
