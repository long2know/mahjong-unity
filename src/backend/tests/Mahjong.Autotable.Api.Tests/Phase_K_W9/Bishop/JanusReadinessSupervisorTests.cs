using Mahjong.Autotable.Api.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Bishop;

/// <summary>
/// Phase K Wave 9 — Bishop. Deterministic state-machine tests for
/// <see cref="JanusReadinessSupervisor"/>. Drives the supervisor via
/// the internal <c>OnProbeResultAsync</c> entry point so the state
/// transitions can be asserted without spinning up a real probe loop
/// or HTTP gateway.
///
/// <list type="number">
///   <item>Initial state is <c>Unknown</c>.</item>
///   <item>First healthy probe flips Unknown → Bound (cold start).</item>
///   <item>Six consecutive failures flip Bound → Unbound.</item>
///   <item>One failure mid-sequence does NOT flip the state.</item>
///   <item>A success resets the failure counter.</item>
///   <item>Once Unbound, six consecutive successes flip to Bound.</item>
///   <item>State holds across cancellation token resets.</item>
///   <item>Default poll interval is 5 seconds.</item>
///   <item>The unbind / rebind thresholds are 6.</item>
/// </list>
/// </summary>
public sealed class JanusReadinessSupervisorTests
{
    private static JanusReadinessSupervisor New() => new(
        new StubProbe(),
        NullLogger<JanusReadinessSupervisor>.Instance);

    private static JanusHealthResult Healthy() =>
        new(true, "janus-gateway", "1.2.0", null);

    private static JanusHealthResult Unhealthy(string err = "timeout") =>
        new(false, null, null, err);

    private sealed class StubProbe : IJanusHealthProbe
    {
        public Task<JanusHealthResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new JanusHealthResult(true, null, null, null));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void InitialState_IsUnknown()
    {
        var s = New();
        Assert.Equal(JanusReadinessState.Unknown, s.CurrentState);
        Assert.Null(s.LastProbeResult);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task FirstHealthyProbe_FlipsUnknownToBound()
    {
        var s = New();
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Bound, s.CurrentState);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task FiveConsecutiveFailures_DoesNotUnbind()
    {
        var s = New();
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        for (var i = 0; i < 5; i++)
            await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Bound, s.CurrentState);
        Assert.Equal(5, s.ConsecutiveFailures);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task SixConsecutiveFailures_FlipsBoundToUnbound()
    {
        var s = New();
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        for (var i = 0; i < JanusReadinessSupervisor.UnbindAfterConsecutiveFailures; i++)
            await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Unbound, s.CurrentState);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task SuccessMidSequence_ResetsFailureCounter()
    {
        var s = New();
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        Assert.Equal(3, s.ConsecutiveFailures);
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        Assert.Equal(0, s.ConsecutiveFailures);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task SixConsecutiveSuccesses_AfterUnbound_FlipsBackToBound()
    {
        var s = New();
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        for (var i = 0; i < JanusReadinessSupervisor.UnbindAfterConsecutiveFailures; i++)
            await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Unbound, s.CurrentState);
        for (var i = 0; i < JanusReadinessSupervisor.RebindAfterConsecutiveSuccesses; i++)
            await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Bound, s.CurrentState);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task UnboundStaysUnbound_UntilSixSuccesses()
    {
        var s = New();
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        for (var i = 0; i < JanusReadinessSupervisor.UnbindAfterConsecutiveFailures; i++)
            await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Unbound, s.CurrentState);
        for (var i = 0; i < JanusReadinessSupervisor.RebindAfterConsecutiveSuccesses - 1; i++)
            await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Unbound, s.CurrentState);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task LastProbeResult_IsStamped()
    {
        var s = New();
        var r = Unhealthy("http-503");
        await s.OnProbeResultAsync(r, CancellationToken.None);
        Assert.NotNull(s.LastProbeResult);
        Assert.Equal("http-503", s.LastProbeResult!.Error);
        Assert.False(s.LastProbeResult.IsHealthy);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void DefaultPollInterval_IsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), JanusReadinessSupervisor.DefaultPollInterval);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void UnbindThreshold_IsSix()
    {
        Assert.Equal(6, JanusReadinessSupervisor.UnbindAfterConsecutiveFailures);
        Assert.Equal(6, JanusReadinessSupervisor.RebindAfterConsecutiveSuccesses);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public async Task BoundStateSurvives_MixedHealthSequence()
    {
        var s = New();
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        await s.OnProbeResultAsync(Unhealthy(), CancellationToken.None);
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        await s.OnProbeResultAsync(Healthy(), CancellationToken.None);
        Assert.Equal(JanusReadinessState.Bound, s.CurrentState);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void JanusReadinessHub_AdminGroupName_IsPinned()
    {
        Assert.Equal("janus:readiness:admin", JanusReadinessHub.AdminGroup);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void Constructor_NullProbe_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new JanusReadinessSupervisor(null!, NullLogger<JanusReadinessSupervisor>.Instance));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new JanusReadinessSupervisor(new StubProbe(), null!));
    }
}
