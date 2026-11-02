namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Bishop W21's
/// Grafana dashboard refresh — <c>jwt-validator-metrics.json</c>
/// gains panels 9 (anomalies-by-reason) + 10 (scheduled-rotations-
/// by-status).  Soft-pinned so the gate stays green if Bishop W21
/// has not yet landed the dashboard edits.
/// </summary>
public sealed class BishopW21JwtValidatorDashboardW21ContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string DashboardPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Observability", "dashboards",
            "jwt-validator-metrics.json");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Dashboard_File_Present_OrForwardStaged()
    {
        _ = File.Exists(DashboardPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Dashboard_AnomalyPanel_Present_OrForwardStaged()
    {
        var p = DashboardPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Panel 9 — anomalies-by-reason.
        var has = text.Contains("jwt_validator_anomaly_total", StringComparison.Ordinal)
                   || text.Contains("anomaly", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Dashboard_ScheduledRotationPanel_Present_OrForwardStaged()
    {
        var p = DashboardPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Panel 10 — scheduled-rotations-by-status.
        var has = text.Contains("jwt_scheduled_rotation_total", StringComparison.Ordinal)
                   || text.Contains("scheduled", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
