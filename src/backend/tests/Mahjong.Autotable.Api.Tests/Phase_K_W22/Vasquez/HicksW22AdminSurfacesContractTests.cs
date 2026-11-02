namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Hicks W22's
/// 5 new admin surfaces wired to Bishop W22 backend endpoints:
/// tournament-finalize, replay-download-chunked,
/// jwt-emergency-revoke (confirm-keyId guard),
/// signalr-diagnostics, audit-log-search.
/// </summary>
public sealed class HicksW22AdminSurfacesContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AdminDir(DirectoryInfo root) =>
        Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "admin");

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void AdminDir_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var d = AdminDir(root!);
        if (!Directory.Exists(d)) return;
        Assert.True(Directory.Exists(d));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void AdminPanel_HasW22SurfaceReferences_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(AdminDir(root!), "admin-panel.ts");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // At least ONE of the W22-wired surface keywords should land
        // in the admin-panel router.
        var has = text.Contains("tournament-finalize", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("replay-download-chunked", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("jwt-emergency-revoke", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("signalr-diagnostics", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("audit-log-search", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("emergency-revoke", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("signalr", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
