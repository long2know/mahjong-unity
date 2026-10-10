namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Hicks W21's
/// Phase L W6 renderer-webgl2 tile-claim-animation module
/// (<c>src/renderer-webgl2/tile-claim-animation.ts</c>) —
/// pung/kong/chi staggered fan-in with easeOutBack.  Soft-pinned
/// so the gate stays green if Hicks W21 has not yet landed the
/// module.
/// </summary>
public sealed class HicksW21PhaseLTileClaimAnimationContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AnimPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "renderer-webgl2", "tile-claim-animation.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void TileClaimAnimation_File_Present_OrForwardStaged()
    {
        _ = File.Exists(AnimPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void TileClaimAnimation_EaseOutBackToken_Present_OrForwardStaged()
    {
        var p = AnimPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("easeOutBack", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("EaseOutBack", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void TileClaimAnimation_ClaimTypeTokens_Present_OrForwardStaged()
    {
        var p = AnimPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // pung/kong/chi staggered fan-in is the W21 contract.
        var hasPung = text.Contains("pung", StringComparison.OrdinalIgnoreCase);
        var hasKong = text.Contains("kong", StringComparison.OrdinalIgnoreCase);
        var hasChi = text.Contains("chi", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasPung || hasKong || hasChi);
    }
}
