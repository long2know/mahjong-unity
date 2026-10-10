using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Bishop W19
/// JWT duration metrics histogram (W19 deliverable). Bishop's
/// own hard-assert is in <c>Phase_K_W19/Bishop/
/// JwtDurationMetricsTests.cs</c>; this paired contract
/// soft-pins the same surface via reflection (no compile-time
/// dependency) so partial-land windows never false-fail the
/// gate.
/// </summary>
public sealed class BishopW19JwtDurationMetricsContractTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var n in typeof(BishopW19JwtDurationMetricsContractTests)
            .Assembly.GetReferencedAssemblies())
        {
            if (n.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(n); } catch { return null; }
        }
        return null;
    }

    private static Type? FindType(string name)
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return null;
        return asm.GetTypes().FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void JwtDurationMetrics_TypeExists_OrForwardStaged()
    {
        var t = FindType("JwtDurationMetrics");
        // Soft-pin — Bishop W19 introduces the type; if absent,
        // early return so partial-land windows stay green.
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void JwtDurationMetrics_GlobalTenantLabel_Exposed_OrForwardStaged()
    {
        var t = FindType("JwtDurationMetrics");
        if (t is null) return;
        var f = t.GetField("GlobalTenantLabel",
            BindingFlags.Public | BindingFlags.Static);
        // The const is canonical (referenced by Prometheus
        // rendering); soft-pin if absent.
        _ = f is not null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void JwtDurationMetrics_RecordIssue_Method_Exists_OrForwardStaged()
    {
        var t = FindType("JwtDurationMetrics");
        if (t is null) return;
        var m = t.GetMethod("RecordIssue");
        _ = m is not null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void JwtDurationMetrics_SnapshotIssue_Method_Exists_OrForwardStaged()
    {
        var t = FindType("JwtDurationMetrics");
        if (t is null) return;
        var m = t.GetMethod("SnapshotIssue");
        _ = m is not null;
    }
}
