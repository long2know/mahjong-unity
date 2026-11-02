namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Apone W22's
/// mobile build matrix expansion (tvOS + watchOS jobs).
/// </summary>
public sealed class AponeW22MobileBuildMatrixContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void MobileBuildWorkflow_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "mobile-build.yml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("ios", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void MobileBuildWorkflow_HasTvOSJob_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "mobile-build.yml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("tvos-build", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("appletvos", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("tvos", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void MobileBuildWorkflow_HasWatchOSJob_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "mobile-build.yml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("watchos-build", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("watchos", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void MobileApplePlatformsDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "mobile-apple-platforms.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("tvOS", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("watchOS", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
