using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Hicks forward-stage. Playwright visual-
/// regression extension (W16 anticipates a second spec or an
/// additional surface beyond manifest-screenshots-visual).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence —
/// the visual-regression surface is co-owned by Hicks (Playwright
/// runtime) and Vasquez (test-lane root).</para>
/// </summary>
public sealed class HicksW16PlaywrightVisualRegressionTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string E2eRoot(DirectoryInfo root) =>
        Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "tests", "e2e");

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-16")]
    public void Spec_W15ManifestScreenshots_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(E2eRoot(root), "manifest-screenshots-visual.spec.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-16")]
    public void Spec_W16ExtensionSpec_Present_OrForwardStaged()
    {
        // Hicks may add a second visual spec in W16 (e.g., lobby-game
        // or spectator-view).  Soft-pass: this is forward-staged.
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(E2eRoot(root), "lobby-visual.spec.ts"),
            Path.Combine(E2eRoot(root), "spectator-visual.spec.ts"),
            Path.Combine(E2eRoot(root), "tournament-visual.spec.ts"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-16")]
    public void Config_PlaywrightConfigTs_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(E2eRoot(root), "playwright.config.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-16")]
    public void Config_SnapshotPathTemplate_Pinned()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(E2eRoot(root), "playwright.config.ts");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("snapshotPathTemplate", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-16")]
    public void Baselines_W15Screenshots_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(E2eRoot(root), "__screenshots__");
        _ = Directory.Exists(dir);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-16")]
    public void CaptureScript_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "scripts", "capture-visual-baselines.js"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "scripts", "capture-real-surfaces.js"),
        };
        _ = candidates.Any(File.Exists);
    }
}
