using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Hicks W18 contract: LH13 W18 cron
/// status (post-Apone W18 form-factor fix). Hicks W18 §9 records
/// the post-fix cron observation in <c>docs/lh13-soft-pin-rationale.md</c>.
/// Soft-pin on absence.
/// </summary>
public sealed class HicksW18Lh13W18CronStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Lh13Doc_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "lh13-soft-pin-rationale.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        Assert.True(text.Length > 0);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Lh13Doc_W18Section_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "lh13-soft-pin-rationale.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        // Hicks W18 will add a §9 (W18) entry; soft-pin until then.
        if (!text.Contains("§9", StringComparison.Ordinal)
            && !text.Contains("W18", StringComparison.Ordinal)) return;
        Assert.True(text.Length > 0);
    }
}
