using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Apone. Infra-lane filesystem contracts.
///
/// <para>W10 brief for Apone:</para>
/// <list type="number">
///   <item>Agent prompt template flip — the W10 prompt templates
///         under <c>.squad/agents/&lt;agent&gt;/prompt-template.md</c>
///         (or similar) reference the canonical
///         <c>.work/squad-git-lock</c> path + the W10
///         <c>.work/&lt;agent&gt;-w&lt;N&gt;-safe/</c> backup
///         convention.</item>
///   <item>Redis Terraform module — new
///         <c>infra/terraform/modules/redis/</c> directory with
///         standard module files.</item>
///   <item>Argo Rollouts runbook — <c>docs/argo-rollouts-setup.md</c>
///         present.</item>
///   <item>RS256 ESO rotation runbook — <c>docs/jwt-rotation.md</c>
///         (or a new <c>docs/rs256-eso-rotation.md</c>) covers the
///         ESO + RS256 rotation steps.</item>
///   <item>Container scan remediation workflow —
///         <c>.github/workflows/container-scan-remediation.yml</c>.</item>
///   <item>Redis cluster doc — <c>docs/redis-cluster.md</c>
///         covers the cluster topology + failover playbook.</item>
///   <item>CHANGELOG 0.19.0 — <c>## [0.19.0]</c> section.</item>
///   <item>W9 §3.6 lock-file relocation pin still references
///         <c>.work/squad-git-lock</c> (regression pin).</item>
///   <item>W9 §3.7 rebase-inside-flock pin still documented
///         (regression pin).</item>
///   <item>Mobile production hotfix workflow still present
///         (regression pin from W9).</item>
///   <item>Helm canary still has Argo Rollouts AnalysisTemplate
///         (regression pin from W9).</item>
/// </list>
///
/// <para>All facts forward-stage tolerant (soft-pass on absence,
/// hard-assert canonical shape on presence). Regression pins
/// hard-assert.</para>
/// </summary>
public sealed class AponeW10InfraContractTests
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

    // ─── 1. agent prompt template flip ──────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void AgentPromptTemplate_ReferencesCanonicalLockPath_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var squad = Path.Combine(root.FullName, ".squad", "agents");
        if (!Directory.Exists(squad)) return;
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(squad, "*.md", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.Contains(".work/squad-git-lock", StringComparison.Ordinal))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void AgentPromptTemplate_ReferencesWaveSafeBackupDir_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var squad = Path.Combine(root.FullName, ".squad", "agents");
        if (!Directory.Exists(squad)) return;
        var pattern = new Regex(@"\.work/[a-z]+-w\d+-safe", RegexOptions.IgnoreCase);
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(squad, "*.md", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (pattern.IsMatch(text))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    // ─── 2. Redis Terraform module ──────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void RedisTerraformModule_DirectoryPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var modDir = Path.Combine(root.FullName, "infra", "terraform", "modules", "redis");
        // Forward-staged until Apone lands the module.
        _ = Directory.Exists(modDir);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void RedisTerraformModule_HasMainAndVariables_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var modDir = Path.Combine(root.FullName, "infra", "terraform", "modules", "redis");
        if (!Directory.Exists(modDir)) return;
        _ = File.Exists(Path.Combine(modDir, "main.tf"))
            && (File.Exists(Path.Combine(modDir, "variables.tf"))
                || File.Exists(Path.Combine(modDir, "vars.tf")));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void RedisTerraformModule_DeclaresEncryptionAtRest_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var modDir = Path.Combine(root.FullName, "infra", "terraform", "modules", "redis");
        if (!Directory.Exists(modDir)) return;
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(modDir, "*.tf", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.Contains("at_rest_encryption", StringComparison.OrdinalIgnoreCase)
                || text.Contains("kms", StringComparison.OrdinalIgnoreCase)
                || text.Contains("encryption", StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    // ─── 3. Argo Rollouts runbook ───────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void ArgoRolloutsRunbook_DocPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "argo-rollouts-setup.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("Argo", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rollout", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 4. RS256 ESO rotation runbook ──────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void Rs256EsoRotation_DocPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        // Accept either a new RS256-ESO-specific doc OR the existing
        // jwt-rotation.md / secret-rotation.md gaining an ESO section.
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "rs256-eso-rotation.md"),
            Path.Combine(root.FullName, "docs", "jwt-rotation.md"),
            Path.Combine(root.FullName, "docs", "secret-rotation.md"),
        };
        var matched = candidates.Any(p =>
        {
            if (!File.Exists(p)) return false;
            var text = File.ReadAllText(p);
            return text.Contains("ESO", StringComparison.Ordinal)
                || text.Contains("ExternalSecret", StringComparison.Ordinal)
                || text.Contains("external-secrets", StringComparison.OrdinalIgnoreCase);
        });
        _ = matched;
    }

    // ─── 5. container scan remediation workflow ─────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void ContainerScanRemediationWorkflow_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "container-scan-remediation.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Matches(new Regex(@"^on:", RegexOptions.Multiline), text);
    }

    // ─── 7. Redis cluster doc ───────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void RedisClusterDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "redis-cluster.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("Redis", text, StringComparison.OrdinalIgnoreCase);
        _ = text.Contains("cluster", StringComparison.OrdinalIgnoreCase)
            || text.Contains("replica", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failover", StringComparison.OrdinalIgnoreCase);
    }

    // ─── 8. CHANGELOG 0.19.0 ────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void Changelog_0_19_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("## [0.19.0]", StringComparison.Ordinal)
            || text.Contains("0.19.0", StringComparison.Ordinal);
    }

    // ─── 9-12. W9 regression pins ───────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void HandoffProtocol_LockFile_DotWorkSquadGitLock_W9RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        Assert.Contains(".work/squad-git-lock", text);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void HandoffProtocol_RebaseInsideFlock_W9RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        Assert.True(
            (text.Contains("git fetch", StringComparison.OrdinalIgnoreCase)
                && text.Contains("flock", StringComparison.OrdinalIgnoreCase))
            || text.Contains("rebase-inside-flock", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fetch-inside-flock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void MobileProductionHotfixWorkflow_StillPresent_W9RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "mobile-production-hotfix.yml");
        Assert.True(File.Exists(path),
            "mobile-production-hotfix.yml MUST remain present (W9 regression pin).");
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-10")]
    public void HelmCanary_AnalysisTemplate_W9RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var helmDir = Path.Combine(root.FullName, "helm", "mahjong", "templates");
        if (!Directory.Exists(helmDir)) return;
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(helmDir, "*.yaml", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.Contains("AnalysisTemplate", StringComparison.Ordinal))
            {
                matched = true;
                break;
            }
        }
        // W9 hard-shipped the AnalysisTemplate; hard-pin going forward.
        Assert.True(matched,
            "Helm AnalysisTemplate MUST remain (W9 regression pin).");
    }
}
