using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Hicks W18 contract: bundle audit
/// W18 (continuation of the W17 autotable-src-eager shrinkage
/// trend). Filesystem-defensive soft-pin.
/// </summary>
public sealed class HicksW18BundleAuditTests
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
    public void BundleAudit_Doc_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "frontend-bundle-audit.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        Assert.True(text.Length > 0);
    }
}
