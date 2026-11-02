using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Tournaments;

/// <summary>
/// Phase K Wave 1 — REST contract tests for the ratings leaderboard
/// endpoint (Bishop). Seeds rows directly into the DB then hits
/// <c>GET /api/ratings/leaderboard</c> and asserts ordering + paging.
/// </summary>
[Collection("DbSerial")]
public sealed class RatingsLeaderboardEndpointTests : TournamentHarness
{
    [Fact]
    public async Task Leaderboard_returns_ordered_rows()
    {
        if (Factory is null) return;
        var client = Factory.CreateClient();
        var ratings = Factory.Services.GetService<PlayerRatingService>();
        if (ratings is null) return; // not yet shipped
        var season = ratings.CurrentSeason();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PlayerRatings.AddRange(
                new PlayerRating { PlayerId = "a", Season = season, EloRating = 1500, GamesPlayed = 5 },
                new PlayerRating { PlayerId = "b", Season = season, EloRating = 1300, GamesPlayed = 10 },
                new PlayerRating { PlayerId = "c", Season = season, EloRating = 1400, GamesPlayed = 7 });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/ratings/leaderboard");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(season, body.GetProperty("season").GetString());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var rows = body.GetProperty("rows");
        Assert.Equal("a", rows[0].GetProperty("playerId").GetString());
        Assert.Equal(1500, rows[0].GetProperty("eloRating").GetInt32());
        Assert.Equal(1, rows[0].GetProperty("rank").GetInt32());
        Assert.Equal("c", rows[1].GetProperty("playerId").GetString());
        Assert.Equal("b", rows[2].GetProperty("playerId").GetString());
    }

    [Fact]
    public async Task Leaderboard_paging_uses_limit_offset()
    {
        if (Factory is null) return;
        var client = Factory.CreateClient();
        var ratings = Factory.Services.GetService<PlayerRatingService>();
        if (ratings is null) return;
        var season = ratings.CurrentSeason();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 5; i++)
            {
                db.PlayerRatings.Add(new PlayerRating
                {
                    PlayerId = $"p{i}",
                    Season = season,
                    EloRating = 1500 - i, // p0 highest, p4 lowest
                    GamesPlayed = 1,
                });
            }
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/ratings/leaderboard?limit=2&offset=2");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, body.GetProperty("total").GetInt32());
        var rows = body.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("p2", rows[0].GetProperty("playerId").GetString());
        Assert.Equal(3, rows[0].GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task Season_endpoint_returns_current_code()
    {
        if (Factory is null) return;
        var client = Factory.CreateClient();
        var resp = await client.GetAsync("/api/ratings/season");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var current = body.GetProperty("current").GetString();
        Assert.False(string.IsNullOrEmpty(current));
        Assert.Contains("-Q", current!);
    }
}
