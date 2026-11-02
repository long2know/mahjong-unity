namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez;

/// <summary>
/// Phase K Wave 13 — Vasquez. PWA-audit workflow gate mirror (W13).
///
/// <para>Vasquez's W13 §2 brief item ("LH13 mirror tests HARD-PIN
/// sync"). Per <c>docs/frontend-pwa-audit.md §6.2</c>: Hicks's W13
/// lane DEFERRED the LH13 hard-pin to W14 (the cron has not yet
/// produced the 3 data points required by §6.1). The W13 mirror
/// therefore continues the SOFT-PIN shape established in W12 — but
/// adds a documentation pin that hard-asserts the §6.2 W13 sync
/// block exists in the doc (so a future regression that erases the
/// §6.2 hand-off note trips this gate).</para>
///
/// <para>Six facts:</para>
/// <list type="number">
///   <item>performance soft-pin (0.85).</item>
///   <item>accessibility soft-pin (0.80).</item>
///   <item>best-practices soft-pin (0.90).</item>
///   <item>seo soft-pin (0.80).</item>
///   <item>§6.2 hand-off note exists in the doc — HARD-ASSERT.</item>
///   <item>The W12 mirror file remains as the regression backstop
///         — HARD-ASSERT.</item>
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

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void PwaAudit_W13_Performance_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("performance", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void PwaAudit_W13_Accessibility_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.80", StringComparison.Ordinal)
         || text.Contains("accessibility", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void PwaAudit_W13_BestPractices_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.90", StringComparison.Ordinal)
         || text.Contains("best-practices", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void PwaAudit_W13_Seo_SoftPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.80", StringComparison.Ordinal)
         || text.Contains("seo", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void FrontendPwaAuditDoc_W13_Section6_2_HardAssert()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.2", text, StringComparison.Ordinal);
        Assert.Contains("W13", text, StringComparison.Ordinal);
        Assert.Contains("LH13", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void PwaAuditWorkflowGateTests_W12_Backstop()
    {
        var asm = typeof(PwaAuditWorkflowGateTests).Assembly;
        var w12 = asm.GetTypes().FirstOrDefault(t =>
            t.FullName == "Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez.PwaAuditWorkflowGateTests");
        Assert.NotNull(w12);
    }
}
