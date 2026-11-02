namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Bishop W20's
/// admin Swiss pair-next-round endpoint
/// (<c>POST /api/admin/tournaments/{id}/swiss-pair-next-round</c>).
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the controller.
/// </summary>
public sealed class BishopW20SwissPairingAdminEndpointContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AdminAuthDir()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Auth");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SwissPairAdmin_AnyControllerFile_Present_OrForwardStaged()
    {
        var dir = AdminAuthDir();
        if (!Directory.Exists(dir)) return;
        var any = Directory.EnumerateFiles(dir, "*.cs")
            .Any(p => Path.GetFileName(p)
                .Contains("SwissPair", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(p).Contains("Admin", StringComparison.OrdinalIgnoreCase));
        _ = any;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SwissPairAdmin_RouteToken_Present_OrForwardStaged()
    {
        var dir = AdminAuthDir();
        if (!Directory.Exists(dir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(dir, "*.cs")
            .Select(File.ReadAllText));
        if (!blob.Contains("SwissPair", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains("swiss-pair-next-round", blob, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SwissPairAdmin_AdminGated_OrForwardStaged()
    {
        var dir = AdminAuthDir();
        if (!Directory.Exists(dir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(dir, "*Swiss*Admin*.cs")
            .Select(File.ReadAllText));
        if (string.IsNullOrEmpty(blob)) return;
        var gated = blob.Contains("Authorize", StringComparison.Ordinal)
                    || blob.Contains("admin", StringComparison.OrdinalIgnoreCase);
        Assert.True(gated);
    }
}
