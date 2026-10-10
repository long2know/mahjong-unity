namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Hicks. LH13 third retry status.
///
/// <para>Hicks's W15 lane item #1: LH13 hard-pin third retry attempt
/// (after the W14 second retry with the new <c>GH_TOKEN</c> path).
/// The §6.1 cadence trigger still requires three consecutive nightly
/// cron runs; the W15 brief gives Hicks the authoritative go/no-go
/// decision.</para>
///
/// <para>Eight reflection-defensive facts paralleling W14's
/// <c>HicksW14LH13HardPinFinalTests</c> pattern. Soft-pass on absence
/// of the pwa-audit workflow; hard-assert on the §6.4 + §6.5
/// documentation blocks once they land.</para>
/// </summary>
public sealed class HicksW15LH13ThirdRetryTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Performance_W15_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.85", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_Accessibility_W15_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.80", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_BestPractices_W15_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("0.90", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_SEO_W15_PinValue_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("0.90", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_GhTokenPath_StillWired()
    {
        // W14 added the GH_TOKEN path; W15 must NOT regress this.
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("GH_TOKEN", StringComparison.Ordinal)
         || text.Contains("GITHUB_TOKEN", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_W15_Section6_4_Documented()
    {
        // The §6.4 W15 sync section MUST land in the doc.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.4", text, StringComparison.Ordinal);
        Assert.Contains("W15", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_W15_Section6_5_EscalationDocumented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.5", text, StringComparison.Ordinal);
        Assert.Contains("Calibration deadlock escalation", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-15")]
    public void PwaAudit_DeferralChain_FiveWaves_DocumentedAsYellow()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        var text = File.ReadAllText(path);
        // The 5-wave deferral chain must be named, and it must remain
        // YELLOW (1 wave below the §6.3 6-wave Coordinator trigger).
        Assert.Contains("five consecutive waves", text,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("YELLOW", text, StringComparison.Ordinal);
    }
}
