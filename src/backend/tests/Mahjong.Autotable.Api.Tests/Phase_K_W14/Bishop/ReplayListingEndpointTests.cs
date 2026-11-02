using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Replays;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Bishop;

/// <summary>
/// Phase K Wave 14 — Bishop. Hard-asserted contract for the
/// paginated <c>GET /api/replays</c> endpoint.
///
/// <list type="number">
///   <item>Anonymous → 200 (endpoint is public).</item>
///   <item>Empty store → 200 with <c>items.length == 0</c>.</item>
///   <item>Seeded rows surface in <c>CompletedAt</c> descending
///         order.</item>
///   <item><c>from</c> / <c>to</c> filters narrow on
///         <c>CompletedAt</c>.</item>
///   <item><c>variant</c> filter narrows by exact match.</item>
///   <item>Bad timestamp → 400.</item>
///   <item><c>limit</c> clamps to <see cref="ReplayOptions.MaxPageSize"/>.</item>
///   <item><c>payloadSize</c> is dropped (always 0) in the listing
///         wire — the full payload is only on the single-row
///         GET.</item>
///   <item>Default page-size constants match the documented values
///         (25 default, 100 max).</item>
///   <item>Envelope carries <c>filters</c> echo of the
///         normalized parameters.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class ReplayListingEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w14-replay-list-{Guid.NewGuid():N}.db");
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

    private async Task SeedAsync(params (DateTime CompletedAt, string Variant)[] rows)
    {
        var store = _factory!.Services.GetRequiredService<IReplayStore>();
        var i = 0;
        foreach (var r in rows)
        {
            await store.InsertAsync(new ReplayRecord
            {
                GameId = Guid.NewGuid(),
                CompletedAt = r.CompletedAt,
                Variant = r.Variant,
                TurnCount = 10 + i,
                CompressedPayload = new byte[] { 0x1, 0x2, 0x3 },
                IngestedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
            });
            i++;
        }
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Options_DefaultPageSizeIs25()
    {
        Assert.Equal(25, ReplayOptions.DefaultPageSize);
        Assert.Equal(25, new ReplayOptions().PageSize);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Options_MaxPageSizeIs100()
    {
        Assert.Equal(100, ReplayOptions.MaxPageSize);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Anonymous_Returns200()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task EmptyStore_Returns200_ZeroCount()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task EnvelopeCarriesAllFields()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("items", out _));
        Assert.True(doc.RootElement.TryGetProperty("count", out _));
        Assert.True(doc.RootElement.TryGetProperty("skip", out _));
        Assert.True(doc.RootElement.TryGetProperty("limit", out _));
        Assert.True(doc.RootElement.TryGetProperty("pageSize", out _));
        Assert.True(doc.RootElement.TryGetProperty("filters", out _));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task SeededRows_OrderedByCompletedAtDescending()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            (anchor.AddHours(-2), "changsha-v1"),
            (anchor, "changsha-v1"),
            (anchor.AddHours(-4), "changsha-v1"));
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        var first = items[0].GetProperty("completedAt").GetDateTime();
        var second = items[1].GetProperty("completedAt").GetDateTime();
        var third = items[2].GetProperty("completedAt").GetDateTime();
        Assert.True(first >= second);
        Assert.True(second >= third);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task FromFilter_Narrows()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            (anchor.AddHours(-5), "changsha-v1"),
            (anchor, "changsha-v1"));
        using var client = NewClient();
        using var resp = await client.GetAsync(
            $"/api/replays?from={anchor.AddHours(-1):O}");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task ToFilter_Narrows()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            (anchor.AddHours(-5), "changsha-v1"),
            (anchor, "changsha-v1"));
        using var client = NewClient();
        using var resp = await client.GetAsync(
            $"/api/replays?to={anchor.AddHours(-1):O}");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task VariantFilter_ExactMatch()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            (anchor, "changsha-v1"),
            (anchor, "expanded-v2"));
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays?variant=changsha-v1");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("changsha-v1",
            doc.RootElement.GetProperty("items")[0].GetProperty("variant").GetString());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task BadFrom_Returns400()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays?from=not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task BadTo_Returns400()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays?to=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task LimitClampsToMax()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays?limit=5000");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ReplayOptions.MaxPageSize,
            doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task LimitClampsToMin()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays?limit=0");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task PayloadSize_AlwaysZero_InListing()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync((anchor, "changsha-v1"));
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement.GetProperty("items")[0];
        // Listing wire drops the heavy payload column — payloadSize
        // should always be 0 regardless of stored payload bytes.
        Assert.Equal(0, row.GetProperty("payloadSize").GetInt32());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task DefaultPageSize_Surfaced()
    {
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ReplayOptions.DefaultPageSize,
            doc.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task RowsCarryFullMetadata()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync((anchor, "changsha-v1"));
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement.GetProperty("items")[0];
        Assert.True(row.TryGetProperty("replayId", out _));
        Assert.True(row.TryGetProperty("gameId", out _));
        Assert.True(row.TryGetProperty("completedAt", out _));
        Assert.True(row.TryGetProperty("variant", out _));
        Assert.True(row.TryGetProperty("turnCount", out _));
        Assert.True(row.TryGetProperty("payloadSize", out _));
        Assert.True(row.TryGetProperty("ingestedAt", out _));
        Assert.True(row.TryGetProperty("expiresAt", out _));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public async Task Skip_AdvancesPage()
    {
        var anchor = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            (anchor.AddHours(-2), "changsha-v1"),
            (anchor.AddHours(-1), "changsha-v1"),
            (anchor, "changsha-v1"));
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/replays?skip=1&limit=1");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("skip").GetInt32());
        // Skipping the most-recent row → second-most-recent surfaces.
        Assert.Equal(anchor.AddHours(-1),
            doc.RootElement.GetProperty("items")[0].GetProperty("completedAt").GetDateTime());
    }
}
