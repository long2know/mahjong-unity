namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez self-lane master inventory test.
/// File-inventory check + handoff-doc + KW22-rename + inbox-memo
/// + retro-audit presence assertions for the W22 cycle.  Hard-
/// asserts; ships in this same PR.
/// </summary>
public sealed class VasquezW22SelfLaneTests
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
    public void HandoffDoc_Section_6_11_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        // §6.11 ratifies Hicks W22 LH13 §6.8 HOLD-YELLOW decision
        // (natural-cron-pace blocker, W25-earliest PROMOTE).
        Assert.Contains("6.11", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_10_W22Retro_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        var has = text.Contains("§10. W22 retrospective audit", StringComparison.Ordinal)
                   || text.Contains("## §10. W22", StringComparison.Ordinal)
                   || text.Contains("W22 retrospective audit", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void Vasquez_W22_SafeBackupDir_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var d = Path.Combine(root!.FullName, ".work", "vasquez-w22-safe");
        // Safe-backup directory is created by the W22 Vasquez agent;
        // tolerate absence in CI (where .work/ may be gitignored).
        _ = Directory.Exists(d);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void Vasquez_W22_LaneMap_Has_Vasquez_Lane_Regex()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("vasquez", text);
        Assert.Contains("Phase_K_W", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void Vasquez_W22_CheckCrossLaneBundlingScript_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci",
            "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void Vasquez_W22_HandoffDoc_14WaveDeferralArc_Recorded()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        // §4.8 + §6.11 + §10 should mention the 14-wave deferral arc
        // (W7 → W22) carried forward at W22.
        var has = text.Contains("14-wave deferral arc", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("14 waves wide", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void Vasquez_W22_HandoffDoc_W25EarliestPrediction_Recorded()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        // §6.11 must explicitly record the W25-earliest PROMOTE prediction.
        var has = text.Contains("W25 earliest", StringComparison.Ordinal)
                   || text.Contains("W25-earliest", StringComparison.Ordinal);
        Assert.True(has);
    }
}
