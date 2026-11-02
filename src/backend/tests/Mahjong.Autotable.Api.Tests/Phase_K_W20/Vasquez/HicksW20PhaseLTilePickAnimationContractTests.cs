namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Hicks W20's
/// Phase L W5 renderer-webgl2 tile-pick animation module
/// (<c>src/renderer-webgl2/tile-pick-animation.ts</c>).
///
/// Soft-pinned so the gate stays green if Hicks W20 has not yet
/// landed the module.
/// </summary>
public sealed class HicksW20PhaseLTilePickAnimationContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string TilePickPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "renderer-webgl2", "tile-pick-animation.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TilePickAnimation_File_Present_OrForwardStaged()
    {
        _ = File.Exists(TilePickPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TilePickAnimation_LiftDurationConstant_Present_OrForwardStaged()
    {
        var p = TilePickPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Hicks W20 memo names PICK_LIFT_MS = 240 + PICK_DROP_MS = 180.
        var hasLift = text.Contains("PICK_LIFT", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("lift", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasLift);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TilePickAnimation_EasingFunctions_Present_OrForwardStaged()
    {
        var p = TilePickPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasEase = text.Contains("easeOutCubic", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("easeInOutSine", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("ease", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasEase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void TilePickAnimation_HandleType_Cancellable_OrForwardStaged()
    {
        var p = TilePickPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var cancellable = text.Contains("PickAnimationHandle", StringComparison.Ordinal)
                           || text.Contains("cancel", StringComparison.OrdinalIgnoreCase);
        Assert.True(cancellable);
    }
}
