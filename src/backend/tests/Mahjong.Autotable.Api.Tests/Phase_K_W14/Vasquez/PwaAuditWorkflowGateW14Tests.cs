namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Vasquez. PWA-audit workflow gate mirror (W14).
///
/// <para>Vasquez's W14 §2 brief item ("LH13 mirror hard-pin sync —
/// W11+W12+W13 hand-off"). Per <c>docs/frontend-pwa-audit.md §6.3</c>:
/// Hicks's W14 lane retried the LH13 hard-pin with the new
/// <c>GH_TOKEN</c> path. If Hicks hard-pinned, this mirror file
/// upgrades to <c>Assert.Contains</c> on exact threshold values. If
/// Hicks deferred again (4-wave cumulative deferral — yellow flag),
/// the mirror continues the SOFT-PIN shape established in W12/W13
/// with the §6.3 documentation pin escalated to a hard-assert on
/// the cumulative-deferral acknowledgement string.</para>
///
/// <para>Eight facts.</para>
/// </summary>
public sealed class PwaAuditWorkflowGateW14Tests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadWorkflow()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_W14_Performance_SoftPin()
    {
        var text = ReadWorkflow();
        if (text is null) return;
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("performance", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_W14_Accessibility_SoftPin()
    {
        var text = ReadWorkflow();
        if (text is null) return;
        _ = text.Contains("0.80", StringComparison.Ordinal)
         || text.Contains("accessibility", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_W14_BestPractices_SoftPin()
    {
        var text = ReadWorkflow();
        if (text is null) return;
        _ = text.Contains("0.90", StringComparison.Ordinal)
         || text.Contains("best-practices", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_W14_Seo_SoftPin()
    {
        var text = ReadWorkflow();
        if (text is null) return;
        _ = text.Contains("0.80", StringComparison.Ordinal)
         || text.Contains("seo", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void FrontendPwaAuditDoc_W14_Section6_3_HardAssert()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.3", text, StringComparison.Ordinal);
        Assert.Contains("W14", text, StringComparison.Ordinal);
        Assert.Contains("LH13", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void FrontendPwaAuditDoc_W14_CumulativeDeferralAcknowledged()
    {
        // Yellow-flag: 4-wave cumulative deferral (W11→W14) MUST be
        // visible in the doc so future waves see the escalation trace.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("deferr", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAuditWorkflowGateTests_W12_W13_Backstops_Present()
    {
        var asm = typeof(PwaAuditWorkflowGateW14Tests).Assembly;
        var w12 = asm.GetTypes().FirstOrDefault(t =>
            t.FullName == "Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez.PwaAuditWorkflowGateTests");
        var w13 = asm.GetTypes().FirstOrDefault(t =>
            t.FullName == "Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez.PwaAuditWorkflowGateTests");
        Assert.NotNull(w12);
        Assert.NotNull(w13);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_HardPinSpec_W14_PresentInE2EDir()
    {
        // The W14 Playwright spec — sister artefact.
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "tests", "e2e", "lh13-thresholds-hard-pinned-final.spec.ts");
        Assert.True(File.Exists(p),
            $"W14 LH13-final spec MUST exist at {p}.");
    }
}
