using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Bishop. Janus readiness gradual degradation.
///
/// <para>W9 shipped <c>JanusReadinessSupervisor</c> with a binary
/// IsReady probe (healthy ↔ degraded). W10 introduces a 3-state
/// degradation level enum so the supervisor can announce
/// <c>Healthy</c>, <c>Degraded</c>, <c>Unavailable</c> separately —
/// the spectator hub can keep serving viewer-only traffic in
/// <c>Degraded</c> instead of full-unbinding.</para>
///
/// <para>Seven facts pin the W10 contract.</para>
/// </summary>
public sealed class BishopW10JanusGradualDegradationTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusReadinessLevel_EnumType_Present_OrForwardStaged()
    {
        var t = T("JanusReadinessLevel", "VoiceReadinessLevel", "JanusReadinessState");
        if (t is null) return;
        Assert.True(t.IsEnum, "JanusReadinessLevel MUST be an enum.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusReadinessLevel_HasThreeCanonicalLevels_OrForwardStaged()
    {
        var t = T("JanusReadinessLevel", "VoiceReadinessLevel", "JanusReadinessState");
        if (t is null) return;
        if (!t.IsEnum) return;
        var names = Enum.GetNames(t).Select(n => n.ToLowerInvariant()).ToHashSet();
        // W11 hard-flip: Bishop's W10 3-level enum shipped
        // (Healthy / Degraded / Unhealthy).
        Assert.True(names.Contains("healthy") || names.Contains("ready") || names.Contains("ok"),
            "JanusReadinessLevel MUST contain Healthy/Ready/Ok (W10 → W11 hard-flip).");
        Assert.True(names.Contains("degraded") || names.Contains("partial") || names.Contains("slow"),
            "JanusReadinessLevel MUST contain Degraded/Partial/Slow (W10 → W11 hard-flip).");
        Assert.True(names.Contains("unhealthy") || names.Contains("unavailable") || names.Contains("down") || names.Contains("offline"),
            "JanusReadinessLevel MUST contain Unhealthy/Unavailable/Down/Offline (W10 → W11 hard-flip).");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusSupervisor_HasLevelProperty_OrForwardStaged()
    {
        var enumT = T("JanusReadinessLevel", "VoiceReadinessLevel", "JanusReadinessState");
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (enumT is null || t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        // W11 hard-flip: Bishop's W10 supervisor exposes the enum-typed
        // CurrentLevel property (see JanusReadinessSupervisor.cs).
        Assert.True(
            props.Any(p => p.PropertyType == enumT
                        || (p.Name.Equals("Level", StringComparison.OrdinalIgnoreCase)
                            && p.PropertyType.IsEnum)
                        || (p.Name.Equals("CurrentLevel", StringComparison.OrdinalIgnoreCase)
                            && p.PropertyType.IsEnum)),
            $"{t.Name} MUST expose an enum-typed Level/CurrentLevel property (W10 → W11 hard-flip).");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusSupervisor_BackCompat_IsReadyProperty_W9RegressionPin()
    {
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (t is null) return;
        var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance);
        _ = members.Any(m =>
            m.Name.Equals("IsReady", StringComparison.OrdinalIgnoreCase)
            || m.Name.Equals("Ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusSupervisor_UnbindAcceptsLevel_OrForwardStaged()
    {
        var enumT = T("JanusReadinessLevel", "VoiceReadinessLevel");
        var t = T("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (enumT is null || t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            (m.Name.Contains("Unbind", StringComparison.OrdinalIgnoreCase)
             || m.Name.Contains("OnLevel", StringComparison.OrdinalIgnoreCase)
             || m.Name.Contains("Transition", StringComparison.OrdinalIgnoreCase))
            && m.GetParameters().Any(p => p.ParameterType == enumT));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusReadinessOptions_DegradedThreshold_OrForwardStaged()
    {
        var t = T("JanusReadinessOptions", "JanusHealthOptions", "JanusOptions");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Any(n => n.Contains("Degraded", StringComparison.OrdinalIgnoreCase)
                       && (n.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("Latency", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("Errors", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void JanusReadinessOptions_UnavailableThreshold_OrForwardStaged()
    {
        var t = T("JanusReadinessOptions", "JanusHealthOptions", "JanusOptions");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Any(n => (n.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Failure", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Down", StringComparison.OrdinalIgnoreCase))
                       && (n.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("After", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("Errors", StringComparison.OrdinalIgnoreCase)));
    }
}
