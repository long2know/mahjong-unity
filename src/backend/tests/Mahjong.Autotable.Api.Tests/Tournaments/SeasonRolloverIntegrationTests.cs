using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Tournaments;

/// <summary>
/// Phase K Wave 1 — direct tests for the season rollover BackgroundService.
/// We never start the timer here; <see cref="SeasonRolloverService.RolloverOnceAsync"/>
/// is the unit-of-work, and tests drive it explicitly with hand-loaded
/// rows so we can pin exactly what the freeze/reset cycle does at a
/// quarter boundary.
/// </summary>
[Collection("DbSerial")]
public sealed class SeasonRolloverIntegrationTests
{
    [Fact]
    public async Task RolloverOnceAsync_freezes_stale_rows_and_clears_live_table()
    {
        var sp = BuildProvider();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PlayerRatings.Add(new PlayerRating { PlayerId = "alice", Season = "1999-Q4", EloRating = 1418, GamesPlayed = 4 });
            db.PlayerRatings.Add(new PlayerRating { PlayerId = "bob", Season = "1999-Q4", EloRating = 1102, GamesPlayed = 7 });
            // A row already at the current season — must NOT be touched.
            var current = scope.ServiceProvider.GetRequiredService<PlayerRatingService>().CurrentSeason();
            db.PlayerRatings.Add(new PlayerRating { PlayerId = "carol", Season = current, EloRating = 1200, GamesPlayed = 1 });
            await db.SaveChangesAsync();
        }

        var rollover = sp.GetRequiredService<SeasonRolloverService>();
        var frozen = await rollover.RolloverOnceAsync();
        Assert.Equal(2, frozen);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var current = scope.ServiceProvider.GetRequiredService<PlayerRatingService>().CurrentSeason();
            // Live table now only contains the current-season carol row.
            var live = await db.PlayerRatings.ToListAsync();
            Assert.Single(live);
            Assert.Equal("carol", live[0].PlayerId);
            // History table has both stale rows.
            var history = await db.PlayerRatingHistory.ToListAsync();
            Assert.Equal(2, history.Count);
            Assert.All(history, h => Assert.Equal("1999-Q4", h.Season));
            var alice = history.First(h => h.PlayerId == "alice");
            Assert.Equal(1418, alice.EloRating);
            Assert.Equal(4, alice.GamesPlayed);
        }
    }

    [Fact]
    public async Task RolloverOnceAsync_is_idempotent_on_existing_snapshot()
    {
        var sp = BuildProvider();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PlayerRatings.Add(new PlayerRating { PlayerId = "alice", Season = "1999-Q4", EloRating = 1300, GamesPlayed = 2 });
            db.PlayerRatingHistory.Add(new PlayerRatingHistory { PlayerId = "alice", Season = "1999-Q4", EloRating = 999, GamesPlayed = 1, FrozenAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var rollover = sp.GetRequiredService<SeasonRolloverService>();
        await rollover.RolloverOnceAsync();

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var history = await db.PlayerRatingHistory.Where(h => h.Season == "1999-Q4").ToListAsync();
            // The first-frozen snapshot wins — we do NOT overwrite with the
            // live-row values.
            Assert.Single(history);
            Assert.Equal(999, history[0].EloRating);
            Assert.Equal(1, history[0].GamesPlayed);
        }
    }

    [Fact]
    public async Task RolloverOnceAsync_noop_when_no_stale_rows()
    {
        var sp = BuildProvider();
        using (var scope = sp.CreateScope())
        {
            var current = scope.ServiceProvider.GetRequiredService<PlayerRatingService>().CurrentSeason();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PlayerRatings.Add(new PlayerRating { PlayerId = "alice", Season = current, EloRating = 1234, GamesPlayed = 5 });
            await db.SaveChangesAsync();
        }
        var rollover = sp.GetRequiredService<SeasonRolloverService>();
        var frozen = await rollover.RolloverOnceAsync();
        Assert.Equal(0, frozen);
    }

    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(AppContext.BaseDirectory, "test-data",
            $"season-rollover-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.Configure<RatingOptions>(o => { });
        services.AddScoped<PlayerRatingService>();
        services.AddSingleton<SeasonRolloverService>();
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }
        return sp;
    }
}
