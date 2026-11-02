using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Contract tests for the Grafana
/// dashboard JSON shipped under
/// <c>src/backend/src/Mahjong.Autotable.Api/Observability/dashboards/tournament-query-duration.json</c>.
/// Asserts the dashboard exists, is valid JSON, carries the
/// required panels + alert thresholds, and references the
/// canonical metric names emitted by
/// <c>TournamentQueryLatencyMetrics</c>.
/// </summary>
public sealed class GrafanaDashboardContractTests
{
    private static string DashboardPath()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        return Path.Combine(root!,
            "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability", "dashboards", "tournament-query-duration.json");
    }

    private static JsonDocument LoadDashboard()
    {
        var path = DashboardPath();
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        return JsonDocument.Parse(json);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_Exists()
    {
        Assert.True(File.Exists(DashboardPath()));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_ValidJson()
    {
        using var doc = LoadDashboard();
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasUid()
    {
        using var doc = LoadDashboard();
        Assert.Equal("bishop-tournament-query-duration", doc.RootElement.GetProperty("uid").GetString());
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasTitle()
    {
        using var doc = LoadDashboard();
        Assert.Equal("Tournament Query Duration", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasBishopWaveTag()
    {
        using var doc = LoadDashboard();
        var tags = doc.RootElement.GetProperty("tags").EnumerateArray()
            .Select(t => t.GetString()).ToArray();
        Assert.Contains("bishop", tags);
        Assert.Contains("wave-16", tags);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasPanels()
    {
        using var doc = LoadDashboard();
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray().ToArray();
        Assert.True(panels.Length >= 5, $"Expected ≥5 panels, got {panels.Length}");
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasP50Panel()
    {
        var json = File.ReadAllText(DashboardPath());
        Assert.Contains("histogram_quantile(0.50", json);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasP95Panel()
    {
        var json = File.ReadAllText(DashboardPath());
        Assert.Contains("histogram_quantile(0.95", json);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasP99Panel()
    {
        var json = File.ReadAllText(DashboardPath());
        Assert.Contains("histogram_quantile(0.99", json);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasRequestRatePanel()
    {
        var json = File.ReadAllText(DashboardPath());
        Assert.Contains("tournament_query_duration_seconds_count", json);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_ReferencesCanonicalMetricName()
    {
        var json = File.ReadAllText(DashboardPath());
        Assert.Contains("tournament_query_duration_seconds_bucket", json);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasP99AlertThreshold500ms()
    {
        var json = File.ReadAllText(DashboardPath());
        Assert.Contains("tournament-query-p99-over-500ms", json);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasAlertAnnotation()
    {
        using var doc = LoadDashboard();
        Assert.True(doc.RootElement.TryGetProperty("annotations", out _));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasEndpointTemplateVariable()
    {
        using var doc = LoadDashboard();
        var list = doc.RootElement.GetProperty("templating").GetProperty("list");
        var names = list.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("endpoint", names);
        Assert.Contains("page_size_bucket", names);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasPhaseKWave16Metadata()
    {
        using var doc = LoadDashboard();
        Assert.True(doc.RootElement.TryGetProperty("_phaseKWave16Bishop", out _));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Dashboard_HasBurnRatePanel()
    {
        var json = File.ReadAllText(DashboardPath());
        Assert.Contains("burn", json);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                && Directory.Exists(Path.Combine(dir.FullName, ".squad")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
