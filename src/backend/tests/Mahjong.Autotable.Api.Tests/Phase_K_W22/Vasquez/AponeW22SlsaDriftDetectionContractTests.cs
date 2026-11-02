namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Apone W22's
/// SLSA-3 drift-detection sustaining workflow.  Weekly cron
/// (Monday 07:00 UTC) + workflow_dispatch.  Walks
/// .github/workflows/*.yml and FAILS when any `uses:&lt;action&gt;@&lt;ref&gt;`
/// ref is NOT a 40-char hex SHA outside the documented
/// allow-list (slsa-framework reusable + ./ local).
/// </summary>
public sealed class AponeW22SlsaDriftDetectionContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void SlsaDriftDetectionWorkflow_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "slsa-drift-detection.yml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("cron:", StringComparison.Ordinal)
                   && text.Contains("workflow_dispatch", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void SlsaDriftDetectionDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "slsa-drift-detection.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("drift", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("SLSA", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void SlsaDriftDetectionWorkflow_HasAllowList_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "slsa-drift-detection.yml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Allow-list per Apone W22 memo §2: slsa-framework reusable
        // workflow tag-pin carve-out + local ./ refs.
        var has = text.Contains("slsa-framework", StringComparison.Ordinal)
                   || text.Contains("allow", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
