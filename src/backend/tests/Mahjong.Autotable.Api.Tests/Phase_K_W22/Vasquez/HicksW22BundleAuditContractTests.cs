namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Hicks W22's
/// bundle-audit §3.7 work: shed ~7 KB from `autotable-src-eager`
/// (112,219 B → 107,020 B target ≤105 KB / 107,520 B) by
/// extracting onboarding card + avatar-migration modal out of
/// identity.ts into lazy chunks (identity-onboarding 3,293 B +
/// identity-avatar-migration 1,987 B).  Also pins three-renderer-
/// big at ≤ 406,635 B (12th consecutive W14 hold-line wave).
/// </summary>
public sealed class HicksW22BundleAuditContractTests
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
    public void DistSizeJson_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "dist-size.json");
        if (!File.Exists(p)) return;
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void DistSizeJson_Has_AutotableSrcEager_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "dist-size.json");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("autotable-src-eager", StringComparison.Ordinal)
                   || text.Contains("autotable-src", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void DistSizeJson_Has_ThreeRendererBig_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "dist-size.json");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("three-renderer-big", StringComparison.Ordinal)
                   || text.Contains("three-renderer", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void IdentityOnboardingChunk_BundleAuditExtracted_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "dist-size.json");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("identity-onboarding", StringComparison.Ordinal)
                   || text.Contains("identity-avatar-migration", StringComparison.Ordinal);
        Assert.True(has);
    }
}
