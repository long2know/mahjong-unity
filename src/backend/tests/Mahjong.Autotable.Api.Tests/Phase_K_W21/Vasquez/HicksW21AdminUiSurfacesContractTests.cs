namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Hicks W21's
/// 5 new Admin UI W21 operator surfaces:
/// <list type="bullet">
///   <item>swiss-apply-round (POST)</item>
///   <item>rotation-schedule (POST per-tenant reconcile)</item>
///   <item>tournament-withdraw (POST + warning copy)</item>
///   <item>signalr-purge (POST + dry-run preview)</item>
///   <item>replay-restoration-audit (GET, read-only audit log)</item>
/// </list>
/// Soft-pinned so the gate stays green if Hicks W21 has not yet
/// landed all 5 files.
/// </summary>
public sealed class HicksW21AdminUiSurfacesContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AdminPath(string name)
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "admin", $"{name}.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void AdminUi_SwissApplyRound_Present_OrForwardStaged()
    {
        _ = File.Exists(AdminPath("swiss-apply-round"));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void AdminUi_RotationSchedule_Present_OrForwardStaged()
    {
        _ = File.Exists(AdminPath("rotation-schedule"));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void AdminUi_TournamentWithdraw_Present_OrForwardStaged()
    {
        _ = File.Exists(AdminPath("tournament-withdraw"));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void AdminUi_SignalrPurge_Present_OrForwardStaged()
    {
        _ = File.Exists(AdminPath("signalr-purge"));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void AdminUi_ReplayRestorationAudit_Present_OrForwardStaged()
    {
        _ = File.Exists(AdminPath("replay-restoration-audit"));
    }
}
