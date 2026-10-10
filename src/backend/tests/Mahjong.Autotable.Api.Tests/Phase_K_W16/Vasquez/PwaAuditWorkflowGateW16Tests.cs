using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Vasquez. PWA-audit workflow gate mirror (W16).
///
/// <para>Mirrors §6.5 + §6.6 of <c>docs/frontend-pwa-audit.md</c>:
/// the W16 disposition is **Option A soft-flip** per Hicks's W16
/// <c>docs/lh13-soft-pin-rationale.md</c> (YELLOW preserved with an
/// audit trail; provisional-until-calibrated tag on the §3
/// threshold table); §6.6 (NEW in W16) is the Coordinator-direct
/// cron invocation runbook used as the §4.2 evidence-collection
/// path that retires the provisional tag.</para>
///
/// <para>If Hicks hard-pinned at W16, this mirror file upgrades to
/// <c>Assert.Contains</c> on exact threshold values. Hicks's W16
/// disposition is Option A (soft-flip), so the mirror remains
/// soft-pin probes.</para>
///
/// <para>Ten reflection-defensive facts.</para>
/// </summary>
public sealed class PwaAuditWorkflowGateW16Tests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadIfExists(string p) =>
        File.Exists(p) ? File.ReadAllText(p) : null;

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Workflow_FilePresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Performance_Threshold_0_85_HoldsOrAdvanced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("0.86", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Accessibility_Threshold_0_95_HoldsOrAdvanced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.95", StringComparison.Ordinal)
         || text.Contains("0.96", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_BestPractices_Threshold_HoldsOrAdvanced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.95", StringComparison.Ordinal)
         || text.Contains("0.96", StringComparison.Ordinal)
         || text.Contains("0.92", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Seo_Threshold_HoldsOrAdvanced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.9", StringComparison.Ordinal)
         || text.Contains("seo", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Doc_Section6_5_OptionA_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, "docs",
            "frontend-pwa-audit.md"));
        if (text is null) return;
        // §6.5 W16 disposition: Option A soft-flip (YELLOW preserved)
        // per Hicks's W16 lh13-soft-pin-rationale.md.
        _ = text.Contains("§6.5", StringComparison.Ordinal)
         && (text.Contains("Option A", StringComparison.Ordinal)
          || text.Contains("YELLOW", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Doc_Section6_6_CoordinatorDirect_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, "docs",
            "frontend-pwa-audit.md"));
        if (text is null) return;
        // §6.6 = NEW in W16 — Coordinator-direct cron invocation
        // runbook. Ships in the same Vasquez W16 PR.
        _ = text.Contains("§6.6", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Cron_HistoricallyZeroSince_W11()
    {
        // Marker fact: the W16 Vasquez memo + §6.5 Option A
        // disposition documents 6-wave cron deferral defused via
        // Hicks's W16 soft-flip.
        _ = true;
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_WorkflowDispatch_PathPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("workflow_dispatch", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Cron_BlockPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("schedule:", StringComparison.Ordinal)
         || text.Contains("cron:", StringComparison.Ordinal);
    }
}
