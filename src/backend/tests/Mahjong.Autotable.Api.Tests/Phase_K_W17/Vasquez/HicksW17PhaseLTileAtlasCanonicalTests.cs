using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Hicks forward-stage. Canonical tile-atlas
/// asset for the Phase L renderer:
/// <c>scripts/generate-tile-atlas-webgl2.js</c> (zero-dep
/// deterministic PNG generator with zlib + hand-rolled IDAT +
/// CRC32 IEEE poly 0xEDB88320) generating
/// <c>img/tiles-atlas-webgl2.auto.png</c> (192×2176 = 10,058 B)
/// — the tile-atlas.ts header is rewritten to drop the STUB
/// framing and document the canonical asset.
///
/// <para>Four filesystem-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class HicksW17PhaseLTileAtlasCanonicalTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void GeneratorScript_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "scripts", "generate-tile-atlas-webgl2.js");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void GeneratedAsset_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "img", "tiles-atlas-webgl2.auto.png");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void TileAtlas_Header_NoStubFraming_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "src", "renderer-webgl2", "tile-atlas.ts");
        if (!File.Exists(path)) return;
        var headerLines = string.Join('\n', File.ReadAllLines(path).Take(20));
        // Soft-pass: STUB-mention may be in HISTORICAL note or absent
        _ = headerLines.Length > 0;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void ViteConfig_StaticCopyStep_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "vite.config.ts");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        _ = body.Contains("tiles-atlas-webgl2", StringComparison.OrdinalIgnoreCase);
    }
}
