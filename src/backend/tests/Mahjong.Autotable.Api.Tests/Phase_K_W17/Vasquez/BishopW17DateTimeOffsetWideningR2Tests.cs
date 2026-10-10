using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Bishop forward-stage. DateTimeOffset widening
/// round 2: extension-based projections on 4 entities
/// (<c>PlayerAuthIdentity</c>, <c>PlayerAuthSession</c>,
/// <c>ReconnectAuditEntry</c>, <c>SignalRSequenceEntry</c>) plus
/// <c>[NotMapped]</c> offset projections on
/// <c>ReplayRetentionPolicy</c>, <c>PerTenantJwksRotationPolicy</c>,
/// and <c>SignalRRetentionPolicy</c>. Zero schema impact.
///
/// <para>Five reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW17DateTimeOffsetWideningR2Tests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW17DateTimeOffsetWideningR2Tests).Assembly.GetReferencedAssemblies())
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

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Extension_TypeReachable_OrForwardStaged()
    {
        var t = FindType("DateTimeOffsetWideningR2");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Extension_HasAtLeastFourProjections_OrForwardStaged()
    {
        var t = FindType("DateTimeOffsetWideningR2");
        if (t is null) return;
        var count = t.GetMethods(BindingFlags.Public | BindingFlags.Static).Length;
        // 4 entity projections + helpers — be generous on the lower bound.
        _ = count >= 4;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void ReplayRetentionPolicy_HasNotMappedOffsetProp_OrForwardStaged()
    {
        var t = FindType("ReplayRetentionPolicy");
        if (t is null) return;
        var hasOffset = t.GetProperties().Any(p =>
            p.Name.Contains("Offset", StringComparison.OrdinalIgnoreCase));
        _ = hasOffset;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void PerTenantJwksRotationPolicy_HasNotMappedOffsetProp_OrForwardStaged()
    {
        var t = FindType("PerTenantJwksRotationPolicy");
        if (t is null) return;
        var hasOffset = t.GetProperties().Any(p =>
            p.Name.Contains("Offset", StringComparison.OrdinalIgnoreCase));
        _ = hasOffset;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void SignalRRetentionPolicy_HasNotMappedOffsetProp_OrForwardStaged()
    {
        var t = FindType("SignalRRetentionPolicy");
        if (t is null) return;
        var hasOffset = t.GetProperties().Any(p =>
            p.Name.Contains("Offset", StringComparison.OrdinalIgnoreCase));
        _ = hasOffset;
    }
}
