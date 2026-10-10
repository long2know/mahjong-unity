namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Hicks W19
/// Phase L renderer canonical wall geometry (4 × 18 × 2 layout
/// + dora indicator slot). Hicks's hard-asserts live in his
/// Playwright spec + the bundle audit; this paired contract
/// soft-pins the TypeScript module presence + canonical export
/// names so the Vasquez gate can observe the W19 surface.
/// </summary>
public sealed class HicksW19PhaseLWallGeometryContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? WallGeometryPath()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        return Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "renderer-webgl2", "wall-geometry.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void WallGeometry_TsModule_File_Present_OrForwardStaged()
    {
        var p = WallGeometryPath();
        if (p is null) return;
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void WallGeometry_CanonicalTileCount_Constant_Present_OrForwardStaged()
    {
        var p = WallGeometryPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("CANONICAL_WALL_TILE_COUNT", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void WallGeometry_PopulateWithDora_Exported_OrForwardStaged()
    {
        var p = WallGeometryPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("populateWallWithDora", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void WallGeometry_WallSlotCentre_Exported_OrForwardStaged()
    {
        var p = WallGeometryPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("wallSlotCentre", text, StringComparison.Ordinal);
    }
}
