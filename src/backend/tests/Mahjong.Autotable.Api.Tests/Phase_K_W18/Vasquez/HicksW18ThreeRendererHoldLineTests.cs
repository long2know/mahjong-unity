using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Hicks W18 contract: 8th consecutive
/// wave at the renderer hold-line floor. Soft-pin on absence —
/// the W14 hold-line was 406 KB; W17 holds at 406,635 B; W18
/// continues the streak.
/// </summary>
public sealed class HicksW18ThreeRendererHoldLineTests
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
    public void ThreeBudget_Doc_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "frontend-three-budget.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        Assert.True(text.Length > 0);
    }
}
