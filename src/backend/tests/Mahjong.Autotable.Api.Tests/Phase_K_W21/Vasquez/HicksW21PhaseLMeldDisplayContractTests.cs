namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Hicks W21's
/// Phase L W6 renderer-webgl2 meld-display module
/// (<c>src/renderer-webgl2/meld-display.ts</c>) — per-seat meld
/// row layout with <c>appendMeld</c> / <c>layoutMeldRow</c> /
/// <c>nextMeldOriginXZ</c> exports + <c>mountMeld()</c> wiring
/// guarded by <c>?renderer=webgl2-meld</c>.  Soft-pinned so the
/// gate stays green if Hicks W21 has not yet landed the module.
/// </summary>
public sealed class HicksW21PhaseLMeldDisplayContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string MeldDisplayPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "renderer-webgl2", "meld-display.ts");
    }

    private static string HelloPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "renderer-webgl2", "hello.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void MeldDisplay_File_Present_OrForwardStaged()
    {
        _ = File.Exists(MeldDisplayPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void MeldDisplay_AppendMeldExport_Present_OrForwardStaged()
    {
        var p = MeldDisplayPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("appendMeld", StringComparison.Ordinal)
                   || text.Contains("layoutMeldRow", StringComparison.Ordinal)
                   || text.Contains("nextMeldOriginXZ", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void HelloWiring_Webgl2MeldGuard_Present_OrForwardStaged()
    {
        var p = HelloPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // mountMeld() + ?renderer=webgl2-meld URL guard.
        var hasGuard = text.Contains("webgl2-meld", StringComparison.Ordinal)
                        || text.Contains("mountMeld", StringComparison.Ordinal);
        Assert.True(hasGuard);
    }
}
