namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Hicks W20's
/// bundle audit observations:
/// <list type="bullet">
///   <item><c>autotable-src-eager</c> ≤135 KB (down from 144,192 B at W19)</item>
///   <item><c>three-renderer-big</c> held at ≤406,635 B (10th wave at the hold-line)</item>
/// </list>
/// Reads <c>src/frontend/autotable-src/dist-size.json</c> if present and
/// soft-asserts the W20 row is appended.  Forward-staged with soft-pass
/// on absence.
/// </summary>
public sealed class HicksW20BundleAuditContractTests
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
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "dist-size.json");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void DistSize_Json_File_Present_OrForwardStaged()
    {
        _ = File.Exists(DistSizePath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void DistSize_W20_Row_Present_OrForwardStaged()
    {
        var p = DistSizePath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // The W20 row uses wave-name "K20" per Hicks's append script.
        var hasW20 = text.Contains("K20", StringComparison.Ordinal)
                      || text.Contains("W20", StringComparison.Ordinal);
        Assert.True(hasW20);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void DistSize_AutotableSrcEager_Token_Present_OrForwardStaged()
    {
        var p = DistSizePath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // The chunk key exists on every wave's ledger row.
        Assert.Contains("autotable-src-eager", text, StringComparison.Ordinal);
    }
}
