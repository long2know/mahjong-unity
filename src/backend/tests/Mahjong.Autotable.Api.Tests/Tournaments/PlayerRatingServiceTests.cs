using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Tournaments;

/// <summary>
/// Phase K Wave 1 — unit tests for the per-(player, season) Elo rating
/// service. Asserts: pure helpers (<see cref="PlayerRatingService.SeasonFromDate"/>,
/// <see cref="PlayerRatingService.PriorSeason"/>), Elo math via
/// <c>ComputeDelta</c>, and the live <c>RecordMatchOutcomeAsync</c>
/// path against an in-process SQLite database.
/// </summary>
[Collection("DbSerial")]
public sealed class PlayerRatingServiceTests
{
    [Fact]
    public void SeasonFromDate_returns_canonical_quarters()
    {
        Assert.Equal("2026-Q1", PlayerRatingService.SeasonFromDate(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-Q1", PlayerRatingService.SeasonFromDate(new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal("2026-Q2", PlayerRatingService.SeasonFromDate(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-Q3", PlayerRatingService.SeasonFromDate(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-Q4", PlayerRatingService.SeasonFromDate(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)));
    }

    [Fact]
    public void PriorSeason_wraps_year_boundary()
    {
        Assert.Equal("2025-Q4", PlayerRatingService.PriorSeason("2026-Q1"));
        Assert.Equal("2026-Q2", PlayerRatingService.PriorSeason("2026-Q3"));
        Assert.Equal("2026-Q3", PlayerRatingService.PriorSeason("2026-Q4"));
    }

    [Fact]
    public void ComputeDelta_winner_gains_positive_loser_negative()
    {
        var svc = BuildService(out _);
        var winnerDelta = svc.ComputeDelta(rating: 1200, opponentRating: 1200, won: true);
        var loserDelta = svc.ComputeDelta(rating: 1200, opponentRating: 1200, won: false);
        // Equal ratings → K * (1 - 0.5) = 16; loser symmetric.
        Assert.Equal(16, winnerDelta);
        Assert.Equal(-16, loserDelta);
    }

    [Fact]
    public void ComputeDelta_higher_rated_winner_gains_less()
    {
        var svc = BuildService(out _);
        var smallDelta = svc.ComputeDelta(rating: 1500, opponentRating: 1200, won: true);
        var bigDelta = svc.ComputeDelta(rating: 1200, opponentRating: 1500, won: true);
        Assert.True(smallDelta < bigDelta, $"smallDelta={smallDelta} bigDelta={bigDelta}");
    }

    [Fact]
    public async Task RecordMatchOutcomeAsync_seeds_new_rows_and_updates_existing()
    {
        var svc = BuildService(out var provider);
        var deltas = await svc.RecordMatchOutcomeAsync(
            new[] { "alice", "bob", "carol", "dave" },
            winnerPlayerId: "alice");

        // alice positive, others negative.
        Assert.True(deltas["alice"] > 0);
        Assert.True(deltas["bob"] < 0);
        Assert.True(deltas["carol"] < 0);
        Assert.True(deltas["dave"] < 0);

        // Live rows persisted.
        var db = provider.GetRequiredService<AppDbContext>();
        var rows = await db.PlayerRatings.ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.GamesPlayed));
    }

    [Fact]
    public async Task RecordMatchOutcomeAsync_filters_bot_ids()
    {
        var svc = BuildService(out var provider);
        var deltas = await svc.RecordMatchOutcomeAsync(
            new[] { "alice", "bob", "bot-eve", "bot-frank" },
            winnerPlayerId: "alice");
        Assert.Contains("alice", deltas.Keys);
        Assert.Contains("bob", deltas.Keys);
        Assert.DoesNotContain("bot-eve", deltas.Keys);
        Assert.DoesNotContain("bot-frank", deltas.Keys);
    }

    [Fact]
    public async Task RecordMatchOutcomeAsync_winner_not_in_list_is_noop()
    {
        var svc = BuildService(out var provider);
        var deltas = await svc.RecordMatchOutcomeAsync(
            new[] { "alice", "bob" },
            winnerPlayerId: "carol");
        Assert.Empty(deltas);
        var db = provider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.PlayerRatings.CountAsync());
    }

    [Fact]
    public async Task LeaderboardAsync_orders_by_elo_desc_then_games()
    {
        var svc = BuildService(out var provider);
        var db = provider.GetRequiredService<AppDbContext>();
        var season = svc.CurrentSeason();
        db.PlayerRatings.AddRange(
            new PlayerRating { PlayerId = "a", Season = season, EloRating = 1400, GamesPlayed = 5 },
            new PlayerRating { PlayerId = "b", Season = season, EloRating = 1300, GamesPlayed = 10 },
            new PlayerRating { PlayerId = "c", Season = season, EloRating = 1500, GamesPlayed = 3 });
        await db.SaveChangesAsync();

        var resp = await svc.LeaderboardAsync(season, limit: 10, offset: 0);
        Assert.Equal(3, resp.Total);
        Assert.Equal("c", resp.Rows[0].PlayerId);
        Assert.Equal(1, resp.Rows[0].Rank);
        Assert.Equal("a", resp.Rows[1].PlayerId);
        Assert.Equal("b", resp.Rows[2].PlayerId);
    }

    /// <summary>
    /// Builds an in-memory SQLite-backed PlayerRatingService. Returns the
    /// service + the DI provider so tests can hand-load data.
    /// </summary>
    private static PlayerRatingService BuildService(out IServiceProvider provider)
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(AppContext.BaseDirectory, "test-data",
            $"player-rating-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.Configure<RatingOptions>(o => { });
        services.AddScoped<PlayerRatingService>();
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }
        provider = sp.CreateScope().ServiceProvider;
        return provider.GetRequiredService<PlayerRatingService>();
    }
}
