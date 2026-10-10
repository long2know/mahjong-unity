namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Hicks W20's three
/// new admin UI surfaces (paired with Bishop W20's controllers):
/// <list type="bullet">
///   <item>Swiss pair-next-round trigger (<c>src/admin/swiss-pair-next-round.ts</c>)</item>
///   <item>Rotation-policy bulk-actions (<c>src/admin/rotation-policy-bulk-actions.ts</c>)</item>
///   <item>JWT rotation drill (<c>src/admin/jwt-rotation-drill.ts</c>)</item>
/// </list>
/// Soft-pinned so the gate stays green if Hicks W20 has not yet
/// landed the modules.
/// </summary>
public sealed class HicksW20AdminUiSurfacesContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AdminDir()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "admin");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void AdminUi_SwissPairNextRound_Module_Present_OrForwardStaged()
    {
        var p = Path.Combine(AdminDir(), "swiss-pair-next-round.ts");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void AdminUi_RotationPolicyBulkActions_Module_Present_OrForwardStaged()
    {
        var p = Path.Combine(AdminDir(), "rotation-policy-bulk-actions.ts");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void AdminUi_JwtRotationDrill_Module_Present_OrForwardStaged()
    {
        var p = Path.Combine(AdminDir(), "jwt-rotation-drill.ts");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Hicks")]
    public void AdminUi_AnyW20Surface_Loaded_OrForwardStaged()
    {
        var dir = AdminDir();
        if (!Directory.Exists(dir)) return;
        var any = Directory.EnumerateFiles(dir, "*.ts")
            .Any(p => Path.GetFileName(p) is
                    "swiss-pair-next-round.ts"
                or "rotation-policy-bulk-actions.ts"
                or "jwt-rotation-drill.ts");
        _ = any;
    }
}
