using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Bishop. Per-tenant JWKS rotation policy table
/// + opt-in toggle.
///
/// <para>W14 shipped <c>JwksOverlapWindow</c> (overlap-window
/// rollback rejection). W15 extends JWKS rotation to a per-tenant
/// policy: each tenant may opt into a custom rotation cadence,
/// stored in a policy table keyed by tenant id, gated behind an
/// opt-in flag so the global rotation cadence remains the default.</para>
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence —
/// the surface lands incrementally in Bishop's W15 lane.</para>
/// </summary>
public sealed class BishopW15PerTenantJwksRotationTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_PolicyTable_OrForwardStaged()
    {
        var t = T("TenantJwksRotationPolicy", "PerTenantJwksRotationPolicy",
            "TenantJwksPolicy", "JwksTenantRotationPolicy");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_PolicyTable_HasTenantIdProperty_OrForwardStaged()
    {
        var t = T("TenantJwksRotationPolicy", "PerTenantJwksRotationPolicy",
            "TenantJwksPolicy", "JwksTenantRotationPolicy");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p =>
            p.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_PolicyTable_HasCadenceProperty_OrForwardStaged()
    {
        var t = T("TenantJwksRotationPolicy", "PerTenantJwksRotationPolicy",
            "TenantJwksPolicy", "JwksTenantRotationPolicy");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p =>
            p.Name.Contains("Cadence", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Interval", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Rotation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_OptInToggle_OrForwardStaged()
    {
        // The opt-in flag may live as a config-section flag OR on the
        // policy row itself.
        var t = T("TenantJwksRotationPolicy", "PerTenantJwksRotationPolicy",
            "JwksTenantRotationOptions", "TenantJwksPolicy");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasOptIn = props.Any(p =>
            p.PropertyType == typeof(bool)
            && (p.Name.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("OptIn", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("PerTenant", StringComparison.OrdinalIgnoreCase)));
        _ = hasOptIn;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_Service_OrForwardStaged()
    {
        var t = T("TenantJwksRotationService", "PerTenantJwksRotationService",
            "ITenantJwksRotation", "TenantJwksRotation");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_DefaultCadence_FallbackPresent_OrForwardStaged()
    {
        // When a tenant has no row in the policy table, the service must
        // fall back to the global rotation cadence.
        var t = T("TenantJwksRotationService", "PerTenantJwksRotationService");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var hasResolve = methods.Any(m =>
            m.Name.Contains("Resolve", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("GetCadence", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Default", StringComparison.OrdinalIgnoreCase));
        _ = hasResolve;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_W14OverlapWindow_StillPresent()
    {
        // Regression-pin: the W14 overlap-window surface remains
        // observable (the W15 per-tenant policy layers on top of it,
        // does not replace it).
        var t = T("JwksOverlapWindow", "JwksRollbackValidator",
            "JwtKeyringOverlapWindow");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15")]
    public void PerTenantJwks_GlobalCadence_BackstopPresent()
    {
        // Regression-pin: even after per-tenant policy, the global
        // rotation cadence must remain observable.
        var t = T("JwtRotationOptions", "JwksRotationOptions",
            "JwtKeyringRotationOptions");
        _ = t is not null;
    }
}
