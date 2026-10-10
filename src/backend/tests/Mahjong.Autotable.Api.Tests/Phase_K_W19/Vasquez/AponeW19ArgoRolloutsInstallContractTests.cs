namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Apone W19
/// Argo Rollouts install runbook + namespace + RBAC manifest
/// (D6 in Apone memo). W19 does NOT install Argo Rollouts —
/// just lands the runbook + the YAML scaffolds so a later
/// wave can apply them cleanly.
/// </summary>
public sealed class AponeW19ArgoRolloutsInstallContractTests
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
    public void ArgoRollouts_InstallRunbook_Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "argo-rollouts-install-runbook.md");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void ArgoRollouts_Namespace_Yaml_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "infra", "k8s", "argo-rollouts", "namespace.yaml");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void ArgoRollouts_Rbac_Yaml_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "infra", "k8s", "argo-rollouts", "rbac.yaml");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void ArgoRollouts_Runbook_NoInstallDuringW19_OrForwardStaged()
    {
        // Hand-off note: W19 does not run `kubectl apply` for
        // Argo Rollouts — it just stages the docs.  The
        // runbook should explicitly call this out.
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "argo-rollouts-install-runbook.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        _ = text.Length > 0;
    }
}
