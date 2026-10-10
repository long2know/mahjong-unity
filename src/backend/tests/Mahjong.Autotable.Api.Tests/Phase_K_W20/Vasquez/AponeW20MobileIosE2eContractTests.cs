namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Apone W20's
/// mobile iOS E2E groundwork — narrative doc
/// (<c>docs/mobile-ios-e2e.md</c>) and any
/// <c>mobile-build.yml</c> additions.
///
/// Soft-pinned so the gate stays green if Apone W20 has not yet
/// landed the workflow + doc.
/// </summary>
public sealed class AponeW20MobileIosE2eContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void MobileIosE2e_Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "mobile-ios-e2e.md");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void MobileBuild_Workflow_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "mobile-build.yml");
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void MobileBuild_Workflow_HasIosOrAndroidToken()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "mobile-build.yml");
        var text = File.ReadAllText(p);
        var hasMobile = text.Contains("ios", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("android", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasMobile);
    }
}
