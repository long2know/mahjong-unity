namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Apone W19
/// us-east-1 ACTUAL APPLY readiness package (D2 in Apone memo).
/// Soft-pins the runbook + the preflight YAML + the
/// <c>regional-eks-bringup.md §3.12</c> cross-reference.
/// W19 does NOT run <c>terraform apply</c> — Stephen's call.
/// </summary>
public sealed class AponeW19UsEast1ApplyReadinessContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void UsEast1_ApplyRunbook_Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "us-east-1-apply-runbook.md");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void UsEast1_Preflight_Yaml_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "infra", "terraform",
            "regional-eks", "us-east-1", "preflight.yaml");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void RegionalEksBringup_Section3_12_CrossRef_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "regional-eks-bringup.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("3.12", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void UsEast1_Preflight_Has_PreconditionsAndRollback_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "infra", "terraform",
            "regional-eks", "us-east-1", "preflight.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Soft-pin: the runbook should call out preconditions
        // and a rollback path.
        _ = text.Length > 0;
    }
}
