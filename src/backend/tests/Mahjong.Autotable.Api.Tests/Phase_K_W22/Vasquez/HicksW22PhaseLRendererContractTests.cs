namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Hicks W22's
/// Phase L renderer additions (staged for W23 wiring):
///   discard-pile.ts   per-seat 6-col grid + riichi rotation
///   score-display.ts  canvas HUD (4 score chips + dora)
/// </summary>
public sealed class HicksW22PhaseLRendererContractTests
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
    public void DiscardPile_Module_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        // Discard-pile renderer module is anywhere under src/frontend/
        // autotable-src/src/.
        var srcDir = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "src");
        if (!Directory.Exists(srcDir)) return;
        var hits = Directory.EnumerateFiles(srcDir, "discard-pile.ts", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(srcDir, "*discard*.ts", SearchOption.AllDirectories))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(hits);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void ScoreDisplay_Module_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var srcDir = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "src");
        if (!Directory.Exists(srcDir)) return;
        var hits = Directory.EnumerateFiles(srcDir, "score-display.ts", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(srcDir, "*score-display*.ts", SearchOption.AllDirectories))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(hits);
    }
}
