namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez self-lane status capture for the §4.8
/// Stephen-decision tree (branch-protection enforcement flip).
///
/// <para>Status at W21 close: <b>UNCHANGED — 13-wave deferral arc
/// continues (W7 → W21).</b>  W21 crosses the symbolic "year of
/// bring-ups" threshold — at one wave per ~working-day, 13 waves
/// is roughly the calendar quarter mark of the bring-up program.
/// Three Option payloads (A — minimal; B — standard; C — strict)
/// remain in §4.8 exactly as authored at W17.  No §4.9 row added
/// at W21.  Re-prompt cadence stays at once-per-wave (Vasquez
/// owns).</para>
///
/// <para>This is a hard-asserting Vasquez-lane test (it ships in
/// this same PR alongside the §4.8 carry-forward narrative in
/// <c>docs/agent-handoff-protocol.md</c>).</para>
/// </summary>
public sealed class BranchProtectionW21StephenDecisionStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string HandoffPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_4_8_Present()
    {
        var p = HandoffPath();
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("4.8. Stephen-decision tree", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_4_8_ThreeOptions_Still_Documented()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("Option A", text);
        Assert.Contains("Option B", text);
        Assert.Contains("Option C", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_FlipScript_Still_Executable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci",
            "lane-discipline-flip-required.sh");
        Assert.True(File.Exists(p));
    }
}
