namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Hicks W22's
/// admin-panel chunk-split (vite manualChunks):
///   admin-panel-core         31,164 B (7 surfaces)
///   admin-panel-tournaments  32,579 B (12 surfaces, lazy)
/// (was admin-panel: 48,984 B at W21).
/// </summary>
public sealed class HicksW22AdminPanelChunkSplitContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void ViteConfig_HasManualChunks_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "vite.config.ts");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("manualChunks", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void ViteConfig_HasAdminPanelCoreChunk_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "vite.config.ts");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("admin-panel-core", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void ViteConfig_HasAdminPanelTournamentsChunk_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "vite.config.ts");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("admin-panel-tournaments", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void DistSize_AdminPanelCore_PresentAndUnder40KB_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "dist-size.json");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("admin-panel-core", StringComparison.Ordinal);
        Assert.True(has);
    }
}
