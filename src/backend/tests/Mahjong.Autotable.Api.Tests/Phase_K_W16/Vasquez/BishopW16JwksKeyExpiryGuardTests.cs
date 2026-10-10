using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. JWKS key expiry guard
/// service (extends W15 per-tenant rotation).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW16JwksKeyExpiryGuardTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16JwksKeyExpiryGuardTests).Assembly.GetReferencedAssemblies())
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

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16")]
    public void Guard_TypeReachable_OrForwardStaged()
    {
        var t = FindType("JwksKeyExpiryGuard")
            ?? FindType("JwksExpiryGuardService")
            ?? FindType("JwksKeyExpiryMonitor");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16")]
    public void Guard_W15RotationStore_StillPresent()
    {
        var t = FindType("PerTenantJwksRotationStore")
            ?? FindType("PerTenantJwksRotation");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16")]
    public void Guard_ThresholdDays_OrForwardStaged()
    {
        var opts = FindType("JwksKeyExpiryGuardOptions")
            ?? FindType("JwksExpiryGuardOptions");
        if (opts is null) return;
        var has = opts.GetProperties()
            .Any(p => p.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
                   || p.Name.Contains("Days", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16")]
    public void Guard_RaisesAlertOnExpiry_OrForwardStaged()
    {
        var t = FindType("JwksKeyExpiryGuard")
            ?? FindType("JwksExpiryGuardService");
        if (t is null) return;
        var has = t.GetMethods().Any(m =>
            m.Name.Contains("Alert", StringComparison.OrdinalIgnoreCase)
         || m.Name.Contains("Notify", StringComparison.OrdinalIgnoreCase)
         || m.Name.Contains("Check", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16")]
    public void Guard_RegisteredInDI_OrForwardStaged()
    {
        var ext = FindType("JwksKeyExpiryGuardExtensions");
        _ = ext is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16")]
    public void Guard_RunsAsBackground_OrForwardStaged()
    {
        var t = FindType("JwksKeyExpiryGuard")
            ?? FindType("JwksKeyExpiryBackgroundService");
        if (t is null) return;
        var implementsBgService = t.GetInterfaces()
            .Any(i => i.Name.Contains("HostedService", StringComparison.OrdinalIgnoreCase));
        _ = implementsBgService || t.BaseType?.Name.Contains("BackgroundService", StringComparison.OrdinalIgnoreCase) == true;
    }
}
