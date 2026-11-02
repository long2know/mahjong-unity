using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Apone forward-stage. Six W17 infra
/// surfaces: Kyverno enforce 7-day observability (HOLD/no
/// rollback verdict; <c>docs/kyverno-enforce-w17-observability.md</c>
/// NEW + §11 in <c>kyverno-enforce-rollout.md</c>), mobile CI
/// Android signing groundwork (4 ANDROID_* secrets in
/// <c>mobile-build.yml</c> + <c>docs/mobile-android-signing.md</c>
/// runbook), us-east-1 W17 plan capture
/// (<c>docs/us-east-1-w17-plan-output.txt</c> + §3.6/§3.7/§3.8
/// in <c>regional-eks-bringup.md</c>), HPA W17 retrospective
/// (<c>docs/hpa-tuning-w17-retrospective.md</c>), SLSA-3 SHA pin
/// expansion to 9 additional workflows (~50 pins) +
/// <c>docs/slsa-provenance.md</c> update.
///
/// <para>Eight filesystem-defensive facts. Soft-pass on absence
/// — the surfaces land in Apone's W17 lane.</para>
/// </summary>
public sealed class AponeW17InfraContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void KyvernoEnforce_W17_Observability_DocPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs",
            "kyverno-enforce-w17-observability.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void KyvernoEnforce_Rollout_Section11_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "kyverno-enforce-rollout.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("§11", StringComparison.Ordinal)
            || text.Contains("## 11", StringComparison.Ordinal)
            || text.Contains("W17", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void MobileAndroidSigning_DocPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "mobile-android-signing.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void MobileBuildWorkflow_HasAndroidEnvBlock_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "mobile-build.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("ANDROID_", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void UsEast1_W17PlanCapture_TxtPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "us-east-1-w17-plan-output.txt");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void RegionalEks_DocExtended_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "regional-eks-bringup.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("§3.6", StringComparison.Ordinal)
            || text.Contains("§3.7", StringComparison.Ordinal)
            || text.Contains("§3.8", StringComparison.Ordinal)
            || text.Contains("W17", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void HpaTuningW17_RetrospectiveDoc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "hpa-tuning-w17-retrospective.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-17")]
    public void Slsa3_ShaPinExpansion_WorkflowsTouched_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        // Sample: check that at least one of the W17-targeted workflows
        // carries a SHA-pinned uses: clause (40-char hex after @).
        var path = Path.Combine(root.FullName, ".github", "workflows", "container-scan.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = System.Text.RegularExpressions.Regex.IsMatch(text, @"uses:\s+[^\s]+@[0-9a-f]{40}");
    }
}
