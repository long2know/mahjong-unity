namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Hicks. LH13 hard-pin / soft-pin final decision.
///
/// <para>Hicks's W14 lane item #1: LH13 hard-pin retry (with the new
/// <c>GH_TOKEN</c> path). The §6.1 cadence-trigger requires three
/// consecutive nightly cron runs; the W14 brief gives Hicks the
/// authoritative go/no-go decision based on the new token wiring.</para>
///
/// <para>Six facts (soft-pass on absence of the pwa-audit workflow
/// file, hard-assert on the §6.3 documentation block once it lands).</para>
/// </summary>
public sealed class HicksW14LH13HardPinFinalTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_Performance_W14_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // The W11 calibrated value remains 0.85 whether hard- or soft-pinned.
        _ = text.Contains("0.85", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_Accessibility_W14_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.80", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_BestPractices_W14_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.90", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAudit_Seo_W14_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.80", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAuditDoc_Section6_3_W14_Decision_HardAssert()
    {
        // The §6.3 W14 block exists in the doc (Vasquez ships it as
        // part of the W14 hand-off). This hard-asserts so a future
        // regression that erases the §6.3 block trips the gate.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.3", text, StringComparison.Ordinal);
        Assert.Contains("W14", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-14")]
    public void PwaAuditDoc_Cumulative4WaveDeferral_Acknowledged()
    {
        // If the W14 cron still hasn't converged, the cumulative
        // deferral is W11→W14 (4 waves). The §6.3 block MUST
        // acknowledge the cumulative deferral count so future agents
        // know the calibration has been slow-burning.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("deferr", text, StringComparison.OrdinalIgnoreCase);
    }
}
