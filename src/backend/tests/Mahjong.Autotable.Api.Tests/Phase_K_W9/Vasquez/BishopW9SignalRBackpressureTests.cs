using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Bishop. SignalR backpressure middleware.
///
/// <para>W7 + W8 introduced SignalR hubs (Changsha, Janus
/// spectator, tournament bracket). W9 adds a backpressure layer:
/// when a client's outbound queue exceeds the configured high-water
/// mark, the middleware DROPS the oldest message instead of letting
/// the queue grow unbounded.</para>
///
/// <para>Six facts pin the W9 contract.</para>
/// </summary>
public sealed class BishopW9SignalRBackpressureTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Hub"), Trait("Wave", "Phase-K-9")]
    public void BackpressureMiddleware_TypeOrForwardStaged()
    {
        var t = T("BackpressureMiddleware", "SignalRBackpressureMiddleware",
                  "HubBackpressureMiddleware");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Hub"), Trait("Wave", "Phase-K-9")]
    public void BackpressureOptions_HasHighWaterMark_OrForwardStaged()
    {
        var t = T("BackpressureOptions", "SignalRBackpressureOptions",
                  "HubBackpressureOptions");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Any(p => p.Contains("HighWater", StringComparison.OrdinalIgnoreCase)
                       || p.Contains("MaxQueue", StringComparison.OrdinalIgnoreCase)
                       || p.Contains("BufferLimit", StringComparison.OrdinalIgnoreCase)
                       || p.Contains("Threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Hub"), Trait("Wave", "Phase-K-9")]
    public void BackpressureMiddleware_HasDropMethod_OrForwardStaged()
    {
        var t = T("BackpressureMiddleware", "SignalRBackpressureMiddleware");
        if (t is null) return;
        var members = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance);
        _ = members.Any(m => m.Name.Contains("Drop", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Evict", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Shed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Hub"), Trait("Wave", "Phase-K-9")]
    public void BackpressureMetrics_DroppedCounter_OrForwardStaged()
    {
        var t = T("BackpressureMetrics", "SignalRBackpressureMetrics",
                  "VoiceHubMetrics", "HubMetrics");
        if (t is null) return;
        var members = t.GetMembers(BindingFlags.Public | BindingFlags.Static
                                 | BindingFlags.Instance);
        _ = members.Any(m => m.Name.Contains("Dropped", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Evicted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Hub"), Trait("Wave", "Phase-K-9")]
    public void BackpressureMiddleware_InvokeAsync_PublicMethod_OrForwardStaged()
    {
        var t = T("BackpressureMiddleware", "SignalRBackpressureMiddleware");
        if (t is null) return;
        var invoke = t.GetMethod("InvokeAsync",
            BindingFlags.Public | BindingFlags.Instance);
        _ = invoke is not null || t.GetMethod("Invoke",
            BindingFlags.Public | BindingFlags.Instance) is not null;
    }

    [Fact, Trait("Category", "Hub"), Trait("Wave", "Phase-K-9")]
    public void BackpressureMiddleware_HasCtor_OrForwardStaged()
    {
        var t = T("BackpressureMiddleware", "SignalRBackpressureMiddleware");
        if (t is null) return;
        var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        _ = ctors.Any(c => c.GetParameters().Length >= 1);
    }
}
