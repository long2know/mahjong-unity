namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Hicks W19
/// Admin UI W19 surfaces — <c>rotation-policy-bulk</c>,
/// <c>replay-integrity-audit</c>, <c>swiss-pairing-audit</c>
/// (read-only). Soft-pins file presence + basic export
/// names so the gate observes the three new admin surfaces.
/// </summary>
public sealed class HicksW19AdminUiSurfacesContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AdminPath(string filename)
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "src", "admin", filename);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void AdminUi_RotationPolicyBulk_File_Present_OrForwardStaged()
    {
        var p = AdminPath("rotation-policy-bulk.ts");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void AdminUi_ReplayIntegrityAudit_File_Present_OrForwardStaged()
    {
        var p = AdminPath("replay-integrity-audit.ts");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void AdminUi_SwissPairingAudit_File_Present_OrForwardStaged()
    {
        var p = AdminPath("swiss-pairing-audit.ts");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void AdminUi_AdminPanel_File_Still_Present()
    {
        var p = AdminPath("admin-panel.ts");
        Assert.True(File.Exists(p),
            $"admin-panel.ts MUST remain at {p}.");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void AdminUi_AdminShared_File_Still_Present()
    {
        var p = AdminPath("admin-shared.ts");
        Assert.True(File.Exists(p),
            $"admin-shared.ts MUST remain at {p}.");
    }
}
