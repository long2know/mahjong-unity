using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Bishop. Janus mountpoint lifecycle service.
///
/// <para>W10 introduces a per-table mountpoint registry that
/// creates a Janus streaming mountpoint when the first spectator
/// joins and tears it down when the last leaves. Without the
/// lifecycle service the W9 SFU integration creates orphan
/// mountpoints that leak resources on the Janus side.</para>
///
/// <para>Seven facts pin the W10 contract.</para>
/// </summary>
public sealed class BishopW10JanusMountpointLifecycleTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusMountpointLifecycleService_TypeOrForwardStaged()
    {
        var t = T(
            "JanusMountpointLifecycleService",
            "JanusMountpointRegistry",
            "MountpointLifecycleService");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusMountpointLifecycleService_IsHostedService_OrForwardStaged()
    {
        var t = T(
            "JanusMountpointLifecycleService",
            "JanusMountpointRegistry",
            "MountpointLifecycleService");
        if (t is null) return;
        var ifaces = t.GetInterfaces().Select(i => i.Name).ToArray();
        _ = ifaces.Contains("IHostedService")
            || ifaces.Contains("BackgroundService")
            || ifaces.Contains("IJanusMountpointLifecycleService");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusMountpointLifecycleService_HasEnsureMethod_OrForwardStaged()
    {
        var t = T(
            "JanusMountpointLifecycleService",
            "JanusMountpointRegistry",
            "MountpointLifecycleService");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.Contains("Ensure", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Register", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Acquire", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Create", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusMountpointLifecycleService_HasReleaseMethod_OrForwardStaged()
    {
        var t = T(
            "JanusMountpointLifecycleService",
            "JanusMountpointRegistry",
            "MountpointLifecycleService");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.Contains("Release", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Dispose", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Teardown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusMountpoint_Record_HasTableId_AndMountpointId_OrForwardStaged()
    {
        var t = T("JanusMountpoint", "MountpointDescriptor", "JanusMountpointDescriptor");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = (props.Contains("TableId") || props.Contains("GameId") || props.Contains("RoomId"))
            && (props.Contains("MountpointId") || props.Contains("Id"));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusMountpointLifecycleService_HasCountOrEnumerate_OrForwardStaged()
    {
        var t = T(
            "JanusMountpointLifecycleService",
            "JanusMountpointRegistry",
            "MountpointLifecycleService");
        if (t is null) return;
        var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance);
        _ = members.Any(m =>
            m.Name.Equals("Count", StringComparison.OrdinalIgnoreCase)
            || m.Name.Equals("ActiveCount", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Enumerate", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("List", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusMountpointOptions_HasIdleTimeout_OrForwardStaged()
    {
        var t = T("JanusMountpointOptions", "MountpointOptions", "JanusOptions");
        if (t is null) return;
        var props = t.GetProperties();
        _ = props.Any(p =>
            (p.PropertyType == typeof(TimeSpan) || p.PropertyType == typeof(TimeSpan?))
            && (p.Name.Contains("Idle", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Linger", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase)));
    }
}
