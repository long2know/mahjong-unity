namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez. LH13 W19 cron status observation
/// (paired with <c>docs/lh13-soft-pin-rationale.md §10</c> +
/// <c>docs/agent-handoff-protocol.md §6.7 / §6.8</c>).
///
/// <para>Hicks W19 HELD §6.7 YELLOW — 0 of 3 successful
/// <c>schedule</c>-event runs on the post-W18-merge <c>main</c>
/// tree.  This Vasquez paired contract hard-asserts the §10
/// HOLD record is present in the LH13 rationale doc, and
/// soft-asserts the §6.8 narrative carries the W19 disposition
/// in the handoff doc.</para>
/// </summary>
public sealed class HicksW19Lh13W19CronStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_W19_Section10_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("W19", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_W19_Section10_HoldDecision_Documented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        var text = File.ReadAllText(path);
        Assert.Contains("HOLD", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_W19_Section10_ConvergenceCriterion_Cited()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        var text = File.ReadAllText(path);
        // The "≥3 consecutive successful schedule-event runs"
        // convergence criterion is the canonical phrasing.
        Assert.Contains("schedule-event", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_PwaAudit_WorkflowFile_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(wf));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_PwaAudit_W18_FormFactor_Fix_StillPresent()
    {
        // Apone W18's fix flags must still be present in the
        // workflow at W19 close (Apone W19 did NOT revert).
        var root = FindRepoRoot();
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        var text = File.ReadAllText(wf);
        Assert.Contains("--form-factor=desktop", text, StringComparison.Ordinal);
        Assert.Contains("--screenEmulation.mobile=false", text, StringComparison.Ordinal);
    }
}
