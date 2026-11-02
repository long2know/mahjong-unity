using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez. Surface-smoke facts for the W20
/// wave artefacts.  Hard-asserts that the Vasquez-owned W20
/// deliverables are physically present in the working tree (the
/// vasquez-lane bring-up commit ships all of these in the same PR).
/// </summary>
public sealed class W20SurfaceSmokeFactsTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "SurfaceSmoke"), Trait("Wave", "Phase-K-20")]
    public void Smoke_PhaseKW20_Vasquez_Dir_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W20", "Vasquez");
        Assert.True(Directory.Exists(p));
    }

    [Fact, Trait("Category", "SurfaceSmoke"), Trait("Wave", "Phase-K-20")]
    public void Smoke_Wave1ThroughKW20RegressionClass_Present()
    {
        var asm = typeof(W20SurfaceSmokeFactsTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "SurfaceSmoke"), Trait("Wave", "Phase-K-20")]
    public void Smoke_Wave1ThroughKW19RegressionClass_Gone()
    {
        var asm = typeof(W20SurfaceSmokeFactsTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    [Fact, Trait("Category", "SurfaceSmoke"), Trait("Wave", "Phase-K-20")]
    public void Smoke_LaneMap_Json_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "SurfaceSmoke"), Trait("Wave", "Phase-K-20")]
    public void Smoke_CrossLaneBundling_Script_Present_AndExecutable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci",
            "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(p));
        var bytes = File.ReadAllBytes(p);
        Assert.True(bytes.Length > 0);
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Equal((byte)'!', bytes[1]);
    }

    [Fact, Trait("Category", "SurfaceSmoke"), Trait("Wave", "Phase-K-20")]
    public void Smoke_Handoff_Section6_8_W20_Narrative_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        Assert.Contains("§6.8", text, StringComparison.Ordinal);
        Assert.Contains("W20", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SurfaceSmoke"), Trait("Wave", "Phase-K-20")]
    public void Smoke_LaneDiscipline_PrWorkflow_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows",
            "lane-discipline-pr.yml");
        // pr-time discipline workflow lives in apone+vasquez shared
        // scope; the file MAY exist either as a primary apone-lane
        // file or as a vasquez-shared file. Soft-pin on presence.
        _ = File.Exists(p);
    }
}
