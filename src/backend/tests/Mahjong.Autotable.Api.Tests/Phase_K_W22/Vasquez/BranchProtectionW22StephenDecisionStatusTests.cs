namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez §4.8 Stephen-decision tree
/// 14-wave deferral arc status pin.  Branch protection on `main`
/// is STILL not configured for the lane-discipline check at W22
/// close.  This is a soft-probe carrying the inherited W7→W22
/// deferral arc forward (now in its 14th wave).
/// </summary>
public sealed class BranchProtectionW22StephenDecisionStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_4_8_StephenDecision_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        Assert.Contains("4.8", text);
        Assert.Contains("Stephen", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W22_DeferralArc_14Wave_Recorded()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        // §10.4 forward-stage carry-over should mention the 14-wave
        // deferral arc (W7 → W22).
        var has = text.Contains("14-wave deferral", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("14 waves wide", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W22_Section_4_9_NotOpened()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        // §6.11 + §10 should explicitly confirm "No §4.9 opened" at W22 close.
        var has = text.Contains("No §4.9", StringComparison.Ordinal)
                   || text.Contains("no §4.9", StringComparison.Ordinal)
                   || text.Contains("No 4.9", StringComparison.Ordinal);
        Assert.True(has);
    }
}
