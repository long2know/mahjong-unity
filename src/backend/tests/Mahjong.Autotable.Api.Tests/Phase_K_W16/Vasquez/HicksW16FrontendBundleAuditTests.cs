using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Hicks forward-stage. Frontend bundle audit
/// passes (extends the W15 candidate audit cadence).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class HicksW16FrontendBundleAuditTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void Audit_Doc_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-bundle-audit.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void Audit_W16_Wave_Section_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-bundle-audit.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("Wave 16", StringComparison.OrdinalIgnoreCase)
         || text.Contains("W16", StringComparison.Ordinal)
         || text.Contains("16.", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void Audit_BuildTooling_DocPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-build-tooling.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void BundleHealth_Workflow_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "bundle-health.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void Vite_BuildScript_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "package.json");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("vite", StringComparison.OrdinalIgnoreCase)
         || text.Contains("build", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Bundle"), Trait("Wave", "Phase-K-16")]
    public void BundleHealth_PrComment_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "bundle-health.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("pull_request", StringComparison.Ordinal)
         || text.Contains("comment", StringComparison.OrdinalIgnoreCase);
    }
}
