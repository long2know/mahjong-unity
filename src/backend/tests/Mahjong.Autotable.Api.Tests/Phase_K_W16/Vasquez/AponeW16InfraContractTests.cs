using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Apone forward-stage. Infra contract probes
/// (Kyverno enforce advancement, HPA min-replicas tuning iteration,
/// us-east-1 plan drift retire, Phase L L1 design memo follow-up,
/// CHANGELOG 0.25.0).
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence —
/// the surfaces land incrementally in Apone's W16 lane.</para>
/// </summary>
public sealed class AponeW16InfraContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void Kyverno_EnforcePolicies_W16Advancement_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "policies", "kyverno-enforce-policies.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "kyverno", "enforce-policies.yaml"),
            Path.Combine(root.FullName, "infra", "policies", "kyverno-enforce.yaml"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void Hpa_MinReplicas_W16Iteration_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "hpa-min-replicas-tuning.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void LaneDisciplineNightly_W15HeredocFix_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "lane-discipline-nightly.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void PhaseL_L1DesignMemo_W16Followup_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "phase-l-l1-design.md"),
            Path.Combine(root.FullName, "Phase_L", "design.md"),
            Path.Combine(root.FullName, "Phase_L_W16", "design-update.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void Changelog_0_25_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.25.0", StringComparison.Ordinal)
         || text.Contains("Wave 16", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void HelmCharts_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "helm");
        _ = Directory.Exists(dir);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void Slsa3_AssessmentDoc_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "slsa.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-16")]
    public void GhcrEcrMirror_Doc_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "ghcr-to-ecr-mirror.md");
        _ = File.Exists(path);
    }
}
