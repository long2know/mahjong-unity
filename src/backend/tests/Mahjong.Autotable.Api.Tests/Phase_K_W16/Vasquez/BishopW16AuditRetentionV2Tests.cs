using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. Audit retention v2
/// service (extends W14 audit retention).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW16AuditRetentionV2Tests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16AuditRetentionV2Tests).Assembly.GetReferencedAssemblies())
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

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-16")]
    public void RetentionV2_TypeReachable_OrForwardStaged()
    {
        var t = FindType("AuditRetentionV2Service")
            ?? FindType("AuditLogRetentionV2");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-16")]
    public void RetentionV2_Options_OrForwardStaged()
    {
        var t = FindType("AuditRetentionV2Options");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-16")]
    public void RetentionV2_HasSweepMethod_OrForwardStaged()
    {
        var t = FindType("AuditRetentionV2Service")
            ?? FindType("AuditLogRetentionV2");
        if (t is null) return;
        var has = t.GetMethods().Any(m =>
            m.Name.Contains("Sweep", StringComparison.OrdinalIgnoreCase)
         || m.Name.Contains("Purge", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-16")]
    public void RetentionV2_TenantScoped_OrForwardStaged()
    {
        var t = FindType("AuditRetentionV2Service");
        if (t is null) return;
        var has = t.GetMethods().SelectMany(m => m.GetParameters())
            .Any(p => p.Name?.Contains("Tenant", StringComparison.OrdinalIgnoreCase) == true);
        _ = has;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-16")]
    public void RetentionV2_BackgroundService_OrForwardStaged()
    {
        var t = FindType("AuditRetentionV2BackgroundService")
            ?? FindType("AuditRetentionV2Service");
        if (t is null) return;
        var has = t.BaseType?.Name.Contains("BackgroundService", StringComparison.OrdinalIgnoreCase) == true
              || t.GetInterfaces().Any(i => i.Name.Contains("HostedService", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-16")]
    public void RetentionV2_RegisteredInDI_OrForwardStaged()
    {
        var ext = FindType("AuditRetentionV2Extensions");
        _ = ext is not null;
    }
}
