namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez self-lane observation test for the
/// W20 retrospective audit carry-over.  W20 process-retrospective
/// audit was authored as <c>docs/agent-handoff-protocol.md §8</c>.
/// W21 introduces a new top-level <c>§9 W21 stash-isolation
/// directive</c> codifying the Apone W20 mid-task stash-reset
/// lesson (Apone's mid-W20 reset wiped Hicks's working tree; Hicks
/// recovered via the <c>apone-w20-baseline-...-recovered-by-...</c>
/// renamed stash).  The W21 directive states explicitly: never
/// touch other agents' working tree state mid-wave.
///
/// <para>This is a hard-asserting Vasquez-lane test (it ships in
/// this same PR alongside the §9 stash-isolation directive in
/// <c>docs/agent-handoff-protocol.md</c>).</para>
/// </summary>
public sealed class W21RetrospectiveAuditObservationTests
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
    public void HandoffDoc_Section_8_W20_Audit_Still_Present()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("§8. W20 retrospective audit", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_9_W21_StashIsolation_Present()
    {
        var text = File.ReadAllText(HandoffPath());
        // New top-level §9 codifies the Apone W20 mid-task reset lesson.
        var has = text.Contains("§9. W21", StringComparison.Ordinal)
                   || text.Contains("§9 W21", StringComparison.Ordinal)
                   || text.Contains("W21 stash-isolation", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_StashIsolation_NeverTouchOtherAgent_Token_Present()
    {
        var text = File.ReadAllText(HandoffPath());
        // Core directive verbiage.
        var has = text.Contains("never touch other agents", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("stash-isolation", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("other agents' working tree", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W21_RatchetLevel_Still_2()
    {
        var text = File.ReadAllText(HandoffPath());
        // Ratchet stays at level 2 (W18 + W19 incidents only; W20
        // and W21 closed without new occurrence).
        var has = text.Contains("ratchet", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("level 2", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
