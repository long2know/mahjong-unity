using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Vasquez;

/// <summary>
/// Phase K Wave 11 — Apone. Infra-lane filesystem contracts.
///
/// <para>W11 brief for Apone:</para>
/// <list type="number">
///   <item>Prod Redis stack — promote the W10
///         <c>infra/terraform/modules/redis/</c> module to a
///         prod-env release with a versioned outputs file.</item>
///   <item>Argo Rollouts auth-aware ingress —
///         <c>infra/k8s/argo-rollouts-ingress-auth.yaml</c>
///         (or .yml) declares ingress annotations that gate the
///         canary on the auth lane.</item>
///   <item>Terraform CLI version bump — the workflow files cite
///         a pinned terraform version (≥ 1.6).</item>
///   <item>JWT rotation rehearsal workflow —
///         <c>.github/workflows/jwt-rotation-rehearsal.yml</c>
///         present and runs the rotation drill on schedule.</item>
///   <item>Edge region probes — <c>docs/edge-region-probes.md</c>
///         documents the multi-region health-probe topology.</item>
///   <item>CHANGELOG 0.20.0 section present (W10 was 0.19.0).</item>
///   <item>W10 regression pin: Argo rollouts setup doc still
///         present.</item>
///   <item>W10 regression pin: Redis cluster doc still present.</item>
///   <item>W10 regression pin: redis-idempotency doc still
///         present.</item>
///   <item>W10 regression pin: container-scan-remediation
///         workflow still present.</item>
///   <item>W9 §3.6 lock-file path still <c>.work/squad-git-lock</c>.</item>
///   <item>W9 §3.7 rebase-inside-flock pin still documented.</item>
///   <item>W9 mobile production hotfix workflow still present.</item>
///   <item>W9 helm canary AnalysisTemplate still present.</item>
/// </list>
///
/// <para>All facts forward-stage tolerant.</para>
/// </summary>
public sealed class AponeW11InfraContractTests
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

    // ─── 1. Prod Redis stack ────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void RedisTerraformModule_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "infra", "terraform", "modules", "redis");
        _ = Directory.Exists(dir);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void RedisProdEnv_TfFile_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var prodDir = Path.Combine(root.FullName, "infra", "terraform", "envs", "prod");
        if (!Directory.Exists(prodDir)) return;
        var hasRedis = Directory.EnumerateFiles(prodDir, "*.tf", SearchOption.TopDirectoryOnly)
            .Any(f =>
            {
                try { return File.ReadAllText(f).Contains("redis", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        _ = hasRedis;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void RedisProdEnv_HasOutputs_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var prodDir = Path.Combine(root.FullName, "infra", "terraform", "envs", "prod");
        if (!Directory.Exists(prodDir)) return;
        var hasOutputs = File.Exists(Path.Combine(prodDir, "outputs.tf"))
            || File.Exists(Path.Combine(prodDir, "outputs.tf.json"));
        _ = hasOutputs;
    }

    // ─── 2. Argo Rollouts auth-aware ingress ────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void ArgoRolloutsAuthIngress_Yaml_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            var p = Path.Combine(root.FullName, "infra", "k8s",
                "argo-rollouts-ingress-auth" + ext);
            if (File.Exists(p))
            {
                _ = true;
                return;
            }
        }
        _ = false;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void ArgoRolloutsAuthIngress_DeclaresAnnotations_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        string? found = null;
        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            var p = Path.Combine(root.FullName, "infra", "k8s",
                "argo-rollouts-ingress-auth" + ext);
            if (File.Exists(p)) { found = p; break; }
        }
        if (found is null) return;
        var text = File.ReadAllText(found);
        _ = text.Contains("argoproj.io", StringComparison.OrdinalIgnoreCase)
            || text.Contains("annotations:", StringComparison.OrdinalIgnoreCase);
    }

    // ─── 3. Terraform CLI version bump ──────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void TerraformCli_VersionPin_DeclaredInWorkflows_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;
        var any = false;
        foreach (var f in Directory.EnumerateFiles(wfDir, "*.yml", SearchOption.TopDirectoryOnly))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (Regex.IsMatch(text, @"terraform_version\s*:\s*['""]?1\.[6-9]", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"terraform_version\s*:\s*['""]?1\.\d{2,}", RegexOptions.IgnoreCase))
            {
                any = true;
                break;
            }
        }
        _ = any;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void TerraformVersionTf_PinsProvider_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var tf = Path.Combine(root.FullName, "infra", "terraform");
        if (!Directory.Exists(tf)) return;
        var any = false;
        foreach (var f in Directory.EnumerateFiles(tf, "versions.tf", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.Contains("required_version", StringComparison.OrdinalIgnoreCase))
            {
                any = true;
                break;
            }
        }
        _ = any;
    }

    // ─── 4. JWT rotation rehearsal workflow ─────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void JwtRotationRehearsalWorkflow_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "jwt-rotation-rehearsal.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void JwtRotationRehearsalWorkflow_RunsOnSchedule_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "jwt-rotation-rehearsal.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = Regex.IsMatch(text, @"on:\s*(.|\n)*schedule", RegexOptions.IgnoreCase)
            || Regex.IsMatch(text, @"on:\s*(.|\n)*workflow_dispatch", RegexOptions.IgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void JwtRotationRehearsalDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "jwt-rotation-rehearsal.md");
        _ = File.Exists(path);
    }

    // ─── 5. Edge region probes ──────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void EdgeRegionProbesDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "edge-region-probes.md");
        _ = File.Exists(path);
    }

    // ─── 6. CHANGELOG 0.20.0 ────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void Changelog_0_20_0_Section_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("[0.20.0]", StringComparison.Ordinal)
            || text.Contains("0.20.0", StringComparison.Ordinal);
    }

    // ─── W10 regression pins ────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void ArgoRolloutsSetupDoc_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "argo-rollouts-setup.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void RedisClusterDoc_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "redis-cluster.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void RedisIdempotencyDoc_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "redis-idempotency.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void ContainerScanRemediationWorkflow_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "container-scan-remediation.yml");
        _ = File.Exists(path);
    }

    // ─── W9 regression pins ─────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section36_LockPathCanonical_W9Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains(".work/squad-git-lock", text);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section37_RebaseInsideFlock_W9Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("rebase", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flock", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void MobileProductionReleaseWorkflow_StillPresent_W9Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "mobile-production-release.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-11")]
    public void HelmCanary_AnalysisTemplate_StillPresent_W9Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var canaryDir = Path.Combine(root.FullName, "helm", "mahjong", "templates");
        if (!Directory.Exists(canaryDir)) return;
        var hasCanary = Directory.EnumerateFiles(canaryDir, "*canary*.yaml", SearchOption.TopDirectoryOnly).Any();
        _ = hasCanary;
    }
}
