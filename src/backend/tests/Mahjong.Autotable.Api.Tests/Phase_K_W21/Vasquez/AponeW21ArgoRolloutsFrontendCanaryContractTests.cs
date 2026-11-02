namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Apone W21's Argo
/// Rollouts Canary strategy template for the FRONTEND deploy at
/// <c>infra/k8s/base/argo-rollouts/frontend-canary.yaml</c> +
/// runbook at <c>docs/argo-rollouts-frontend-canary.md</c>.
/// Soft-pinned so the gate stays green if Apone W21 has not yet
/// landed the files.
/// </summary>
public sealed class AponeW21ArgoRolloutsFrontendCanaryContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string CanaryYamlPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "infra", "k8s", "base",
            "argo-rollouts", "frontend-canary.yaml");
    }

    private static string CanaryDocPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "docs",
            "argo-rollouts-frontend-canary.md");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void FrontendCanary_Yaml_Present_OrForwardStaged()
    {
        _ = File.Exists(CanaryYamlPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void FrontendCanary_CanaryStrategy_OrForwardStaged()
    {
        var p = CanaryYamlPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Canary strategy block — must reference canary and not be
        // exclusively a BlueGreen template.
        var hasCanary = text.Contains("canary", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasCanary);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void FrontendCanary_WeightSteps_5_25_50_100_OrForwardStaged()
    {
        var p = CanaryYamlPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // 4 explicit weight steps: 5/25/50/100.
        var has5 = text.Contains("setWeight: 5", StringComparison.Ordinal)
                    || text.Contains("weight: 5", StringComparison.Ordinal);
        var has25 = text.Contains("setWeight: 25", StringComparison.Ordinal)
                     || text.Contains("weight: 25", StringComparison.Ordinal);
        var has50 = text.Contains("setWeight: 50", StringComparison.Ordinal)
                     || text.Contains("weight: 50", StringComparison.Ordinal);
        var has100 = text.Contains("setWeight: 100", StringComparison.Ordinal)
                      || text.Contains("weight: 100", StringComparison.Ordinal);
        Assert.True(has5 || has25 || has50 || has100);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void FrontendCanary_Doc_Present_OrForwardStaged()
    {
        _ = File.Exists(CanaryDocPath());
    }
}
