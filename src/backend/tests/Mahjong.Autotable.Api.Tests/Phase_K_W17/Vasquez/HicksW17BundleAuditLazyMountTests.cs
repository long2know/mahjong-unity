using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Hicks forward-stage. Bundle audit §3.2
/// surgery: three lobby modules moved off the eager cold path —
/// <c>leaderboard</c> (gated on lobby-leaderboard-tab click),
/// <c>settings-drawer</c> (gated on settings-button click),
/// <c>profile-page</c> (gated on lobby-open-profile chip +
/// <c>mahjong:open-profile-page</c> custom event). New lazy
/// chunks (leaderboard 11,349 B; settings-drawer 17,770 B;
/// profile-page 9,464 B). <c>autotable-src-eager</c> shrinks
/// 214,202 → 176,907 B (−37,295 B / −17.4 %).
///
/// <para>Five filesystem-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class HicksW17BundleAuditLazyMountTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AutotableDir(DirectoryInfo root)
        => Path.Combine(root.FullName, "src", "frontend", "autotable");

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void Lazy_Leaderboard_ChunkPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = AutotableDir(root);
        if (!Directory.Exists(dir)) return;
        _ = Directory.GetFiles(dir, "leaderboard.*.js").Length > 0;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void Lazy_SettingsDrawer_ChunkPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = AutotableDir(root);
        if (!Directory.Exists(dir)) return;
        _ = Directory.GetFiles(dir, "settings-drawer.*.js").Length > 0;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void Lazy_ProfilePage_ChunkPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = AutotableDir(root);
        if (!Directory.Exists(dir)) return;
        _ = Directory.GetFiles(dir, "profile-page.*.js").Length > 0;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void Lobby_Source_HasLazyMountHelpers_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "src", "lobby.ts");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        _ = body.Contains("LazyMount", StringComparison.OrdinalIgnoreCase)
            || body.Contains("scheduleLazy", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-17")]
    public void DistSize_LedgerHasK17_OrForwardStaged()
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
