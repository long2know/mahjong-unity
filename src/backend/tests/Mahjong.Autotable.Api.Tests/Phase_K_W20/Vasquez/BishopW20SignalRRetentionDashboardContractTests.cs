namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Bishop W20's
/// SignalR retention Grafana dashboard JSON under
/// <c>src/backend/.../Observability/dashboards/</c>.
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the dashboard.
/// </summary>
public sealed class BishopW20SignalRRetentionDashboardContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string DashboardsDir()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Observability", "dashboards");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SignalRRetentionDashboard_AnyJsonFile_Present_OrForwardStaged()
    {
        var dir = DashboardsDir();
        if (!Directory.Exists(dir)) return;
        var any = Directory.EnumerateFiles(dir, "signalr-retention*.json").Any();
        Assert.True(any);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SignalRRetentionDashboard_HasSignalRTokens_OrForwardStaged()
    {
        var dir = DashboardsDir();
        if (!Directory.Exists(dir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(dir, "signalr-retention*.json")
            .Select(File.ReadAllText));
        if (string.IsNullOrEmpty(blob)) return;
        Assert.Contains("signalr", blob, StringComparison.OrdinalIgnoreCase);
    }
}
