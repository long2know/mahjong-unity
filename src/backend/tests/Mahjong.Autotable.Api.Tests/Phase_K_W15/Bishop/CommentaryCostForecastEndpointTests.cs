#if TESTING_SHIM
using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tests.Shims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Hard-asserted contract for the
/// admin-gated <c>GET /api/commentary/cost/forecast</c> endpoint.
///
/// <list type="number">
///   <item>Anonymous → 401.</item>
///   <item>Non-admin → 403.</item>
///   <item>Admin → 200.</item>
///   <item>Envelope carries projectedMonthEndCost,
///         confidence, daysOfDataUsed, projectionMethodology.</item>
///   <item>Methodology echoes the documented literal
///         <c>"linear-extrapolation:days-elapsed"</c>.</item>
///   <item>Confidence "low" when daysOfDataUsed &lt; 3.</item>
///   <item>Confidence "medium" when 3 ≤ daysOfDataUsed &lt; 10.</item>
///   <item>Confidence "high" when daysOfDataUsed ≥ 10.</item>
///   <item><c>?days=N</c> override pins the denominator.</item>
///   <item>Negative <c>?days</c> falls back to computed days.</item>
///   <item>projectedMonthEndCost is a JSON number.</item>
///   <item><c>month</c> field echoes the current YYYY-MM.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class CommentaryCostForecastEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w15-cost-fcast-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Commentary:Model", "gpt-test-mini");
            b.UseSetting("Commentary:CostBudget:MonthlyCapUsd", "100.0");
            b.UseSetting("Commentary:CostBudget:TokensPerDollar", "200000");
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

    private HttpClient NewClient() =>
        _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Anonymous_Returns401()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session-required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task NonAdmin_Returns403()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: null);
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("admin-required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Admin_Returns200()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Envelope_CarriesAllFields()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("projectedMonthEndCost", out _));
        Assert.True(root.TryGetProperty("confidence", out _));
        Assert.True(root.TryGetProperty("daysOfDataUsed", out _));
        Assert.True(root.TryGetProperty("projectionMethodology", out _));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Methodology_EchoesDocumentedLiteral()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("linear-extrapolation:days-elapsed",
            doc.RootElement.GetProperty("projectionMethodology").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Confidence_Low_WhenDaysLessThan3()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast?days=2");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("low", doc.RootElement.GetProperty("confidence").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Confidence_Medium_WhenDays3To9()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast?days=5");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("medium", doc.RootElement.GetProperty("confidence").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Confidence_High_WhenDaysAtLeast10()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast?days=15");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("high", doc.RootElement.GetProperty("confidence").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task DaysOverride_PinsDenominator()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast?days=12");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(12, doc.RootElement.GetProperty("daysOfDataUsed").GetInt32());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task NegativeDays_FallsBackToComputedDays()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast?days=-5");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // -5 is rejected; the endpoint computes days from the
        // current month — must be ≥ 1.
        Assert.True(doc.RootElement.GetProperty("daysOfDataUsed").GetInt32() >= 1);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task ProjectedCost_IsNumber()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Number,
            doc.RootElement.GetProperty("projectedMonthEndCost").ValueKind);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Month_EchoesCurrentYearMonth()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var now = DateTime.UtcNow;
        Assert.Equal($"{now.Year:D4}-{now.Month:D2}",
            doc.RootElement.GetProperty("month").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Model_EchoesConfiguredModel()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/forecast");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("gpt-test-mini", doc.RootElement.GetProperty("model").GetString());
    }
}
#endif
