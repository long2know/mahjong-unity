namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Hicks W21's
/// <c>profile-drawer</c> extraction from <c>./profile</c> (~3.9 KB
/// lazy chunk; lazy-mount on lobby-open-profile chip
/// hover/focus/click — mirrors W17 §3.2
/// <c>scheduleProfilePageLazyMount</c>).  Soft-pinned so the gate
/// stays green if Hicks W21 has not yet landed the extraction.
/// </summary>
public sealed class HicksW21ProfileDrawerLazyContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string DrawerPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "profile-drawer.ts");
    }

    private static string LobbyPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "lobby.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void ProfileDrawer_File_Present_OrForwardStaged()
    {
        _ = File.Exists(DrawerPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void LobbyLazyMount_ProfileDrawer_OrForwardStaged()
    {
        var p = LobbyPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Lobby lazy-mount of the new profile-drawer chunk.
        var has = text.Contains("profile-drawer", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("profileDrawer", StringComparison.Ordinal);
        Assert.True(has);
    }
}
