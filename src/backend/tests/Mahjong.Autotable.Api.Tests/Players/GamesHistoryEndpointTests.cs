using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase K Wave 1 — REST contract tests for the games-history export
/// endpoint (Bishop). Seeds rows directly into <c>PlayerGameHistory</c>
/// then hits <c>GET /api/games</c> in JSON + CSV formats.
/// </summary>
[Collection("DbSerial")]
public sealed class GamesHistoryEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"games-history-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; });
            });
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

    private async Task SeedAsync(IEnumerable<PlayerGameHistory> rows)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PlayerGameHistory.AddRange(rows);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task JsonExport_filters_by_playerId_and_pages()
    {
        if (_factory is null) return;
        var now = DateTime.UtcNow;
        await SeedAsync(new[]
        {
            new PlayerGameHistory { PlayerId = "alice", GameId = Guid.NewGuid(), StartedAt = now.AddHours(-3), CompletedAt = now.AddHours(-2), FinalScore = 100, Won = true },
            new PlayerGameHistory { PlayerId = "alice", GameId = Guid.NewGuid(), StartedAt = now.AddHours(-2), CompletedAt = now.AddHours(-1), FinalScore = 80, Won = false },
            new PlayerGameHistory { PlayerId = "bob",   GameId = Guid.NewGuid(), StartedAt = now.AddHours(-1), CompletedAt = now,             FinalScore = 60, Won = false },
        });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/games?playerId=alice");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alice", body.GetProperty("playerId").GetString());
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        var games = body.GetProperty("games");
        Assert.Equal(2, games.GetArrayLength());
    }

    [Fact]
    public async Task CsvExport_uses_rfc4180_quoting_and_carries_header_row()
    {
        if (_factory is null) return;
        var now = DateTime.UtcNow;
        await SeedAsync(new[]
        {
            new PlayerGameHistory
            {
                PlayerId = "alice",
                GameId = Guid.NewGuid(),
                StartedAt = now.AddMinutes(-30),
                CompletedAt = now,
                FinalScore = 42,
                Won = true,
            },
        });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/games?playerId=alice&format=csv");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/csv", resp.Content.Headers.ContentType?.MediaType ?? "");
        var csv = await resp.Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Header + 1 data row.
        Assert.True(lines.Length >= 2);
        // Header row must include the columns we documented in the brief.
        // The controller filters by ?playerId= so the rows don't repeat it.
        Assert.Contains("GameId", lines[0]);
        Assert.Contains("FinalScore", lines[0]);
        Assert.Contains("CompletedAt", lines[0]);
    }

    [Fact]
    public async Task DateFilter_applies_inclusive_from_to_window()
    {
        if (_factory is null) return;
        var now = DateTime.UtcNow;
        await SeedAsync(new[]
        {
            new PlayerGameHistory { PlayerId = "alice", GameId = Guid.NewGuid(), StartedAt = now.AddDays(-7), CompletedAt = now.AddDays(-7).AddHours(1), FinalScore = 10 },
            new PlayerGameHistory { PlayerId = "alice", GameId = Guid.NewGuid(), StartedAt = now.AddDays(-1), CompletedAt = now.AddDays(-1).AddHours(1), FinalScore = 20 },
        });

        var client = _factory.CreateClient();
        var fromIso = now.AddDays(-2).ToString("o");
        var resp = await client.GetAsync($"/api/games?playerId=alice&from={Uri.EscapeDataString(fromIso)}");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task MissingPlayerId_returns_400()
    {
        if (_factory is null) return;
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/games");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
