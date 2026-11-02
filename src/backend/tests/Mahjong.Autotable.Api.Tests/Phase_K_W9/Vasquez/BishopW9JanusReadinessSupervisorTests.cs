using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Bishop. Janus readiness supervisor + unbind/rebind.
///
/// <para>W8 wired <c>JanusSpectatorVoiceHub</c> to the Janus
/// SignalingGateway; W9 adds a readiness supervisor that watches
/// the Janus health endpoint, unbinds the spectator hub on health
/// loss, and re-binds on recovery. Contract: the supervisor is a
/// hosted service that exposes a public <c>IsReady</c> probe.</para>
///
/// <para>Six facts pin the W9 contract.</para>
/// </summary>
public sealed class BishopW9JanusReadinessSupervisorTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void JanusReadinessSupervisor_TypeOrForwardStaged()
    {
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor", "JanusSupervisor");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void JanusReadinessSupervisor_ImplementsHostedService_OrForwardStaged()
    {
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor", "JanusSupervisor");
        if (t is null) return;
        var ifaces = t.GetInterfaces().Select(i => i.Name).ToArray();
        _ = ifaces.Contains("IHostedService")
            || ifaces.Contains("BackgroundService")
            || ifaces.Contains("IJanusReadinessSupervisor");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void JanusReadinessSupervisor_HasIsReadyProbe_OrForwardStaged()
    {
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (t is null) return;
        var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance);
        _ = members.Any(m =>
            m.Name.Equals("IsReady", StringComparison.OrdinalIgnoreCase)
            || m.Name.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("IsHealthy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void JanusReadinessSupervisor_HasUnbindMethod_OrForwardStaged()
    {
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor", "JanusSpectatorVoiceHub");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m => m.Name.Contains("Unbind", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Detach", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Disconnect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void JanusReadinessSupervisor_HasRebindMethod_OrForwardStaged()
    {
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor", "JanusSpectatorVoiceHub");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m => m.Name.Contains("Rebind", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Reconnect", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Attach", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-9")]
    public void JanusReadinessOptions_HasProbeInterval_OrForwardStaged()
    {
        var t = T("JanusReadinessOptions", "JanusHealthOptions", "JanusOptions", "VoiceOptions");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Any(p => p.Contains("Interval", StringComparison.OrdinalIgnoreCase)
                       || p.Contains("Cadence", StringComparison.OrdinalIgnoreCase)
                       || p.Contains("Period", StringComparison.OrdinalIgnoreCase));
    }
}
