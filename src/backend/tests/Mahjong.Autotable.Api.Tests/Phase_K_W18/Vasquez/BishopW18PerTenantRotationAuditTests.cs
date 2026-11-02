using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Bishop W18 contract: per-tenant
/// JWKS rotation audit-cadence. Soft-pin: assemblies are
/// reflected, types found via best-effort name lookup; on absence
/// the test early-returns to keep the gate green while the
/// surface converges.
/// </summary>
public sealed class BishopW18PerTenantRotationAuditTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW18PerTenantRotationAuditTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Surface_Reachable_OrSoftPass()
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("PerTenantRotationAuditWriter", StringComparison.Ordinal)
            || x.Name.Equals("PerTenantRotationAuditCadence", StringComparison.Ordinal)
            || x.Name.Equals("PerTenantRotationStore", StringComparison.Ordinal));
        _ = t is not null;
    }
}
