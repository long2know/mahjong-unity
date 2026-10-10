using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Bishop;

/// <summary>
/// Phase K Wave 14 — Bishop. Hard-asserted contract for the
/// paginated <c>GET /api/tournaments/{id}/brackets</c> endpoint.
///
/// <list type="number">
///   <item>Anonymous → 200 (endpoint is public).</item>
///   <item>Empty tournament → 200 with <c>items.length == 0</c>.</item>
///   <item>Seeded rows surface in <c>(RoundNumber, MatchSlot)</c>
///         ascending order.</item>
///   <item><c>skip</c> + <c>limit</c> slice correctly.</item>
///   <item><c>limit</c> clamps to <see cref="BracketQueryOptions.MaxPageSize"/>.</item>
///   <item><c>pageSize</c> in envelope reflects the configured
///         default.</item>
///   <item>Default page-size constants match the documented
///         values (50 default, 200 max).</item>
///   <item>Items carry the W12+W13 columns (id, roundNumber,
///         matchSlot, seedA, seedB, winnerSeed, status,
///         completedAt).</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class BracketQueryEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w14-brk-{Guid.NewGuid():N}.db");
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

    private async Task SeedAsync(Guid tournamentId, params (int Round, int Slot, string A, string B)[] rows)
    {
        var store = _factory!.Services.GetRequiredService<IBracketStore>();
        foreach (var r in rows)
        {
            await store.UpsertAsync(new BracketRecord
            {
                TournamentId = tournamentId,
                RoundNumber = r.Round,
                MatchSlot = r.Slot,
                SeedA = r.A,
                SeedB = r.B,
                Status = "pending",
            });
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Options_DefaultPageSizeIs50()
    {
        Assert.Equal(50, BracketQueryOptions.DefaultPageSize);
        Assert.Equal(50, new BracketQueryOptions().PageSize);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Options_MaxPageSizeIs200()
    {
        Assert.Equal(200, BracketQueryOptions.MaxPageSize);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Anonymous_Returns200()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{Guid.NewGuid():D}/brackets");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task EmptyTournament_Returns200_ZeroCount()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{Guid.NewGuid():D}/brackets");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task EnvelopeCarriesPageSize()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{Guid.NewGuid():D}/brackets");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("pageSize", out _));
        Assert.True(doc.RootElement.TryGetProperty("skip", out _));
        Assert.True(doc.RootElement.TryGetProperty("limit", out _));
        Assert.True(doc.RootElement.TryGetProperty("totalCount", out _));
        Assert.True(doc.RootElement.TryGetProperty("tournamentId", out _));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task SeededRows_ReturnedInRoundSlotOrder()
    {
        var tid = Guid.NewGuid();
        // Insert out-of-order; expect ordered output.
        await SeedAsync(tid,
            (2, 0, "winner-1", "winner-2"),
            (1, 1, "player-3", "player-4"),
            (1, 0, "player-1", "player-2"));
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{tid:D}/brackets");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(1, items[0].GetProperty("roundNumber").GetInt32());
        Assert.Equal(0, items[0].GetProperty("matchSlot").GetInt32());
        Assert.Equal(1, items[1].GetProperty("roundNumber").GetInt32());
        Assert.Equal(1, items[1].GetProperty("matchSlot").GetInt32());
        Assert.Equal(2, items[2].GetProperty("roundNumber").GetInt32());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task SeededRow_CarriesAllColumns()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid, (1, 0, "player-1", "player-2"));
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{tid:D}/brackets");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement.GetProperty("items")[0];
        Assert.True(row.TryGetProperty("id", out _));
        Assert.True(row.TryGetProperty("tournamentId", out _));
        Assert.True(row.TryGetProperty("roundNumber", out _));
        Assert.True(row.TryGetProperty("matchSlot", out _));
        Assert.True(row.TryGetProperty("seedA", out _));
        Assert.True(row.TryGetProperty("seedB", out _));
        Assert.True(row.TryGetProperty("winnerSeed", out _));
        Assert.True(row.TryGetProperty("status", out _));
        Assert.True(row.TryGetProperty("completedAt", out _));
        Assert.Equal("player-1", row.GetProperty("seedA").GetString());
        Assert.Equal("player-2", row.GetProperty("seedB").GetString());
        Assert.Equal("pending", row.GetProperty("status").GetString());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task SkipAndLimit_Slice()
    {
        var tid = Guid.NewGuid();
        await SeedAsync(tid,
            (1, 0, "a", "b"),
            (1, 1, "c", "d"),
            (1, 2, "e", "f"),
            (1, 3, "g", "h"));
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{tid:D}/brackets?skip=1&limit=2");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("skip").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("limit").GetInt32());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items[0].GetProperty("matchSlot").GetInt32());
        Assert.Equal(2, items[1].GetProperty("matchSlot").GetInt32());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task LimitClampsToMax()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{Guid.NewGuid():D}/brackets?limit=5000");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(BracketQueryOptions.MaxPageSize,
            doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task LimitClampsToMin()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{Guid.NewGuid():D}/brackets?limit=-5");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task NegativeSkip_ClampsToZero()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{Guid.NewGuid():D}/brackets?skip=-10");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("skip").GetInt32());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task TournamentIdEchoesRoute()
    {
        var tid = Guid.NewGuid();
        using var client = NewClient();
        using var resp = await client.GetAsync($"/api/tournaments/{tid:D}/brackets");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(tid, doc.RootElement.GetProperty("tournamentId").GetGuid());
    }
}
