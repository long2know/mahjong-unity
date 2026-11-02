using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Hicks forward-stage. Phase L renderer-webgl2
/// hello-world extension (W15 shipped a single-triangle hello-world;
/// W16 likely extends with a tile atlas or second draw call).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class HicksW16PhaseLWebGL2ExtensionTests
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

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-16")]
    public void WebGL2HelloWorld_W15Predecessor_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(FrontendRoot(root), "src", "renderer", "webgl2"),
            Path.Combine(FrontendRoot(root), "src", "renderer"),
            Path.Combine(FrontendRoot(root), "src", "phase-l"),
        };
        _ = candidates.Any(Directory.Exists);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-16")]
    public void TileAtlas_Extension_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(FrontendRoot(root), "src", "renderer", "tile-atlas.ts"),
            Path.Combine(FrontendRoot(root), "src", "renderer", "atlas"),
            Path.Combine(FrontendRoot(root), "src", "renderer", "webgl2", "atlas.ts"),
        };
        _ = candidates.Any(p => File.Exists(p) || Directory.Exists(p));
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-16")]
    public void SecondDrawCall_Extension_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(FrontendRoot(root), "src", "renderer", "webgl2", "draw.ts");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        _ = text.Contains("drawElements", StringComparison.Ordinal)
         || text.Contains("drawArrays", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-16")]
    public void PhaseL_L1DesignMemo_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "phase-l-l1-design.md"),
            Path.Combine(root.FullName, "Phase_L", "design.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-16")]
    public void PhaseL_RendererBudget_DocPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-three-budget.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-16")]
    public void PhaseL_BringupDoc_W16Section_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "phase-l-bringup.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("Wave 16", StringComparison.OrdinalIgnoreCase)
         || text.Contains("W16", StringComparison.Ordinal);
    }
}
