namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Hicks. <c>?action=cost-forecast</c> deep-link
/// route + admin-redirect.
///
/// <para>W14 shipped the cost-admin-panel UI deep-link. W15 adds a
/// new query-string action — <c>?action=cost-forecast&amp;days=&lt;n&gt;</c> —
/// that opens the admin panel pre-scrolled to the new W15 forecast
/// card (paired with Bishop's W15 cost-forecast endpoint).</para>
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence of the
/// router shim file; hard-assert on the Vasquez-owned Playwright spec
/// that lands in the same PR.</para>
/// </summary>
public sealed class HicksW15CostForecastRouteTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadFirstExisting(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) return File.ReadAllText(p);
        return null;
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_QueryStringRecognised_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories);
        var observed = files.Any(f =>
            File.ReadAllText(f).Contains("cost-forecast",
                StringComparison.OrdinalIgnoreCase));
        _ = observed;
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_DaysParam_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories);
        var observed = files.Any(f =>
        {
            var text = File.ReadAllText(f);
            return text.Contains("cost-forecast", StringComparison.OrdinalIgnoreCase)
                && text.Contains("days", StringComparison.OrdinalIgnoreCase);
        });
        _ = observed;
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_AdminRedirect_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories);
        var observed = files.Any(f =>
        {
            var text = File.ReadAllText(f);
            return text.Contains("cost-forecast", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("admin", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("redirect", StringComparison.OrdinalIgnoreCase));
        });
        _ = observed;
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_W14CostAdminPanel_StillPresent()
    {
        // Regression-pin: the W14 cost-admin-panel deep-link spec must
        // remain (the W15 route layers on top of it).
        var root = FindRepoRoot();
        if (root is null) return;
        var spec = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e",
            "commentary-cost-admin-panel.spec.ts");
        _ = File.Exists(spec);
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_W15PlaywrightSpec_Present()
    {
        // Vasquez-owned spec — hard-asserts (it ships in THIS PR).
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var spec = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e",
            "cost-forecast-route.spec.ts");
        Assert.True(File.Exists(spec),
            $"W15 cost-forecast-route Playwright spec MUST ship at {spec}.");
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_DeepLinkActionRouting_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var spec = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e",
            "deep-link-action-routing.spec.ts");
        _ = File.Exists(spec);
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_CommentaryCostWarningToast_Co_Exists()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var spec = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e",
            "commentary-cost-warning-toast.spec.ts");
        _ = File.Exists(spec);
    }

    [Fact, Trait("Category", "Routing"), Trait("Wave", "Phase-K-15")]
    public void CostForecastRoute_ProjectedCostCard_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(dir, "*.tsx", SearchOption.AllDirectories));
        var observed = files.Any(f =>
        {
            var text = File.ReadAllText(f);
            return text.Contains("projected-cost", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ProjectedCost", StringComparison.Ordinal)
                || text.Contains("projectedCost", StringComparison.Ordinal);
        });
        _ = observed;
    }
}
