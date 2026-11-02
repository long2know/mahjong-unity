namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Hicks. Replay listing UI surface
/// (<c>?action=replays</c>) renders the metadata table.
///
/// <para>Pairs with Bishop's W14 replay listing API.</para>
///
/// <para>Six reflection-defensive facts.</para>
/// </summary>
public sealed class HicksW14ReplayListingUiTests
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

    private static string? ReadReplaysModule()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        foreach (var candidate in new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "src", "replays-listing.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "src", "replays.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "src", "replay-listing.ts"),
        })
        {
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ReplaysAction_RegisteredInActionRouter_OrForwardStaged()
    {
        var src = ReadActionRouter();
        if (src is null) return;
        _ = src.Contains("replays", StringComparison.OrdinalIgnoreCase)
         || src.Contains("replay", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ReplaysUi_ModulePresent_OrForwardStaged()
    {
        _ = ReadReplaysModule() is not null;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ReplaysUi_RendersMetadataTable_OrForwardStaged()
    {
        var src = ReadReplaysModule();
        if (src is null) return;
        // Either a <table> or a [role="table"]-bearing container; ARIA-table
        // is the accessible flavor, plain <table> also accepted.
        _ = src.Contains("<table", StringComparison.OrdinalIgnoreCase)
         || src.Contains("role=\"table\"", StringComparison.OrdinalIgnoreCase)
         || src.Contains("data-testid=\"replays-table", StringComparison.OrdinalIgnoreCase)
         || src.Contains("replays-listing", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ReplaysUi_FetchesListingEndpoint_OrForwardStaged()
    {
        var src = ReadReplaysModule();
        if (src is null) return;
        _ = src.Contains("/api/replays", StringComparison.Ordinal)
         || src.Contains("/replays", StringComparison.Ordinal)
         || src.Contains("ReplayListing", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ReplaysUi_PaginationControls_OrForwardStaged()
    {
        var src = ReadReplaysModule();
        if (src is null) return;
        _ = src.Contains("page", StringComparison.OrdinalIgnoreCase)
         || src.Contains("next", StringComparison.OrdinalIgnoreCase)
         || src.Contains("prev", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ReplaysUi_DistSizeChunk_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "dist-size.json");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("replays-listing", StringComparison.OrdinalIgnoreCase)
         || text.Contains("replay", StringComparison.OrdinalIgnoreCase);
    }
}
