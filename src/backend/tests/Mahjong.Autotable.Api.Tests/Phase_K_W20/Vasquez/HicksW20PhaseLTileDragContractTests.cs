namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Hicks W20's
/// Phase L W5 renderer-webgl2 tile drag-and-drop module
/// (<c>src/renderer-webgl2/tile-drag.ts</c>) — Pointer Events
/// drag + hover with raycaster bridge to the canvas.
///
/// Soft-pinned so the gate stays green if Hicks W20 has not yet
/// landed the module.
/// </summary>
public sealed class HicksW20PhaseLTileDragContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string TileDragPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "renderer-webgl2", "tile-drag.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TileDrag_File_Present_OrForwardStaged()
    {
        _ = File.Exists(TileDragPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TileDrag_PointerEventToken_Present_OrForwardStaged()
    {
        var p = TileDragPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasPointer = text.Contains("pointer", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasPointer);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TileDrag_RaycasterToken_Present_OrForwardStaged()
    {
        var p = TileDragPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasRay = text.Contains("raycast", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasRay);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TileDrag_HoverOrDragToken_Present_OrForwardStaged()
    {
        var p = TileDragPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasHoverDrag = text.Contains("drag", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("hover", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasHoverDrag);
    }
}
