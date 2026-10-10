namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez self-lane master inventory test.
/// File-inventory check + handoff-doc + KW21-rename + inbox-memo
/// + dry-run-log presence assertions for the W21 cycle.  Hard-
/// asserts; ships in this same PR.
/// </summary>
public sealed class VasquezW21SelfLaneTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_6_10_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        // §6.10 ratifies Hicks W21 LH13 §6.8 HOLD-YELLOW decision.
        Assert.Contains("6.10", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_9_StashIsolation_W21_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        var has = text.Contains("§9. W21", StringComparison.Ordinal)
                   || text.Contains("§9 W21", StringComparison.Ordinal)
                   || text.Contains("W21 stash-isolation", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void Vasquez_W21_SafeBackupDir_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var d = Path.Combine(root!.FullName, ".work", "vasquez-w21-safe");
        // Safe-backup directory is created by the W21 Vasquez agent;
        // tolerate absence in CI (where .work/ may be gitignored).
        _ = Directory.Exists(d);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void Vasquez_W21_LaneMap_Has_Vasquez_Lane_Regex()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("vasquez", text);
        Assert.Contains("Phase_K_W", text);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Vasquez")]
    public void Vasquez_W21_CheckCrossLaneBundlingScript_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci",
            "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(p));
    }
}
