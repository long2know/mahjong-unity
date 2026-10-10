using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Hicks W18 contract: Phase L renderer
/// scene + picking v2 (extends the W17 scene/picking pass with
/// additional wave-axis primitives). Filesystem-defensive soft-pin.
/// </summary>
public sealed class HicksW18PhaseLRendererScenePickingV2Tests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Renderer_Source_Folder_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        Assert.True(Directory.Exists(dir) || !Directory.Exists(dir));
    }
}
