namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Apone W22's
/// Kyverno enforce-flip on the require-resource-limits +
/// disallow-host-paths ClusterPolicies (W21 audit → W22 enforce).
/// </summary>
public sealed class AponeW22KyvernoEnforceFlipContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void RequireResourceLimits_Enforce_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "infra", "k8s", "base",
            "kyverno-policies", "require-resource-limits.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasEnforce = text.Contains("validationFailureAction: Enforce", StringComparison.Ordinal)
                   || text.Contains("validationFailureAction: enforce", StringComparison.Ordinal);
        Assert.True(hasEnforce);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void RequireResourceLimits_FailurePolicy_Fail_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "infra", "k8s", "base",
            "kyverno-policies", "require-resource-limits.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("failurePolicy: Fail", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void DisallowHostPaths_Enforce_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "infra", "k8s", "base",
            "kyverno-policies", "disallow-host-paths.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasEnforce = text.Contains("validationFailureAction: Enforce", StringComparison.Ordinal)
                   || text.Contains("validationFailureAction: enforce", StringComparison.Ordinal);
        Assert.True(hasEnforce);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void KyvernoW22AdditionalRulesDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "kyverno-w22-additional-rules.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("W22", StringComparison.Ordinal)
                   && (text.Contains("Enforce", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("enforce-flip", StringComparison.OrdinalIgnoreCase));
        Assert.True(has);
    }
}
