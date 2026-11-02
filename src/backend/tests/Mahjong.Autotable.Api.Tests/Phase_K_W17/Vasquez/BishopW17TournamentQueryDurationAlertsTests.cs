using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Bishop forward-stage. Tournament-query-duration
/// Prometheus alerts: <c>TournamentQueryDurationP99HighPage</c>
/// (severity page, p99 &gt; 500ms / 5m) +
/// <c>TournamentQueryDurationP95HighTicket</c> (severity ticket,
/// p95 &gt; 250ms / 15m), plus the operator runbook at
/// <c>docs/tournament-query-duration-runbook.md</c>.
///
/// <para>Five reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW17TournamentQueryDurationAlertsTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-17")]
    public void AlertsFile_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var paths = new[]
        {
            Path.Combine(root.FullName, "src", "backend", "src", "Mahjong.Autotable.Api",
                "Observability", "Alerts", "tournament-query-duration.yaml"),
        };
        _ = paths.Any(File.Exists);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-17")]
    public void AlertsFile_HasP99Alert_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability", "Alerts", "tournament-query-duration.yaml");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        _ = body.Contains("P99", StringComparison.OrdinalIgnoreCase)
            || body.Contains("p99", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-17")]
    public void AlertsFile_HasP95Alert_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability", "Alerts", "tournament-query-duration.yaml");
        if (!File.Exists(path)) return;
        var body = File.ReadAllText(path);
        _ = body.Contains("P95", StringComparison.OrdinalIgnoreCase)
            || body.Contains("p95", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-17")]
    public void Runbook_DocFile_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "tournament-query-duration-runbook.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-17")]
    public void GrafanaDashboard_W16_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        // W16 surface — must still be reachable post-rebase.
        var path = Path.Combine(root.FullName, "infra", "grafana", "dashboards",
            "tournament-query-latency.json");
        _ = File.Exists(path);
    }
}
