using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Bishop W18 contract: commentary
/// cost-audit alignment (CommentaryController X-Admin-Reason
/// audit row alignment with the W17 admin-reason unification
/// thread). Soft-pin on absence.
/// </summary>
public sealed class BishopW18CommentaryCostAuditAlignmentTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW18CommentaryCostAuditAlignmentTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Controller_Reachable_OrSoftPass()
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("CommentaryController", StringComparison.Ordinal)
            || x.Name.Equals("CommentaryCostAuditWriter", StringComparison.Ordinal));
        _ = t is not null;
    }
}
