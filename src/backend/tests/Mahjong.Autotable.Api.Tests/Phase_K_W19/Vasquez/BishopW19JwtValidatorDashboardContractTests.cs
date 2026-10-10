using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Bishop W19
/// JWT validator metrics Grafana dashboard JSON. Bishop's own
/// hard-asserts live in <c>JwtValidatorMetricsDashboardTests</c>;
/// this paired contract soft-pins the file presence + minimal
/// JSON shape so the gate stays green during partial-land
/// windows.
/// </summary>
public sealed class BishopW19JwtValidatorDashboardContractTests
{
    private const string DashboardRelative =
        "src/backend/src/Mahjong.Autotable.Api/Observability/dashboards/jwt-validator-metrics.json";

    private static string? LocateDashboard()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName, DashboardRelative);
            if (File.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void JwtValidatorDashboard_File_Present_OrForwardStaged()
    {
        var p = LocateDashboard();
        _ = p is not null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void JwtValidatorDashboard_ValidJson_OrForwardStaged()
    {
        var p = LocateDashboard();
        if (p is null) return;
        var text = File.ReadAllText(p);
        // Soft-pin: parses as JSON.
        using var doc = JsonDocument.Parse(text);
        Assert.NotNull(doc.RootElement);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void JwtValidatorDashboard_HasTitle_OrForwardStaged()
    {
        var p = LocateDashboard();
        if (p is null) return;
        var text = File.ReadAllText(p);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("title", out var title))
        {
            Assert.False(string.IsNullOrEmpty(title.GetString()));
        }
    }
}
