#if TESTING_SHIM
using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Tests.Shims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Bishop;

/// <summary>
/// Phase K Wave 14 — Bishop. Hard-asserted contract for the
/// admin-gated <c>GET /api/commentary/cost/summary</c> endpoint.
///
/// <list type="number">
///   <item>Anonymous → 401 with <c>{ "error": "session-required" }</c>.</item>
///   <item>Non-admin session → 403 with <c>{ "error": "admin-required" }</c>.</item>
///   <item>Admin session → 200 with the canonical envelope:
///         <c>currentMonthCost</c>, <c>budgetCapUsd</c>,
///         <c>percentUsed</c>, <c>state</c>, <c>model</c>,
///         <c>month</c>, <c>byModel</c>.</item>
///   <item><c>byModel</c> is a single-entry array carrying the
///         configured <c>Commentary:Model</c>.</item>
///   <item>Budget unwired → endpoint still returns 200 with a
///         zeroed envelope (defence-in-depth).</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class CommentaryCostSummaryEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w14-cost-{Guid.NewGuid():N}.db");
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

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Anonymous_Returns401()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session-required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task NonAdmin_Returns403()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: null);
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("admin-required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_Returns200()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_EnvelopeCarriesAllFields()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("currentMonthCost", out _));
        Assert.True(doc.RootElement.TryGetProperty("budgetCapUsd", out _));
        Assert.True(doc.RootElement.TryGetProperty("percentUsed", out _));
        Assert.True(doc.RootElement.TryGetProperty("monthlyTokens", out _));
        Assert.True(doc.RootElement.TryGetProperty("tokensPerDollar", out _));
        Assert.True(doc.RootElement.TryGetProperty("state", out _));
        Assert.True(doc.RootElement.TryGetProperty("model", out _));
        Assert.True(doc.RootElement.TryGetProperty("month", out _));
        Assert.True(doc.RootElement.TryGetProperty("byModel", out _));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_ByModel_IsSingleEntryArray()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var byModel = doc.RootElement.GetProperty("byModel");
        Assert.Equal(JsonValueKind.Array, byModel.ValueKind);
        Assert.Equal(1, byModel.GetArrayLength());
        Assert.True(byModel[0].TryGetProperty("model", out _));
        Assert.True(byModel[0].TryGetProperty("cost", out _));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_ModelEchoesConfiguredValue()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("gpt-test-mini",
            doc.RootElement.GetProperty("model").GetString());
        var byModel = doc.RootElement.GetProperty("byModel")[0];
        Assert.Equal("gpt-test-mini", byModel.GetProperty("model").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_MonthIsCurrentYearMonth()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var month = doc.RootElement.GetProperty("month").GetString();
        var now = DateTime.UtcNow;
        Assert.Equal($"{now.Year:D4}-{now.Month:D2}", month);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_BudgetCapEchoesConfiguredValue()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(100m,
            doc.RootElement.GetProperty("budgetCapUsd").GetDecimal());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_StateIsHealthyByDefault()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Healthy",
            doc.RootElement.GetProperty("state").GetString());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_PercentUsedIsZeroForFreshMeter()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/commentary/cost/summary");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0d, doc.RootElement.GetProperty("percentUsed").GetDouble());
    }
}
#endif
