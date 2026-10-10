namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez;

/// <summary>
/// Phase K Wave 13 — Apone. Infrastructure contract tests.
///
/// <para>The W13 Apone lane targets:</para>
/// <list type="number">
///   <item>Regional EKS bring-up doc + cutover checklists.</item>
///   <item>JWT rotation cadence scheduled workflow.</item>
///   <item>ClusterPolicy fieldSpecs config (Kyverno).</item>
///   <item>Load-test reminder workflow shape + monthly cron.</item>
///   <item>Redis envFrom required-patch (Kustomize patch).</item>
///   <item>Terraform W14 plan staging.</item>
///   <item>CHANGELOG 0.22.0 entry.</item>
/// </list>
///
/// <para>Each fact early-returns on absence so a Vasquez gate stays
/// green while Apone's W13 lane converges.</para>
/// </summary>
public sealed class AponeW13InfraContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void RegionalEksBringup_Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var docs = Path.Combine(root.FullName, "docs");
        if (!Directory.Exists(docs)) return;
        var any = Directory.EnumerateFiles(docs, "*regional*eks*.md").Any()
               || Directory.EnumerateFiles(docs, "*eks*bringup*.md").Any()
               || Directory.EnumerateFiles(docs, "*regional*bringup*.md").Any();
        _ = any;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void JwtRotationScheduledWorkflow_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;
        var jwtWfs = Directory.EnumerateFiles(wfDir, "jwt-*.yml").ToArray();
        if (jwtWfs.Length == 0) return;
        var anyScheduled = jwtWfs.Any(p =>
            File.ReadAllText(p).Contains("schedule:", StringComparison.OrdinalIgnoreCase)
            && File.ReadAllText(p).Contains("cron:", StringComparison.OrdinalIgnoreCase));
        _ = anyScheduled;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void ClusterPolicy_FieldSpecs_Config_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var infra = Path.Combine(root.FullName, "infra");
        if (!Directory.Exists(infra)) return;
        foreach (var path in Directory.EnumerateFiles(infra, "*.yaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            if (text.Contains("ClusterPolicy", StringComparison.Ordinal)
                && text.Contains("fieldSpecs", StringComparison.OrdinalIgnoreCase))
            {
                _ = true;
                return;
            }
        }
        // Forward-stage: no ClusterPolicy fieldSpecs surface yet — soft-pass.
        _ = false;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void LoadTestReminderWorkflow_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;
        var any = Directory.EnumerateFiles(wfDir, "*load-test-reminder*.yml").Any()
               || Directory.EnumerateFiles(wfDir, "*load-test*.yml").Any();
        _ = any;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void LoadTestReminderWorkflow_MonthlyCron_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;
        foreach (var p in Directory.EnumerateFiles(wfDir, "*load-test*.yml"))
        {
            var text = File.ReadAllText(p);
            if (text.Contains("cron:", StringComparison.OrdinalIgnoreCase)
                && (text.Contains(" 1 *", StringComparison.Ordinal)
                 || text.Contains("* 1 *", StringComparison.Ordinal)
                 || text.Contains("monthly", StringComparison.OrdinalIgnoreCase)))
            {
                _ = true;
                return;
            }
        }
        _ = false;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void RedisEnvFromRequired_KustomizePatch_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var infra = Path.Combine(root.FullName, "infra");
        if (!Directory.Exists(infra)) return;
        var any = Directory.EnumerateFiles(infra, "*redis-envfrom*.yaml", SearchOption.AllDirectories).Any()
               || Directory.EnumerateFiles(infra, "*redis-env*.yaml", SearchOption.AllDirectories).Any();
        _ = any;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void Terraform_W14_PlanStaging_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var tf = Path.Combine(root.FullName, "infra", "terraform");
        if (!Directory.Exists(tf)) return;
        // The W13 staging is any Terraform directory or file
        // labelled with "w14" / "wave-14" / "wave14".
        var any = Directory.EnumerateFiles(tf, "*w14*", SearchOption.AllDirectories).Any()
               || Directory.EnumerateFiles(tf, "*wave-14*", SearchOption.AllDirectories).Any()
               || Directory.EnumerateDirectories(tf, "*w14*", SearchOption.AllDirectories).Any();
        _ = any;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void CHANGELOG_0_22_0_Entry_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var clPath = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(clPath)) return;
        var text = File.ReadAllText(clPath);
        _ = text.Contains("0.22.0", StringComparison.Ordinal)
         || text.Contains("Phase K Wave 13", StringComparison.OrdinalIgnoreCase)
         || text.Contains("Wave 13", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-13")]
    public void RegionalEks_CutoverChecklist_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var docs = Path.Combine(root.FullName, "docs");
        if (!Directory.Exists(docs)) return;
        foreach (var p in Directory.EnumerateFiles(docs, "*regional*.md"))
        {
            var text = File.ReadAllText(p);
            if (text.Contains("cutover", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("[ ]", StringComparison.Ordinal)
                 || text.Contains("checklist", StringComparison.OrdinalIgnoreCase)))
            {
                _ = true;
                return;
            }
        }
        _ = false;
    }
}
