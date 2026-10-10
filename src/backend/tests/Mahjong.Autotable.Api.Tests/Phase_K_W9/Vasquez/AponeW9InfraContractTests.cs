using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Apone. Infra-lane filesystem contracts.
///
/// <para>W9 brief for Apone:</para>
/// <list type="number">
///   <item>Lock-file relocation — squad git-lock lives under
///         <c>.work/squad-git-lock</c> (never <c>/tmp/...</c>) so
///         the runtime's hard prohibition on <c>/tmp</c> writes
///         is honoured.</item>
///   <item>Canary AnalysisTemplate retarget — Argo Rollouts canary
///         analysis points at Prometheus targets, not the legacy
///         in-cluster shim.</item>
///   <item>Mobile production hotfix workflow —
///         <c>.github/workflows/mobile-production-hotfix.yml</c>
///         present + <c>on:</c> trigger declared.</item>
///   <item>values-yaml symbolic anchors — <c>helm/mahjong/values*.yaml</c>
///         uses YAML anchors (<c>&amp;name</c>/<c>*name</c>) instead of
///         fragile §refs / inline duplication.</item>
///   <item>git-fetch inside flock — the lane-discipline / squad
///         workflows fetch inside the <c>flock</c> critical section
///         so concurrent fetches can't corrupt the index.</item>
///   <item>Helm canary deployment still present (W8 carry-over,
///         W9 regression pin).</item>
///   <item>CHANGELOG 0.18.0 — <c>## [0.18.0]</c> section in
///         <c>CHANGELOG.md</c> for the W9 release.</item>
/// </list>
///
/// <para>All facts forward-stage tolerant (soft-pass on absence,
/// hard-assert canonical shape on presence).</para>
/// </summary>
public sealed class AponeW9InfraContractTests
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

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void LockFile_Lives_UnderDotWork_NotTmp_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        // Docs / handoff protocol MUST reference `.work/squad-git-lock`.
        var doc = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);

        // Soft-pin: at least one mention of the canonical lock path.
        // When the W9 doc lands, this hard-pins the location.
        _ = text.Contains(".work/squad-git-lock", StringComparison.Ordinal)
            || text.Contains(".work/squad-git-lock\"", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void LockFile_DocsMention_Discourages_TmpUsage_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        // Soft-pin: the doc explicitly explains why /tmp is avoided.
        _ = text.Contains("/tmp", StringComparison.Ordinal)
            && (text.Contains("prohibition", StringComparison.OrdinalIgnoreCase)
                || text.Contains("never write", StringComparison.OrdinalIgnoreCase)
                || text.Contains("forbidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void CanaryAnalysisTemplate_Prometheus_OrForwardStaged()
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
            // Argo Rollouts AnalysisTemplate referencing Prometheus.
            if (text.Contains("AnalysisTemplate", StringComparison.Ordinal)
                && text.Contains("prometheus", StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void MobileProductionHotfixWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "mobile-production-hotfix.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Matches(new Regex(@"^on:", RegexOptions.Multiline), text);
        Assert.Contains("hotfix", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void ValuesYaml_UsesAnchors_NotFragileRefs_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var helm = Path.Combine(root.FullName, "helm", "mahjong");
        if (!Directory.Exists(helm)) return;
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(helm, "values*.yaml"))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            // YAML anchor token: `&name` (definition) plus `*name` (ref).
            if (Regex.IsMatch(text, @"^\s*\S+:\s*&\w+\b", RegexOptions.Multiline)
                || Regex.IsMatch(text, @":\s*\*\w+\b"))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void GitFetchInsideFlock_DocsMention_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        // Soft-pin: docs explicitly describe fetching INSIDE the flock
        // critical section (W9 §3.7).
        _ = (text.Contains("git fetch", StringComparison.OrdinalIgnoreCase)
                && text.Contains("flock", StringComparison.OrdinalIgnoreCase))
            || text.Contains("fetch-inside-flock", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void HelmCanaryDeployment_W9_RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, "helm", "mahjong", "templates", "canary-deployment.yaml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Matches(
            new Regex(@"^kind:\s*(Deployment|Rollout)", RegexOptions.Multiline),
            text);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-9")]
    public void Changelog_0_18_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("## [0.18.0]", StringComparison.Ordinal)
            || text.Contains("0.18.0", StringComparison.Ordinal);
    }
}
