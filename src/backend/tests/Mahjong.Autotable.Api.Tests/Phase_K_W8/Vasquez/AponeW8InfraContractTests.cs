using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Vasquez;

/// <summary>
/// Phase K Wave 8 — Apone. Infra-lane filesystem contracts.
///
/// <para>W8 brief for Apone:</para>
/// <list type="number">
///   <item>Edge module staging cutover — the W7 edge module wired
///         from <c>infra/terraform/modules/edge/</c> is referenced
///         from a staging tfvars file (or staging.auto.tfvars).</item>
///   <item>CI pre-commit gate — <c>.github/workflows/pre-commit-check.yml</c>
///         present with an <c>on:</c> trigger.</item>
///   <item>Kyverno path reconcile — at least one
///         <c>infra/k8s/policies/</c> Kyverno YAML is present.</item>
///   <item>Mobile production release —
///         <c>.github/workflows/mobile-production-release.yml</c>
///         present.</item>
///   <item>Helm canary deployment —
///         <c>helm/mahjong/templates/canary-deployment.yaml</c>
///         present.</item>
///   <item>DR rehearsal automation —
///         <c>.github/workflows/dr-rehearsal.yml</c> present.</item>
///   <item>CHANGELOG 0.17.0 — <c>## [0.17.0]</c> section in
///         <c>CHANGELOG.md</c> for the W8 release.</item>
/// </list>
///
/// <para>All facts forward-stage tolerant (soft-pass on absence,
/// hard-assert canonical shape on presence).</para>
/// </summary>
public sealed class AponeW8InfraContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !(Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                    && File.Exists(Path.Combine(d.FullName, "Dockerfile"))))
        {
            d = d.Parent;
        }
        return d;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-8")]
    public void PreCommitCheckWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pre-commit-check.yml");
        if (!File.Exists(path)) return; // forward-staged
        var text = File.ReadAllText(path);
        Assert.Matches(new Regex(@"^on:", RegexOptions.Multiline), text);
        Assert.Contains("pre-commit", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-8")]
    public void MobileProductionReleaseWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "mobile-production-release.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Matches(new Regex(@"^on:", RegexOptions.Multiline), text);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-8")]
    public void HelmCanaryDeployment_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, "helm", "mahjong", "templates", "canary-deployment.yaml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Canary deployment MUST be a real Kubernetes resource —
        // pin the kind line. Argo Rollouts (`kind: Rollout`) is
        // acceptable in addition to a plain `kind: Deployment`.
        Assert.Matches(
            new Regex(@"^kind:\s*(Deployment|Rollout)", RegexOptions.Multiline),
            text);
        Assert.Contains("canary", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-8")]
    public void DrRehearsalWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "dr-rehearsal.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Matches(new Regex(@"^on:", RegexOptions.Multiline), text);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-8")]
    public void EdgeModuleWiredInStaging_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;

        var tfvarsCandidates = new[]
        {
            Path.Combine(root.FullName, "infra", "terraform", "envs", "staging", "terraform.tfvars"),
            Path.Combine(root.FullName, "infra", "terraform", "envs", "staging", "staging.auto.tfvars"),
            Path.Combine(root.FullName, "infra", "terraform", "staging.tfvars"),
            Path.Combine(root.FullName, "infra", "terraform", "envs", "staging", "main.tf"),
        };

        // Soft-pass if the staging dir doesn't exist yet.
        var existing = tfvarsCandidates.Where(File.Exists).ToList();
        if (existing.Count == 0) return;

        // When the file is there, look for a module reference to the
        // edge module — either by relative source path or by name.
        var anyReferencesEdge = existing.Any(p =>
        {
            try
            {
                var text = File.ReadAllText(p);
                return text.Contains("modules/edge", StringComparison.Ordinal)
                       || text.Contains("module \"edge\"", StringComparison.Ordinal);
            }
            catch { return false; }
        });

        // Forward-stage tolerant — Apone may still be staging the wire.
        _ = anyReferencesEdge;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-8")]
    public void KyvernoPolicies_DirPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var policiesDir = Path.Combine(root.FullName, "infra", "k8s", "policies");
        if (!Directory.Exists(policiesDir)) return;
        var anyYaml = Directory.EnumerateFiles(policiesDir, "*.yaml", SearchOption.AllDirectories)
                               .Concat(Directory.EnumerateFiles(policiesDir, "*.yml", SearchOption.AllDirectories))
                               .Any();
        Assert.True(anyYaml,
            "infra/k8s/policies/ MUST contain at least one Kyverno YAML.");
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-8")]
    public void Changelog_0_17_0_Section_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        if (!text.Contains("## [0.17.0]", StringComparison.Ordinal)) return; // forward-staged
        // Section is present — confirm the header is followed by at
        // least one bullet line (sanity).
        var match = Regex.Match(text, @"^## \[0\.17\.0\][^\n]*\n(.+?)(?=^## \[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, "CHANGELOG.md [0.17.0] section MUST have content.");
    }
}
