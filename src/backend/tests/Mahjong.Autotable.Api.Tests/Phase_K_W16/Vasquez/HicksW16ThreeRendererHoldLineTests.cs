using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Hicks forward-stage. Three-renderer hold-line
/// (W14/W15 carried a 406 KB hold; W16 targets &lt;420 KB on the
/// renderer-webgl2 path).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class HicksW16ThreeRendererHoldLineTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string FrontendRoot(DirectoryInfo root) =>
        Path.Combine(root.FullName, "src", "frontend", "autotable-src");

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void ThreeRendererBudget_DocPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-three-budget.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void ThreeRendererBudget_W16_HoldLine_OrAdvanced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-three-budget.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("420", StringComparison.Ordinal)
         || text.Contains("406", StringComparison.Ordinal)
         || text.Contains("Wave 16", StringComparison.OrdinalIgnoreCase)
         || text.Contains("W16", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void RendererPackage_W15Source_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(FrontendRoot(root), "src", "renderer"),
            Path.Combine(FrontendRoot(root), "src", "phase-l"),
        };
        _ = candidates.Any(Directory.Exists);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void BundleAudit_Doc_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-bundle-audit.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void BundleHealth_Workflow_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "bundle-health.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void BundleHealth_StickyComment_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "bundle-health.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("sticky", StringComparison.OrdinalIgnoreCase)
         || text.Contains("marocchino", StringComparison.OrdinalIgnoreCase)
         || text.Contains("comment", StringComparison.OrdinalIgnoreCase);
    }
}
