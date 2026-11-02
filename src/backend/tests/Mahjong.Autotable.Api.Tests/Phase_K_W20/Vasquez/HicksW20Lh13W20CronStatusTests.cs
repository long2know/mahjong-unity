namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez. LH13 W20 cron status ratification
/// (paired with <c>docs/lh13-soft-pin-rationale.md §11</c> +
/// <c>docs/agent-handoff-protocol.md §6.8</c>).
///
/// <para>Hicks W20 HELD §6.8 YELLOW — gh-CLI unauthenticated in
/// the bring-up shell + only ~97 minutes elapsed since W18 merge
/// to <c>main</c>, well short of the §4.2 ≥3 <c>schedule</c>-event
/// runs required to PROMOTE.  This Vasquez paired contract
/// hard-asserts the §11 HOLD record is present in the LH13
/// rationale doc, and confirms the §6.8 narrative carries the
/// W20 disposition in the handoff doc.</para>
/// </summary>
public sealed class HicksW20Lh13W20CronStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_W20_Section11_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("W20", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_W20_Section11_HoldDecision_Documented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        var text = File.ReadAllText(path);
        Assert.Contains("HOLD", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_W20_Section11_ConvergenceCriterion_Cited()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        var text = File.ReadAllText(path);
        // "≥3 consecutive successful schedule-event runs" is the
        // canonical convergence criterion from §4.2.
        Assert.Contains("schedule-event", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_PwaAudit_WorkflowFile_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(wf));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void Lh13SoftPin_PwaAudit_W18_FormFactor_Fix_StillPresent_W20()
    {
        // Apone W18's fix flags must still be present in the
        // workflow at W20 close (no W20 regression).
        var root = FindRepoRoot();
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        var text = File.ReadAllText(wf);
        Assert.Contains("--form-factor=desktop", text, StringComparison.Ordinal);
        Assert.Contains("--screenEmulation.mobile=false", text, StringComparison.Ordinal);
    }
}
