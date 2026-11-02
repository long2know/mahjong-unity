using System.Text.Json;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Contract tests for the JWT
/// validator metrics Grafana dashboard JSON. Pins the
/// dashboard's UID, title, tags, panel list, and metric
/// references so a future refactor cannot silently break the
/// W19 ops surface.
/// </summary>
public sealed class JwtValidatorMetricsDashboardTests
{
    private const string DashboardRelative =
        "src/backend/src/Mahjong.Autotable.Api/Observability/dashboards/jwt-validator-metrics.json";

    private static string LocateDashboard()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName, DashboardRelative);
            if (File.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate Grafana dashboard at {DashboardRelative}.");
    }

    private static JsonDocument LoadDashboard()
    {
        var path = LocateDashboard();
        var text = File.ReadAllText(path);
        return JsonDocument.Parse(text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_FileExists()
    {
        Assert.True(File.Exists(LocateDashboard()));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_IsValidJson()
    {
        using var doc = LoadDashboard();
        Assert.NotNull(doc);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasExpectedUid()
    {
        using var doc = LoadDashboard();
        Assert.Equal("bishop-jwt-validator-metrics", doc.RootElement.GetProperty("uid").GetString());
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasTitleWithJwtKeyword()
    {
        using var doc = LoadDashboard();
        var title = doc.RootElement.GetProperty("title").GetString() ?? "";
        Assert.Contains("JWT", title);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasBishopAndWave19Tags()
    {
        using var doc = LoadDashboard();
        var tags = doc.RootElement.GetProperty("tags").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("bishop", tags);
        Assert.Contains("wave-19", tags);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasPanelsList()
    {
        using var doc = LoadDashboard();
        var panels = doc.RootElement.GetProperty("panels");
        Assert.True(panels.GetArrayLength() >= 6, "Expected at least 6 panels (p50/p95/p99 × issue+validator).");
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasIssueDurationMetricReference()
    {
        var text = File.ReadAllText(LocateDashboard());
        Assert.Contains("jwt_issue_duration_seconds_bucket", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasValidatorCheckDurationMetricReference()
    {
        var text = File.ReadAllText(LocateDashboard());
        Assert.Contains("jwt_validator_check_duration_seconds_bucket", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasP50P95AndP99Targets()
    {
        var text = File.ReadAllText(LocateDashboard());
        Assert.Contains("histogram_quantile(0.50", text);
        Assert.Contains("histogram_quantile(0.95", text);
        Assert.Contains("histogram_quantile(0.99", text);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Dashboard_HasTenantTemplateVariable()
    {
        using var doc = LoadDashboard();
        var list = doc.RootElement.GetProperty("templating").GetProperty("list");
        var names = list.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("tenant", names);
    }
}
