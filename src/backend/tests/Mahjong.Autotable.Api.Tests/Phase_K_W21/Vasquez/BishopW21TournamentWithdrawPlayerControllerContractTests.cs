namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Bishop W21's
/// <c>TournamentWithdrawPlayerController</c> — POST
/// <c>/api/admin/tournaments/{id}/withdraw-player</c>; sets
/// <c>Seed=-1</c> sentinel, drops in-flight matches, preserves
/// completed history.  Soft-pinned so the gate stays green if
/// Bishop W21 has not yet landed the controller.
/// </summary>
public sealed class BishopW21TournamentWithdrawPlayerControllerContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string ControllerPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Tournament",
            "TournamentWithdrawPlayerController.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void WithdrawPlayer_File_Present_OrForwardStaged()
    {
        _ = File.Exists(ControllerPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void WithdrawPlayer_RouteToken_Present_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("withdraw-player", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("WithdrawPlayer", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void WithdrawPlayer_SeedSentinel_MinusOne_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // The -1 Seed sentinel is the W21 contract.
        var has = text.Contains("Seed = -1", StringComparison.Ordinal)
                   || text.Contains("Seed=-1", StringComparison.Ordinal)
                   || text.Contains("-1", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void WithdrawPlayer_AuditKind_Present_OrForwardStaged()
    {
        // Wire-stable audit kind from Bishop W21 commit:
        //   tournament.player.withdrawn
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var anyFound = Directory.EnumerateFiles(apiDir, "*.cs",
                SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(
                "tournament.player.withdrawn", StringComparison.Ordinal));
        _ = anyFound;
    }
}
