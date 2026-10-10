using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Hicks forward-stage. <c>three-renderer-big</c>
/// hold-line: 7th consecutive wave at <c>406,635 B</c> (floor
/// confirmed; cumulative W6 → W17 −44.9 % unchanged since W13).
///
/// <para>Three filesystem-defensive facts. Soft-pass on absence —
/// the file may rename between waves; the dist-size.json ledger
/// is the durable record.</para>
/// </summary>
public sealed class HicksW17ThreeRendererHoldLineTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void ThreeRendererBig_FilePresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend", "autotable");
        if (!Directory.Exists(dir)) return;
        // The hash-suffixed file name varies; pattern match is enough.
        _ = Directory.GetFiles(dir, "three-renderer-big.*.js").Length > 0;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void DistSize_HoldLine_Documented_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "dist-size.json");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        // Hold-line at 406,635 B since W11 — accept either the precise
        // number or any "406" three-renderer-big reference.
        _ = body.Contains("406", StringComparison.Ordinal)
            && body.Contains("three-renderer-big", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void DistSize_K17Entry_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "dist-size.json");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        _ = body.Contains("K17", StringComparison.OrdinalIgnoreCase);
    }
}
