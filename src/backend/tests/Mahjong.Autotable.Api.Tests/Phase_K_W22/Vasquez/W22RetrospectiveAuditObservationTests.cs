namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez self-lane observation test for the
/// W21 retrospective audit carry-over.  W21 introduced
/// <c>docs/agent-handoff-protocol.md §9 W21 stash-isolation
/// directive + retrospective audit</c>.  W22 introduces a new
/// top-level <c>§10 W22 retrospective audit</c> codifying the
/// 2nd consecutive 4-for-4 atomic-flock + 2nd consecutive
/// zero-EXECUTION-coord wave milestones.
///
/// <para>This is a hard-asserting Vasquez-lane test (it ships in
/// this same PR alongside the §10 W22 retro audit in
/// <c>docs/agent-handoff-protocol.md</c>).</para>
/// </summary>
public sealed class W22RetrospectiveAuditObservationTests
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

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_9_W21_StashIsolation_Still_Present()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("§9. W21 stash-isolation", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_10_W22Retro_Present()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("§10. W22 retrospective audit", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_TwelfthZeroViolationWaveMilestone_Recorded()
    {
        var text = File.ReadAllText(HandoffPath());
        var has = text.Contains("12th consecutive 0-violation lane wave", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("12th consecutive 0-violation", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_ZeroExecutionCoordWaveStreak_Recorded()
    {
        var text = File.ReadAllText(HandoffPath());
        // 2nd consecutive zero-EXECUTION-coord wave at W22.
        var has = text.Contains("zero-EXECUTION", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("zero EXECUTION", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W22_RatchetLevel_Still_2()
    {
        var text = File.ReadAllText(HandoffPath());
        // Ratchet stays at level 2 (W18 + W19 incidents only; W20,
        // W21, W22 closed without new occurrence).
        var has = text.Contains("ratchet", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("level 2", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
