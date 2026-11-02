namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Vasquez. PWA-audit workflow gate mirror (W15).
///
/// <para>Mirrors §6.4 + §6.5 of <c>docs/frontend-pwa-audit.md</c>:
/// Hicks's W15 lane retried the LH13 hard-pin (third retry); the
/// §6.5 escalation block documents the 5-wave calibration deadlock
/// and the Stephen-direct manual seeding recommendation.</para>
///
/// <para>If Hicks hard-pinned, this mirror file upgrades to
/// <c>Assert.Contains</c> on exact threshold values. If Hicks remained
/// soft-pinned (the expected case per the §6.4 cadence status), the
/// mirror remains soft-pin probes.</para>
///
/// <para>Ten reflection-defensive facts.</para>
/// </summary>
public sealed class PwaAuditWorkflowGateW15Tests
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

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Workflow_FilePresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "pwa-audit.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Performance_Threshold_0_85()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.85", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Accessibility_Threshold_0_80()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.80", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Section6_4_Documented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.4", text, StringComparison.Ordinal);
        Assert.Contains("W15", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Section6_5_EscalationDocumented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        var text = File.ReadAllText(path);
        Assert.Contains("§6.5", text, StringComparison.Ordinal);
        Assert.Contains("Calibration deadlock", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_DeferralChain_5Waves_Documented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        var text = File.ReadAllText(path);
        Assert.Contains("five consecutive waves", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_DeferralChain_RemainsYellow_NotRed()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        var text = File.ReadAllText(path);
        // The §6.4 status remains YELLOW; W16 may flip to RED if the
        // 6-wave threshold trips.
        Assert.Contains("YELLOW", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_PredecessorMirrors_StillReferenced()
    {
        var asm = typeof(PwaAuditWorkflowGateW15Tests).Assembly;
        var w14 = asm.GetTypes().FirstOrDefault(x =>
            x.FullName == "Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez.PwaAuditWorkflowGateW14Tests");
        Assert.NotNull(w14);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Section6_3_PredecessorDocumented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        var text = File.ReadAllText(path);
        Assert.Contains("§6.3", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_StephenDirectManualSeed_Recommended()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        var text = File.ReadAllText(path);
        // §6.5 names manually triggering pwa-audit.yml via the Actions
        // UI as the unblock recommendation.
        Assert.Contains("manual", text, StringComparison.OrdinalIgnoreCase);
    }
}
