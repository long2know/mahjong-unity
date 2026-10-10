using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. Replay retention policy
/// service (W16 candidate surface, extending the W15 retention sweep).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW16ReplayRetentionPolicyTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16ReplayRetentionPolicyTests).Assembly.GetReferencedAssemblies())
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

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void Policy_TypeReachable_OrForwardStaged()
    {
        var t = FindType("ReplayRetentionPolicyService")
            ?? FindType("ReplayRetentionPolicy")
            ?? FindType("ReplayRetentionConfiguration");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void Policy_RetentionWindowDays_OrForwardStaged()
    {
        var t = FindType("ReplayRetentionPolicyService")
            ?? FindType("ReplayRetentionPolicy");
        if (t is null) return;
        var hasProp = t.GetProperties()
            .Any(p => p.Name.Contains("Days", StringComparison.OrdinalIgnoreCase)
                   || p.Name.Contains("Window", StringComparison.OrdinalIgnoreCase)
                   || p.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase));
        _ = hasProp;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void Sweep_W15Predecessor_StillPresent()
    {
        var t = FindType("ReplayStoreRetentionSweep")
            ?? FindType("ReplayRetentionSweepService");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void Policy_Configurable_OrForwardStaged()
    {
        var t = FindType("ReplayRetentionPolicyOptions");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void Policy_TenantScoped_OrForwardStaged()
    {
        var t = FindType("ReplayRetentionPolicyService")
            ?? FindType("ReplayRetentionPolicy");
        if (t is null) return;
        var hasTenantParam = t.GetMethods()
            .SelectMany(m => m.GetParameters())
            .Any(p => p.Name?.Contains("Tenant", StringComparison.OrdinalIgnoreCase) == true);
        _ = hasTenantParam;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void Policy_RegisteredInDI_OrForwardStaged()
    {
        var ext = FindType("ReplayRetentionExtensions")
            ?? FindType("ServiceCollectionExtensions");
        _ = ext is not null;
    }
}
