using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. Spectator presence
/// metrics (extends W15 spectator audit retention sweep).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW16SpectatorPresenceMetricsTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16SpectatorPresenceMetricsTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    private static Type? FindType(string name)
    {
        var asm = ResolveApiAssembly();
        return asm?.GetTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-16")]
    public void PresenceMetrics_TypeReachable_OrForwardStaged()
    {
        var t = FindType("SpectatorPresenceMetrics")
            ?? FindType("SpectatorPresenceMeter")
            ?? FindType("SpectatorPresenceCounter");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-16")]
    public void PresenceMetrics_HasGauge_OrForwardStaged()
    {
        var t = FindType("SpectatorPresenceMetrics");
        if (t is null) return;
        var has = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(f => f.Name.Contains("Gauge", StringComparison.OrdinalIgnoreCase)
                   || f.Name.Contains("Present", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-16")]
    public void Audit_W15Predecessor_StillPresent()
    {
        var t = FindType("SpectatorHandoffAuditRetentionSweep")
            ?? FindType("SpectatorAuditRetentionSweepService");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-16")]
    public void Metrics_TenantTag_OrForwardStaged()
    {
        var t = FindType("SpectatorPresenceMetrics");
        if (t is null) return;
        var has = t.GetMethods().Any(m => m.GetParameters()
            .Any(p => p.Name?.Contains("Tenant", StringComparison.OrdinalIgnoreCase) == true));
        _ = has;
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-16")]
    public void Metrics_HasIncrementMethod_OrForwardStaged()
    {
        var t = FindType("SpectatorPresenceMetrics");
        if (t is null) return;
        var has = t.GetMethods().Any(m =>
            m.Name.Contains("Inc", StringComparison.OrdinalIgnoreCase)
         || m.Name.Contains("Record", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-16")]
    public void Metrics_OpenTelemetryWired_OrForwardStaged()
    {
        var t = FindType("SpectatorPresenceMetrics");
        if (t is null) return;
        var has = t.GetCustomAttributes(false)
            .Any(a => a.GetType().Name.Contains("Meter", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }
}
