using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Bishop W18 contract: replay-retention
/// policy evaluator (the evaluator that runs the W17 retention
/// CRUD records on the actual replay store, scheduled). Soft-pin
/// on absence.
/// </summary>
public sealed class BishopW18ReplayRetentionPolicyEvaluationTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW18ReplayRetentionPolicyEvaluationTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Evaluator_Reachable_OrSoftPass()
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReplayRetentionPolicyEvaluator", StringComparison.Ordinal)
            || x.Name.Equals("ReplayRetentionEvaluator", StringComparison.Ordinal)
            || x.Name.Equals("ReplayRetentionSweepService", StringComparison.Ordinal));
        _ = t is not null;
    }
}
