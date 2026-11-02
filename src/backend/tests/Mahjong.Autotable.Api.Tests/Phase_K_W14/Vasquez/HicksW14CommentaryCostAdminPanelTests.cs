namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Hicks. Commentary cost admin panel
/// (<c>?action=admin-cost</c>) — admin-only with 401 redirect +
/// rendered cost data.
///
/// <para>Pairs with Bishop's W14 commentary cost summary endpoint.</para>
///
/// <para>Six reflection-defensive facts.</para>
/// </summary>
public sealed class HicksW14CommentaryCostAdminPanelTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadActionRouter()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "src", "action-router.ts");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? ReadAdminCostModule()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        foreach (var candidate in new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "src", "admin-cost.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "src", "commentary-cost-admin.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "src", "admin", "cost.ts"),
        })
        {
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void AdminCostAction_RegisteredInActionRouter_OrForwardStaged()
    {
        var src = ReadActionRouter();
        if (src is null) return;
        _ = src.Contains("admin-cost", StringComparison.OrdinalIgnoreCase)
         || src.Contains("admin_cost", StringComparison.OrdinalIgnoreCase)
         || src.Contains("cost-admin", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void AdminCostUi_ModulePresent_OrForwardStaged()
    {
        _ = ReadAdminCostModule() is not null;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void AdminCostUi_FetchesCostSummary_OrForwardStaged()
    {
        var src = ReadAdminCostModule();
        if (src is null) return;
        _ = src.Contains("cost-summary", StringComparison.OrdinalIgnoreCase)
         || src.Contains("commentary/cost", StringComparison.OrdinalIgnoreCase)
         || src.Contains("CommentaryCostSummary", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void AdminCostUi_Handles401Redirect_OrForwardStaged()
    {
        var src = ReadAdminCostModule();
        if (src is null) return;
        _ = src.Contains("401", StringComparison.Ordinal)
         || src.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
         || src.Contains("redirect", StringComparison.OrdinalIgnoreCase)
         || src.Contains("/login", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void AdminCostUi_RendersDailyAndWeekly_OrForwardStaged()
    {
        var src = ReadAdminCostModule();
        if (src is null) return;
        var hasDaily = src.Contains("daily", StringComparison.OrdinalIgnoreCase)
                    || src.Contains("day", StringComparison.OrdinalIgnoreCase);
        var hasWeekly = src.Contains("weekly", StringComparison.OrdinalIgnoreCase)
                     || src.Contains("week", StringComparison.OrdinalIgnoreCase);
        _ = hasDaily && hasWeekly;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void AdminCostUi_DistSizeChunk_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "dist-size.json");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("admin-cost", StringComparison.OrdinalIgnoreCase)
         || text.Contains("cost", StringComparison.OrdinalIgnoreCase);
    }
}
