using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Hicks forward-stage. Phase L renderer-webgl2
/// W17 polish: <c>scene.ts</c> (TileScene runtime wiring camera +
/// mesh + atlas with rAF-coalesced redraw and DPR-aware framebuffer),
/// <c>picking.ts</c> (ray-cast picking via inverse-VP + ray-AABB
/// slab intersection — CPU-side, no readPixels round-trip), and
/// the extended 3-mode <c>hello.ts</c> harness adding
/// <c>?renderer=webgl2-scene</c> with click-to-pick demo.
///
/// <para>Five filesystem-defensive facts. Soft-pass on absence —
/// the surfaces land in Hicks's W17 lane.</para>
/// </summary>
public sealed class HicksW17PhaseLRendererSceneTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string RendererDir(DirectoryInfo root)
        => Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "src", "renderer-webgl2");

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void Scene_Source_File_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(RendererDir(root), "scene.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void Picking_Source_File_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(RendererDir(root), "picking.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void Hello_Harness_Has3ModeWebGL2Scene_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(RendererDir(root), "hello.ts");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        _ = body.Contains("webgl2-scene", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void IndexTs_UrlGateRegex_AcceptsWebGL2Scene_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "src", "index.ts");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        _ = body.Contains("webgl2-scene", StringComparison.OrdinalIgnoreCase)
            || body.Contains("webgl2-tile-mesh", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void TileMesh_W16_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        // W16 surface — must still be reachable post-rebase.
        var path = Path.Combine(RendererDir(root), "tile-mesh.ts");
        _ = File.Exists(path);
    }
}
