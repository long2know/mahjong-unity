#if TESTING_SHIM
using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Spectator;
using Mahjong.Autotable.Api.Tests.Shims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Bishop;

/// <summary>
/// Phase K Wave 14 — Bishop. Hard-asserted contract for the
/// admin-gated <c>GET /api/spectator/handoff/audit</c> endpoint.
///
/// <list type="number">
///   <item>Anonymous → 401 with <c>{ "error": "session-required" }</c>.</item>
///   <item>Non-admin session → 403 with <c>{ "error": "admin-required" }</c>.</item>
///   <item>Admin session → 200 with the paginated envelope.</item>
///   <item><c>gameId</c> filter narrows the row set.</item>
///   <item><c>from</c> / <c>to</c> filters narrow on IssuedAt.</item>
///   <item>Bad timestamp → 400.</item>
///   <item><c>limit</c> clamps to <c>MaxPageSize</c>.</item>
///   <item><c>skip</c> + <c>limit</c> slice correctly.</item>
///   <item>Page-size option <see cref="SpectatorHandoffAuditOptions.PageSize"/>
///         feeds the response envelope.</item>
///   <item>Default page-size constants match the documented values.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class SpectatorAuditQueryEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w14-spec-audit-{Guid.NewGuid():N}.db");
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

    private HttpClient NewClient() =>
        _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task SeedAsync(Guid? gameId = null, int count = 1, DateTime? whenStart = null)
    {
        var store = _factory!.Services.GetRequiredService<ISpectatorHandoffAuditStore>();
        var when = whenStart ?? DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            await store.InsertAsync(new SpectatorHandoffAuditRecord
            {
                UserId = $"player-{i}",
                GameId = gameId ?? Guid.NewGuid(),
                TokenJti = Guid.NewGuid().ToString("D"),
                IssuedAt = when.AddSeconds(i),
                Scope = "spectator:test",
                ClientIp = "127.0.0.1",
                UserAgent = "test/1.0",
            });
        }
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Options_DefaultPageSizeIs50()
    {
        Assert.Equal(50, SpectatorHandoffAuditOptions.DefaultPageSize);
        Assert.Equal(50, new SpectatorHandoffAuditOptions().PageSize);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Options_MaxPageSizeIs200()
    {
        Assert.Equal(200, SpectatorHandoffAuditOptions.MaxPageSize);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Anonymous_Returns401()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/spectator/handoff/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session-required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task NonAdmin_Returns403()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: null);
        using var resp = await client.GetAsync("/api/spectator/handoff/audit");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("admin-required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task ModeratorRole_Returns403()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "moderator");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_Returns200_WithEnvelope()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("items", out _));
        Assert.True(doc.RootElement.TryGetProperty("count", out _));
        Assert.True(doc.RootElement.TryGetProperty("skip", out _));
        Assert.True(doc.RootElement.TryGetProperty("limit", out _));
        Assert.True(doc.RootElement.TryGetProperty("pageSize", out _));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_EmptyStore_ReturnsZeroCount()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_ReturnsSeededRow()
    {
        var gid = Guid.NewGuid();
        await SeedAsync(gid, 1);
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        var row = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(gid, row.GetProperty("gameId").GetGuid());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_GameIdFilter_Narrows()
    {
        var gid = Guid.NewGuid();
        await SeedAsync(gameId: gid, count: 1);
        await SeedAsync(gameId: Guid.NewGuid(), count: 3);
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync($"/api/spectator/handoff/audit?gameId={gid:D}");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_FromToFilters_NarrowOnIssuedAt()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(gameId: Guid.NewGuid(), count: 1, whenStart: anchor.AddHours(-2));
        await SeedAsync(gameId: Guid.NewGuid(), count: 1, whenStart: anchor);
        await SeedAsync(gameId: Guid.NewGuid(), count: 1, whenStart: anchor.AddHours(2));
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        var from = anchor.AddHours(-1).ToString("O");
        var to = anchor.AddHours(1).ToString("O");
        using var resp = await client.GetAsync(
            $"/api/spectator/handoff/audit?from={from}&to={to}");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_BadFrom_Returns400()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit?from=not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_BadTo_Returns400()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit?to=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_LimitClampsToMax()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit?limit=5000");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(SpectatorHandoffAuditOptions.MaxPageSize,
            doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_LimitClampsToMin()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit?limit=0");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_SkipAndLimit_Slice()
    {
        await SeedAsync(count: 5);
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit?skip=2&limit=2");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("skip").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_DefaultPageSize_Surfaced()
    {
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(SpectatorHandoffAuditOptions.DefaultPageSize,
            doc.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Admin_NegativeSkip_ClampsToZero()
    {
        await SeedAsync(count: 1);
        using var client = NewClient();
        client.WithDirectSession(_factory!.Services, Guid.NewGuid(), role: "admin");
        using var resp = await client.GetAsync("/api/spectator/handoff/audit?skip=-10");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("skip").GetInt32());
    }
}
#endif
