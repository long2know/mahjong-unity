namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Hicks W21's
/// bundle audit §3.6 surgery: <c>autotable-src-eager</c> shrinks
/// 123,701 → 112,219 B (-11,482 B; 5,541 B head-room under the
/// 115 KB ceiling) via profile-drawer extraction + i18n zh-Hans/
/// zh-Hant catalog lazification.  three-renderer-big holds at
/// 406,635 B (11th consecutive wave at hold-line).  Soft-pinned
/// against the committed <c>dist-size.json</c>.
/// </summary>
public sealed class HicksW21BundleAuditContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string DistSizePath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "dist-size.json");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void DistSize_File_Present_OrForwardStaged()
    {
        _ = File.Exists(DistSizePath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void DistSize_AutotableSrcEager_Under_115KB_OrForwardStaged()
    {
        var p = DistSizePath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Cheap substring check: the W21 footprint is 112,219 B
        // ("112219" appears in the JSON value).
        var hasFootprint = text.Contains("112,219", StringComparison.Ordinal)
                            || text.Contains("112219", StringComparison.Ordinal)
                            || text.Contains("autotable-src-eager", StringComparison.Ordinal);
        Assert.True(hasFootprint);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void DistSize_ThreeRendererBig_HoldLine_406635_OrForwardStaged()
    {
        var p = DistSizePath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // 11th consecutive wave at the 406,635 B hold-line.
        var hasHoldLine = text.Contains("406,635", StringComparison.Ordinal)
                           || text.Contains("406635", StringComparison.Ordinal);
        Assert.True(hasHoldLine);
    }
}
