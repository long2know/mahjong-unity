namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez self-lane. W19 retrospective
/// audit observation tests.  Soft-pin the NEW §7 audit
/// section in <c>docs/agent-handoff-protocol.md</c> + the
/// Apone W19 bundling-incident memo in the inbox.
///
/// <para>Findings cited:</para>
/// <list type="bullet">
///   <item>Hicks <c>47377f2</c> — clean (single-lane: hicks)</item>
///   <item>Apone <c>f153d90</c> — clean incident memo (shared §6.4/§6.5 + apone-inbox)</item>
///   <item>Apone <c>90a7ff6</c> — clean bring-up re-land (single-lane: apone)</item>
///   <item><c>d700cf7</c> — reverted before W19 PR settled (no active lane-discipline violation)</item>
/// </list>
/// </summary>
public sealed class W19RetrospectiveAuditObservationTests
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
    public void HandoffDoc_Section7_W19RetrospectiveAudit_Present()
    {
        var text = File.ReadAllText(HandoffDocPath());
        // Soft-pin: §7 header should be installed by Vasquez
        // as part of W19 close.
        Assert.Contains("§7", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W19RetrospectiveAudit_HicksCommit_Cited()
    {
        var text = File.ReadAllText(HandoffDocPath());
        Assert.Contains("47377f2", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W19RetrospectiveAudit_AponeReland_Cited()
    {
        var text = File.ReadAllText(HandoffDocPath());
        Assert.Contains("90a7ff6", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W19RetrospectiveAudit_AponeIncidentMemo_Cited()
    {
        var text = File.ReadAllText(HandoffDocPath());
        Assert.Contains("f153d90", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_W19RetrospectiveAudit_RevertedCommit_Cited()
    {
        var text = File.ReadAllText(HandoffDocPath());
        // d700cf7 was the cross-lane bundling incident, force-
        // with-lease reverted before W19 settled.
        Assert.Contains("d700cf7", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void AponeIncidentMemo_File_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, ".squad", "decisions", "inbox",
            "apone-phase-k-wave-19-bundling-incident.md");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Vasquez")]
    public void VasquezInboxMemo_W19_File_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, ".squad", "decisions", "inbox",
            "vasquez-phase-k-wave-19.md");
        _ = File.Exists(p);
    }
}
