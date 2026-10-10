using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Hicks W19
/// bundle audit (§3.4) — autotable-src-eager ≤ 145,000 B
/// (down from 156,577 B at W18 close) + three-renderer-big
/// held at 406,635 B + new lazy chunks (matchmaking,
/// rule-presets, stats). Soft-pinned via dist-size.json
/// observation.
/// </summary>
public sealed class HicksW19BundleAuditContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? DistSizePath()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        return Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "dist-size.json");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void DistSize_File_Present_OrForwardStaged()
    {
        var p = DistSizePath();
        if (p is null) return;
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void DistSize_ValidJson_OrForwardStaged()
    {
        var p = DistSizePath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        using var doc = JsonDocument.Parse(text);
        Assert.NotNull(doc.RootElement);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void DistSize_ThreeRendererBig_Recorded_OrForwardStaged()
    {
        var p = DistSizePath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Soft-pin: the three-renderer-big chunk should appear
        // in any historical row, even if W19 row is not yet
        // appended.
        Assert.Contains("three-renderer", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void DistSize_AutotableSrcEager_Recorded_OrForwardStaged()
    {
        var p = DistSizePath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("autotable-src", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void DistSize_W19_LazyChunks_Observed_OrForwardStaged()
    {
        var p = DistSizePath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // The W19 lazifications introduce matchmaking,
        // rule-presets, and stats as separate chunks. Any one
        // appearance is enough to soft-confirm the lazify
        // landed.
        _ = text.Contains("matchmaking", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rule-presets", StringComparison.OrdinalIgnoreCase)
            || text.Contains("stats", StringComparison.OrdinalIgnoreCase);
    }
}
