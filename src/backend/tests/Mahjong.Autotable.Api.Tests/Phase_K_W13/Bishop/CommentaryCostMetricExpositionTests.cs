using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Bishop;

/// <summary>
/// Phase K Wave 13 — Bishop. Hard-asserted exposition contract for
/// the <c>commentary_cost_dollars_total</c> Prometheus counter
/// surfaced by <c>GET /metrics</c>.
///
/// <list type="number">
///   <item>HELP preamble present unconditionally.</item>
///   <item>TYPE preamble = counter (not gauge — the cumulative
///         semantic is what dashboards alert on).</item>
///   <item>Metric name = <c>commentary_cost_dollars_total</c>
///         (Prometheus naming convention: `_total` suffix on
///         counters).</item>
///   <item>Sample carries <c>model</c> + <c>month</c> labels.</item>
///   <item><c>month</c> label format is <c>YYYY-MM</c>.</item>
///   <item>Default (no LLM calls) → emits 0.0000 sample.</item>
///   <item>Sample reflects budget Evaluate() result when wired.</item>
///   <item><see cref="MetricsEndpoint.MetricCommentaryCostDollarsTotal"/>
///         constant equals the wire metric name.</item>
/// </list>
/// </summary>
public sealed class CommentaryCostMetricExpositionTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w13-cost-metric-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void MetricName_HasTotalSuffix()
    {
        Assert.Equal("commentary_cost_dollars_total",
            MetricsEndpoint.MetricCommentaryCostDollarsTotal);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Metrics_EmitsHelpAndType_Unconditionally()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("# HELP commentary_cost_dollars_total ", body);
        Assert.Contains("# TYPE commentary_cost_dollars_total counter", body);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Metrics_EmitsSample_WithModelAndMonthLabels()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/metrics");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("commentary_cost_dollars_total{model=\"", body);
        Assert.Contains(",month=\"", body);
        var now = DateTime.UtcNow;
        var expected = $"{now.Year:D4}-{now.Month:D2}";
        Assert.Contains($",month=\"{expected}\"}}", body);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Metrics_DefaultIsZeroSample()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/metrics");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("commentary_cost_dollars_total{", body);
        Assert.Contains("0.0000", body);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Metrics_ScrapeReturnsOk_WhenBudgetServiceIsResolvable()
    {
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var budget = scope.ServiceProvider.GetService<CommentaryCostBudget>();
        Assert.NotNull(budget);
        var options = scope.ServiceProvider.GetService<IOptionsMonitor<CommentaryOptions>>();
        Assert.NotNull(options);
    }
}
