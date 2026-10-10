namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez self-lane W22 surface smoke facts.
/// A small set of "the things this Vasquez W22 commit promised"
/// hard-pins that the inbox memo / handoff doc reference.  All
/// hard-assert (they ship in this same PR).
/// </summary>
public sealed class W22SurfaceSmokeFactsTests
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
    public void Phase_K_W22_Vasquez_Directory_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var d = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W22", "Vasquez");
        Assert.True(Directory.Exists(d));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void Phase_K_W22_Vasquez_HasAtLeastTwentyForwardStages()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var d = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W22", "Vasquez");
        Assert.True(Directory.Exists(d));
        var n = Directory.EnumerateFiles(d, "*.cs", SearchOption.TopDirectoryOnly).Count();
        // Vasquez W22 brief: 20-30 forward-stage contract files.
        Assert.True(n >= 20, $"Expected ≥ 20 W22 Vasquez forward-stage files; found {n}.");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void KW21_To_KW22_Regression_Class_Rename_Landed()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var newPath = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Regression",
            "Wave1ThroughKW22RegressionTests.cs");
        var oldPath = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Regression",
            "Wave1ThroughKW21RegressionTests.cs");
        Assert.True(File.Exists(newPath), "New W22 regression class missing.");
        Assert.False(File.Exists(oldPath), "Old W21 regression class still present.");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void Wave1ThroughKW22RegressionTests_Class_Present()
    {
        var asm = typeof(W22SurfaceSmokeFactsTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }
}
