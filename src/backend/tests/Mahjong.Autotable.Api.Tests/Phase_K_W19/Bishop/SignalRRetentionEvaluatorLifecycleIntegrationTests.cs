using Mahjong.Autotable.Api.Observability;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Integration tests asserting that
/// the existing <see cref="SignalRRetentionPolicyEvaluator"/>
/// records both APPLY + CAP-TRIGGERED events into the new
/// <see cref="SignalRRetentionLifecycleMetrics"/> collector
/// when one is wired (and stays a no-op when no collector is
/// wired, preserving W17 + W18 behaviour).
/// </summary>
public sealed class SignalRRetentionEvaluatorLifecycleIntegrationTests
{
    private static SignalRRetentionPolicyEvaluator MakeEvaluator(
        SignalRRetentionLifecycleMetrics? lifecycle = null,
        ISignalRRetentionPolicyStore? store = null,
        int ceilingMinutes = 100)
    {
        var options = new SignalRRetentionCeilingOptions { GlobalCeilingMinutes = ceilingMinutes };
        var cappedMetrics = new SignalRRetentionPolicyCappedMetrics();
        return new SignalRRetentionPolicyEvaluator(options, cappedMetrics, store, lifecycle);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_NormalFlow_RecordsApplied()
    {
        var lifecycle = new SignalRRetentionLifecycleMetrics();
        var evaluator = MakeEvaluator(lifecycle);
        var r = await evaluator.EvaluateAsync("tenant-a", globalFallbackMinutes: 50, CancellationToken.None);
        Assert.False(r.Capped);
        Assert.Equal(1, lifecycle.SnapshotApplied()["tenant-a"]);
        Assert.Empty(lifecycle.SnapshotCapTriggered());
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_RequestExceedsCeiling_RecordsCapTriggered()
    {
        var lifecycle = new SignalRRetentionLifecycleMetrics();
        var evaluator = MakeEvaluator(lifecycle, ceilingMinutes: 60);
        // Global fallback 500 > ceiling 60 => cap fires.
        var r = await evaluator.EvaluateAsync("tenant-a", globalFallbackMinutes: 500, CancellationToken.None);
        Assert.True(r.Capped);
        Assert.Equal(1, lifecycle.SnapshotCapTriggered()["tenant-a"]);
        Assert.Empty(lifecycle.SnapshotApplied());
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_NullLifecycleMetrics_IsNoOp()
    {
        // No lifecycle metrics wired => evaluator must still
        // function (preserves W17 / W18 behaviour).
        var evaluator = MakeEvaluator(lifecycle: null);
        var r = await evaluator.EvaluateAsync("tenant-a", globalFallbackMinutes: 50, CancellationToken.None);
        Assert.False(r.Capped);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Evaluate_SyncFlow_RecordsApplied()
    {
        var lifecycle = new SignalRRetentionLifecycleMetrics();
        var evaluator = MakeEvaluator(lifecycle);
        var r = evaluator.Evaluate(new SignalRRetentionPolicy
        {
            TenantId = "tenant-x",
            RetentionMinutes = 30,
        }, globalFallbackMinutes: 50);
        Assert.False(r.Capped);
        Assert.Equal(1, lifecycle.SnapshotApplied()["tenant-x"]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Evaluate_SyncFlow_CapTriggered()
    {
        var lifecycle = new SignalRRetentionLifecycleMetrics();
        var evaluator = MakeEvaluator(lifecycle, ceilingMinutes: 60);
        var r = evaluator.Evaluate(new SignalRRetentionPolicy
        {
            TenantId = "tenant-x",
            RetentionMinutes = 1000,
        }, globalFallbackMinutes: 50);
        Assert.True(r.Capped);
        Assert.Equal(1, lifecycle.SnapshotCapTriggered()["tenant-x"]);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_AccumulatesAcrossMultipleCalls()
    {
        var lifecycle = new SignalRRetentionLifecycleMetrics();
        var evaluator = MakeEvaluator(lifecycle);
        await evaluator.EvaluateAsync("tenant-a", 10, CancellationToken.None);
        await evaluator.EvaluateAsync("tenant-a", 10, CancellationToken.None);
        await evaluator.EvaluateAsync("tenant-b", 10, CancellationToken.None);
        Assert.Equal(2, lifecycle.SnapshotApplied()["tenant-a"]);
        Assert.Equal(1, lifecycle.SnapshotApplied()["tenant-b"]);
    }
}
