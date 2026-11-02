using Mahjong.Autotable.Api.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Gradual-degradation contract for
/// <see cref="JanusReadinessSupervisor"/>. W9 shipped a binary
/// Bound / Unbound state machine; operators asked for a richer
/// "Healthy / Degraded / Unhealthy" signal so dashboards can warn
/// before the circuit actually trips.
///
/// <list type="number">
///   <item>Initial level after construction is
///         <see cref="JanusReadinessLevel.Healthy"/>.</item>
///   <item>One healthy probe (cold-start optimisation) keeps the
///         level <see cref="JanusReadinessLevel.Healthy"/>.</item>
///   <item>Two consecutive failures keep the level
///         <see cref="JanusReadinessLevel.Healthy"/> (below the
///         degrade threshold of 3).</item>
///   <item>Three consecutive failures move the level to
///         <see cref="JanusReadinessLevel.Degraded"/> while the
///         binding stays <see cref="JanusReadinessState.Bound"/>
///         (or <see cref="JanusReadinessState.Unknown"/>).</item>
///   <item>Five consecutive failures (still below the unbind
///         threshold of 6) remain
///         <see cref="JanusReadinessLevel.Degraded"/>.</item>
///   <item>Six consecutive failures trip the circuit; level
///         becomes <see cref="JanusReadinessLevel.Unhealthy"/>.</item>
///   <item>A single recovery probe inside the degraded window
///         resets the failure counter, returning the level to
///         <see cref="JanusReadinessLevel.Healthy"/> without
///         requiring a full rebind.</item>
///   <item>After a circuit trip, the level stays
///         <see cref="JanusReadinessLevel.Unhealthy"/> until the
///         rebind threshold (6 successes) is reached.</item>
///   <item>The supervisor's thresholds are exposed as public
///         constants so the dashboard can render them.</item>
///   <item>The <see cref="JanusReadinessLevel"/> enum is exposed
///         (not internal) so admin clients can deserialise it.</item>
/// </list>
/// </summary>
public sealed class JanusReadinessGradualDegradationTests
{
    private sealed class StubProbe : IJanusHealthProbe
    {
        private readonly Func<JanusHealthResult> _next;
        public StubProbe(Func<JanusHealthResult> next) { _next = next; }
        public Task<JanusHealthResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(_next());
    }

    private static JanusReadinessSupervisor NewSupervisor() =>
        new(new StubProbe(() => new JanusHealthResult(false, null, null, "stub")),
            NullLogger<JanusReadinessSupervisor>.Instance,
            hub: null,
            pollInterval: TimeSpan.FromSeconds(5));

