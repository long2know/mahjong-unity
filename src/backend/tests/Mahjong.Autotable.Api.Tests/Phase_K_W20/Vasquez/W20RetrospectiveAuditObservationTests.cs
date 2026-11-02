namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez self-lane.  W20 retrospective audit
/// observations — confirm the audit subsection lands in
/// <c>docs/agent-handoff-protocol.md</c> with per-agent stash /
/// add discipline compliance records for Apone (bc775b9),
/// Hicks (107afb7), Bishop (9e7d797), and the Vasquez bring-up
/// commit itself.
/// </summary>
public sealed class W20RetrospectiveAuditObservationTests
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

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_W20_RetrospectiveAudit_Subsection_Present()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("W20 retrospective audit", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_W20_RetrospectiveAudit_References_PriorThreeCommits()
    {
        var text = File.ReadAllText(HandoffPath());
        // The retrospective audit names the three prior W20 commits.
        Assert.Contains("bc775b9", text, StringComparison.Ordinal);
        Assert.Contains("107afb7", text, StringComparison.Ordinal);
        Assert.Contains("9e7d797", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_W20_RetrospectiveAudit_StashAddDiscipline_Vocabulary()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("Stash-ONCE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Explicit-add", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Single-lane", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_W20_Retrospective_DocumentsZeroBundlingViolations()
    {
        var text = File.ReadAllText(HandoffPath());
        // The narrative records zero violations at W20 close (10th
        // consecutive 0-violation wave — W11-W20 streak).
        var hasZeroViolations = text.Contains("0 active", StringComparison.OrdinalIgnoreCase)
                                  || text.Contains("0-violation", StringComparison.OrdinalIgnoreCase)
                                  || text.Contains("zero-violation", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasZeroViolations);
    }
}
