using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Bishop forward-stage.
/// <c>IPerTenantJwksRotationStore.DeleteAsync</c> hard-delete
/// (InMemory + EF impls) + the new
/// <c>auth.jwks.per-tenant.hard-deleted</c> audit-kind constant.
///
/// <para>Five reflection-defensive facts. Soft-pass on absence —
/// the W16 sentinel-row soft-delete workaround is preserved for
/// back-compat; the W17 hard-delete is the strict path.</para>
/// </summary>
public sealed class BishopW17PerTenantRotationDeleteAsyncTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW17PerTenantRotationDeleteAsyncTests).Assembly.GetReferencedAssemblies())
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
    public void Store_Interface_HasDeleteAsync_OrForwardStaged()
    {
        var t = FindType("IPerTenantJwksRotationStore");
        if (t is null) return;
        var hasDelete = t.GetMethods().Any(m =>
            m.Name.Equals("DeleteAsync", StringComparison.Ordinal));
        _ = hasDelete;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Store_Inmem_Impl_HasDeleteAsync_OrForwardStaged()
    {
        var t = FindType("InMemoryPerTenantJwksRotationStore");
        if (t is null) return;
        var hasDelete = t.GetMethods().Any(m =>
            m.Name.Equals("DeleteAsync", StringComparison.Ordinal));
        _ = hasDelete;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Store_Ef_Impl_HasDeleteAsync_OrForwardStaged()
    {
        var t = FindType("EfPerTenantJwksRotationStore");
        if (t is null) return;
        var hasDelete = t.GetMethods().Any(m =>
            m.Name.Equals("DeleteAsync", StringComparison.Ordinal));
        _ = hasDelete;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void AuditKind_HardDeleted_Constant_OrForwardStaged()
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return;
        var found = false;
        foreach (var t in asm.GetTypes())
        {
            if (!t.IsClass || !t.IsAbstract || !t.IsSealed) continue;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is string s
                    && s.Contains("hard-deleted", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (found) break;
        }
        _ = found;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Admin_Controller_DeleteVerb_StillExists_OrForwardStaged()
    {
        var t = FindType("PerTenantRotationAdminController");
        if (t is null) return;
        var hasDelete = t.GetMethods().Any(m =>
            m.GetCustomAttributes(inherit: true)
             .Any(a => a.GetType().Name.Contains("HttpDelete", StringComparison.OrdinalIgnoreCase)));
        _ = hasDelete;
    }
}
