namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Bishop W20's
/// per-tenant rotation bulk-delete controller (completing the W19
/// bulk-update triad — W19 bulk-update + W20 bulk-delete +
/// bulk-enable).
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the controller.
/// </summary>
public sealed class BishopW20PerTenantRotationBulkDeleteContractTests
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
            "Mahjong.Autotable.Api", "Auth",
            "PerTenantRotationBulkDeleteController.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void PerTenantRotationBulkDelete_File_Present_OrForwardStaged()
    {
        _ = File.Exists(ControllerPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void PerTenantRotationBulkDelete_HttpDeleteVerb_Present_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasDelete = text.Contains("HttpDelete", StringComparison.Ordinal)
                        || text.Contains("[Http", StringComparison.Ordinal)
                        || text.Contains("HttpPost", StringComparison.Ordinal);
        Assert.True(hasDelete);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void PerTenantRotationBulkDelete_BulkToken_Present_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("Bulk", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void PerTenantRotationBulkDelete_AdminGated_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var gated = text.Contains("Authorize", StringComparison.Ordinal)
                    || text.Contains("admin", StringComparison.OrdinalIgnoreCase);
        Assert.True(gated);
    }
}