    private static readonly JanusHealthResult Healthy = new(true, "janus", "1.2.0", null);
    private static readonly JanusHealthResult Unhealthy = new(false, null, null, "stub-fail");

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void InitialLevel_IsHealthy()
    {
        var s = NewSupervisor();
        Assert.Equal(JanusReadinessLevel.Healthy, s.CurrentLevel);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task SingleHealthyProbe_LevelStaysHealthy()
    {
        var s = NewSupervisor();
        await s.OnProbeResultAsync(Healthy, default);
        Assert.Equal(JanusReadinessLevel.Healthy, s.CurrentLevel);
        Assert.Equal(JanusReadinessState.Bound, s.CurrentState);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task TwoConsecutiveFailures_BelowDegradeThreshold_LevelHealthy()
    {
        var s = NewSupervisor();
        await s.OnProbeResultAsync(Unhealthy, default);
        await s.OnProbeResultAsync(Unhealthy, default);
        Assert.Equal(JanusReadinessLevel.Healthy, s.CurrentLevel);
        Assert.Equal(2, s.ConsecutiveFailures);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task ThreeConsecutiveFailures_LevelDegraded_StateStillBound()
    {
        var s = NewSupervisor();
        for (var i = 0; i < JanusReadinessSupervisor.DegradeAfterConsecutiveFailures; i++)
            await s.OnProbeResultAsync(Unhealthy, default);
        Assert.Equal(JanusReadinessLevel.Degraded, s.CurrentLevel);
        Assert.NotEqual(JanusReadinessState.Unbound, s.CurrentState);
        Assert.Equal(3, s.ConsecutiveFailures);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task FiveConsecutiveFailures_StillDegraded()
    {
        var s = NewSupervisor();
        for (var i = 0; i < 5; i++)
            await s.OnProbeResultAsync(Unhealthy, default);
        Assert.Equal(JanusReadinessLevel.Degraded, s.CurrentLevel);
        Assert.NotEqual(JanusReadinessState.Unbound, s.CurrentState);
        Assert.Equal(5, s.ConsecutiveFailures);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task SixConsecutiveFailures_LevelUnhealthy_StateUnbound()
    {
        var s = NewSupervisor();
        for (var i = 0; i < JanusReadinessSupervisor.UnbindAfterConsecutiveFailures; i++)
            await s.OnProbeResultAsync(Unhealthy, default);
        Assert.Equal(JanusReadinessState.Unbound, s.CurrentState);
        Assert.Equal(JanusReadinessLevel.Unhealthy, s.CurrentLevel);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task SingleRecoveryInsideDegradedWindow_ReturnsLevelToHealthy()
    {
        var s = NewSupervisor();
        for (var i = 0; i < 4; i++)
            await s.OnProbeResultAsync(Unhealthy, default);
        Assert.Equal(JanusReadinessLevel.Degraded, s.CurrentLevel);

        await s.OnProbeResultAsync(Healthy, default);
        Assert.Equal(JanusReadinessLevel.Healthy, s.CurrentLevel);
        Assert.Equal(0, s.ConsecutiveFailures);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task AfterCircuitTrip_StaysUnhealthyUntilRebind()
    {
        var s = NewSupervisor();
        for (var i = 0; i < JanusReadinessSupervisor.UnbindAfterConsecutiveFailures; i++)
            await s.OnProbeResultAsync(Unhealthy, default);
        Assert.Equal(JanusReadinessLevel.Unhealthy, s.CurrentLevel);

        for (var i = 0; i < JanusReadinessSupervisor.RebindAfterConsecutiveSuccesses - 1; i++)
        {
            await s.OnProbeResultAsync(Healthy, default);
            Assert.Equal(JanusReadinessLevel.Unhealthy, s.CurrentLevel);
            Assert.Equal(JanusReadinessState.Unbound, s.CurrentState);
        }

        await s.OnProbeResultAsync(Healthy, default);
        Assert.Equal(JanusReadinessState.Bound, s.CurrentState);
        Assert.Equal(JanusReadinessLevel.Healthy, s.CurrentLevel);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void DegradeAndUnbindThresholds_ArePubliclyExposed()
    {
        Assert.True(JanusReadinessSupervisor.DegradeAfterConsecutiveFailures > 0);
        Assert.True(JanusReadinessSupervisor.UnbindAfterConsecutiveFailures
            > JanusReadinessSupervisor.DegradeAfterConsecutiveFailures,
            "Degrade threshold must precede unbind threshold so operators see a warning before the circuit trips.");
        Assert.Equal(3, JanusReadinessSupervisor.DegradeAfterConsecutiveFailures);
        Assert.Equal(6, JanusReadinessSupervisor.UnbindAfterConsecutiveFailures);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusReadinessLevel_EnumIsPubliclyVisible()
    {
        var t = typeof(JanusReadinessLevel);
        Assert.True(t.IsEnum);
        Assert.True(t.IsPublic);
        var names = Enum.GetNames(t);
        Assert.Contains("Healthy", names);
        Assert.Contains("Degraded", names);
        Assert.Contains("Unhealthy", names);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void Supervisor_ImplementsICurrentLevelInterface()
    {
        var iface = typeof(IJanusReadinessSupervisor);
        var prop = iface.GetProperty("CurrentLevel");
        Assert.NotNull(prop);
        Assert.Equal(typeof(JanusReadinessLevel), prop!.PropertyType);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task LevelDerivation_IndependentOfState_WhenUnbound()
    {
        var s = NewSupervisor();
        for (var i = 0; i < JanusReadinessSupervisor.UnbindAfterConsecutiveFailures; i++)
            await s.OnProbeResultAsync(Unhealthy, default);
        Assert.Equal(JanusReadinessState.Unbound, s.CurrentState);
        Assert.Equal(JanusReadinessLevel.Unhealthy, s.CurrentLevel);
    }
}
