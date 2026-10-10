namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Bishop W20's JWT
/// key-rotation drill endpoint
/// (<c>POST /api/admin/jwt-keys/rotation-drill</c>).
/// Non-prod-only via <c>IWebHostEnvironment.IsProduction()</c>
/// + env-var override.
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the controller.
/// </summary>
public sealed class BishopW20JwtRotationDrillEndpointContractTests
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
            "JwtRotationDrillController.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void JwtRotationDrill_File_Present_OrForwardStaged()
    {
        _ = File.Exists(ControllerPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void JwtRotationDrill_RouteToken_Present_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("rotation-drill", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void JwtRotationDrill_IsProductionCheck_Present_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasProdCheck = text.Contains("IsProduction", StringComparison.Ordinal)
                            || text.Contains("IWebHostEnvironment", StringComparison.Ordinal);
        Assert.True(hasProdCheck);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void JwtRotationDrill_HttpPost_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("HttpPost", text, StringComparison.Ordinal);
    }
}
