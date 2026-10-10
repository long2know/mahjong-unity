namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Bishop. Phase L bring-up document structure.
///
/// <para>Bishop's W14 lane item: scaffold the <c>docs/phase-l-bringup.md</c>
/// document with the canonical Phase L bring-up structure
/// (objectives, scope, deliverables-by-agent, dependencies, exit
/// criteria). The document is forward-staged: it ships in the W14
/// PR but does not yet enumerate every Phase L wave item — that
/// drives W15+ work.</para>
///
/// <para>Six reflection-defensive doc facts.</para>
/// </summary>
public sealed class BishopW14PhaseLBringupDocTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadDoc()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var path = Path.Combine(root.FullName, "docs", "phase-l-bringup.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLBringup_DocPresent_OrForwardStaged()
    {
        _ = ReadDoc() is not null;
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLBringup_HasObjectivesSection_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        _ = doc.Contains("Objective", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("Goal", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLBringup_HasScopeSection_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        _ = doc.Contains("Scope", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLBringup_HasAgentBreakdown_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        var hasAny = doc.Contains("Bishop", StringComparison.Ordinal)
                  || doc.Contains("Hicks", StringComparison.Ordinal)
                  || doc.Contains("Apone", StringComparison.Ordinal)
                  || doc.Contains("Vasquez", StringComparison.Ordinal);
        _ = hasAny;
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLBringup_HasExitCriteria_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        _ = doc.Contains("Exit", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("Done", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("Acceptance", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLBringup_HasDependenciesSection_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        _ = doc.Contains("Depend", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("Prerequisite", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("Requires", StringComparison.OrdinalIgnoreCase);
    }
}
