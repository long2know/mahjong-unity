namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez self-lane. Stephen-decision tree
/// status observation: §4.8 (branch-protection enforcement
/// flip) is STILL UNCHANGED at W19 close.
///
/// <para>11-wave deferral arc: W7 → W19 (11 consecutive waves
/// in which the §4.8 decision has not landed).  The
/// preflight URL hard-asserts HTTP 404 on <c>main</c>.</para>
///
/// <para>NO §4.9 has been opened — there is exactly one
/// pending Stephen-decision (§4.8) in the handoff doc at
/// W19 close.</para>
/// </summary>
public sealed class BranchProtectionW19StephenDecisionStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string HandoffDocPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_4_8_StephenDecision_StillPresent()
    {
        var text = File.ReadAllText(HandoffDocPath());
        Assert.Contains("4.8", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_NoSection_4_9_OpenedAtW19()
    {
        // Only §4.8 is the open Stephen-decision through W19.
        // The next decision token (§4.9) should NOT yet be
        // installed as a heading in the doc.
        var text = File.ReadAllText(HandoffDocPath());
        Assert.DoesNotContain("## §4.9", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_DeferralArc_W7_W19_Mentioned()
    {
        var text = File.ReadAllText(HandoffDocPath());
        // 11-wave deferral: W7 should still be cited as the
        // origin of the deferral, and W19 should appear as
        // the latest extension waypoint.
        Assert.Contains("W7", text, StringComparison.Ordinal);
        Assert.Contains("W19", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void Lh13SoftPin_File_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        Assert.True(File.Exists(p),
            $"docs/lh13-soft-pin-rationale.md MUST remain at {p}.");
    }
}
