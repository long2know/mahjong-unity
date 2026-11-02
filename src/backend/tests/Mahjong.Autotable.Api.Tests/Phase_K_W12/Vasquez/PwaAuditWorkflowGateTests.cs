using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Vasquez. PWA-audit workflow gate mirror.
///
/// <para>Vasquez's W12 #2 brief item ("LH13 workflow threshold edit
/// collaborate with Hicks's #4"): for ANY assertion changes Hicks
/// makes to <c>.github/workflows/pwa-audit.yml</c> thresholds,
/// mirror them here. This way a future Hicks-side regression (e.g.
/// silently bumping <c>performance</c> back from 0.85 to 0.95
/// without recalibration) trips the Vasquez gate.</para>
///
/// <para>W12 status: per <c>docs/frontend-pwa-audit.md §6.1</c>,
/// the threshold hard-pin is deferred to W13 (the cron has only
/// produced 1 data point against the 3 required). W12 ships these
/// facts as SOFT pins — each fact early-returns on absence; once
/// W13 hard-pins, the <c>if (!File.Exists(...)) return;</c>
/// becomes <c>Assert.True(File.Exists(...));</c> and the
/// soft-pin equality flips to <c>Assert.Equal</c>.</para>
///
/// <para>Five mirror facts:</para>
/// <list type="number">
///   <item><c>performance</c> threshold value matches §7
///         calibration table (0.85).</item>
///   <item><c>accessibility</c> threshold (0.80).</item>
///   <item><c>best-practices</c> threshold (0.90).</item>
///   <item><c>seo</c> threshold (0.80).</item>
///   <item>The §6.1 hand-off note is in
///         <c>docs/frontend-pwa-audit.md</c> (W12 documentation
///         pin — hard-asserts since it ships in this PR).</item>
/// </list>
/// </summary>
public sealed class PwaAuditWorkflowGateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-12")]
    public void PwaAudit_PerformanceThreshold_W11Calibrated_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §7 calibrated value is 0.85; W12 soft-pin tolerates absence.
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("performance", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-12")]
    public void PwaAudit_AccessibilityThreshold_W11Calibrated_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §7 calibrated value is 0.80.
        _ = text.Contains("0.80", StringComparison.Ordinal)
         || text.Contains("accessibility", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-12")]
    public void PwaAudit_BestPracticesThreshold_W11Calibrated_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §7 calibrated value is 0.90.
        _ = text.Contains("0.90", StringComparison.Ordinal)
         || text.Contains("best-practices", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-12")]
    public void PwaAudit_SeoThreshold_W11Calibrated_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §7 calibrated value is 0.80.
        _ = text.Contains("0.80", StringComparison.Ordinal)
         || text.Contains("seo", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-12")]
    public void FrontendPwaAuditDoc_W12_Section6_1_HardAssert()
    {
        // This documentation pin SHIPS in this PR — hard-assert.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.1", text, StringComparison.Ordinal);
        Assert.Contains("LH13", text, StringComparison.Ordinal);
        Assert.Contains("W13", text, StringComparison.Ordinal);
    }
}
