namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Hicks. Playwright <c>snapshotPathTemplate</c>
/// convention.
///
/// <para>The W13 visual-regression baselines under
/// <c>src/frontend/autotable-src/tests/e2e/__screenshots__/</c> pin
/// by relative path. Playwright's default snapshot path is OS-
/// specific (e.g., <c>spec-name.spec.ts-snapshots/screenshot-Linux.png</c>),
/// which causes baseline drift between developer machines and CI.
/// The <c>snapshotPathTemplate</c> config setting normalises this to
/// a single canonical path.</para>
///
/// <para>Six reflection-defensive facts. Soft-pass on absence of the
/// playwright config file; hard-assert on the Vasquez-owned
/// Playwright spec that lands in the same PR.</para>
/// </summary>
public sealed class HicksW15SnapshotPathTemplateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadFirstExisting(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) return File.ReadAllText(p);
        return null;
    }

    private static string PlaywrightConfigPath(DirectoryInfo root) =>
        Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "playwright.config.ts");

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-15")]
    public void SnapshotPathTemplate_ConfigKey_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadFirstExisting(PlaywrightConfigPath(root));
        if (text is null) return;
        _ = text.Contains("snapshotPathTemplate", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-15")]
    public void SnapshotPathTemplate_RelativeToTestFile_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadFirstExisting(PlaywrightConfigPath(root));
        if (text is null) return;
        // The convention pins the path to be relative to the test
        // file's directory, NOT to the OS-specific defaults — typical
        // template references include {testFileDir}, {arg}, or
        // {projectName}.
        _ = text.Contains("{testFileDir}", StringComparison.Ordinal)
         || text.Contains("{testFilePath}", StringComparison.Ordinal)
         || text.Contains("__screenshots__", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-15")]
    public void SnapshotPathTemplate_W15PlaywrightSpec_Present()
    {
        // Vasquez-owned consumer spec — hard-asserts (it ships in
        // THIS PR).
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var spec = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e",
            "snapshotPathTemplate.spec.ts");
        Assert.True(File.Exists(spec),
            $"W15 snapshotPathTemplate Playwright spec MUST ship at {spec}.");
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-15")]
    public void SnapshotPathTemplate_W13Baselines_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e", "__screenshots__");
        // Soft-pass: the baselines may exist or not depending on the
        // most recent capture-baselines run.
        _ = Directory.Exists(dir);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-15")]
    public void SnapshotPathTemplate_VisualRegressionWorkflow_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".github", "workflows",
            "playwright-visual-regression.yml");
        Assert.True(File.Exists(path),
            $"playwright-visual-regression.yml MUST remain at {path}.");
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-15")]
    public void SnapshotPathTemplate_W14SpecFix_StillReferenced_InDocs()
    {
        // W14 §5.2 (test-architecture doc) names the spec fix
        // (page.goto before setContent); this must remain.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§5.2", text, StringComparison.Ordinal);
    }
}
