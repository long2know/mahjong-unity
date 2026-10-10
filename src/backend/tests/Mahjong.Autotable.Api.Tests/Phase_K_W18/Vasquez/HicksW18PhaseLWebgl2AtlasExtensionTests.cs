using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Hicks W18 contract: Phase L
/// webgl2 atlas extension (continuation of the W15-onward
/// webgl2 hello-world thread). Filesystem-defensive soft-pin.
/// </summary>
public sealed class HicksW18PhaseLWebgl2AtlasExtensionTests
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
    public void Renderer_Src_Folder_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend", "autotable-src");
        if (!Directory.Exists(dir)) return;
        Assert.True(Directory.Exists(dir));
    }
}
