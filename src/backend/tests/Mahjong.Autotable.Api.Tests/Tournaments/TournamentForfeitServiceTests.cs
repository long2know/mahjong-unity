using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Tournaments;

/// <summary>
/// Phase K Wave 1 — direct unit tests for the disconnect-driven
/// tournament forfeit BackgroundService (Bishop). We never start the
/// timer; <see cref="TournamentForfeitService.SweepOnceAsync"/> is the
/// unit-of-work and is driven explicitly with hand-loaded state.
/// </summary>
[Collection("DbSerial")]
public sealed class TournamentForfeitServiceTests
{
    [Fact]
    public async Task SweepOnce_forfeits_dropped_player_after_grace_period()
    {
        var sp = BuildProvider();
        var gameId = Guid.NewGuid();
        Guid matchId;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tournament = new Mahjong.Autotable.Api.Data.Entities.Tournament
            {
                Id = Guid.NewGuid(),
                Name = "T",
                Format = "single-elimination",
                Status = "in-progress",
                CreatedByPlayerId = "host",
                MaxPlayers = 4,
                GamesPerMatch = 1,
                CreatedAt = DateTime.UtcNow,
            };
            db.Tournaments.Add(tournament);
            matchId = Guid.NewGuid();
            db.TournamentMatches.Add(new TournamentMatch
            {
                Id = matchId,
                TournamentId = tournament.Id,
                Round = 1,
                Player1Id = "alice",
                Player2Id = "bob",
                Status = "in-progress",
                GameIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { gameId }),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var forfeit = sp.GetRequiredService<TournamentForfeitService>();
        forfeit.NoteDisconnect(gameId.ToString(), "alice");
        // No sweep yet → still in-progress.
        await forfeit.SweepOnceAsync();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var match = await db.TournamentMatches.FindAsync(matchId);
            Assert.NotNull(match);
            Assert.Equal("in-progress", match!.Status);
        }

        // Force the disconnect timestamp into the past so the next sweep
        // fires.
        BackdateDisconnect(forfeit, gameId.ToString(), "alice", TimeSpan.FromSeconds(forfeit.ReconnectGracePeriodSeconds + 5));
        var forfeited = await forfeit.SweepOnceAsync();
        Assert.Equal(1, forfeited);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var match = await db.TournamentMatches.FindAsync(matchId);
            Assert.Equal("complete", match!.Status);
            Assert.Equal("bob", match.WinnerPlayerId);
            Assert.True(match.ForfeitedByDisconnect);
            Assert.Equal("alice", match.ForfeitedPlayerId);
            // Audit row appended.
            var audit = await db.ReconnectAuditEntries
                .Where(a => a.PlayerId == TournamentForfeitService.ForfeitAuditMarker)
                .ToListAsync();
            Assert.Single(audit);
        }
    }

    [Fact]
    public async Task NoteReconnect_clears_pending_entry()
    {
        var sp = BuildProvider();
        var forfeit = sp.GetRequiredService<TournamentForfeitService>();
        forfeit.NoteDisconnect("game-1", "alice");
        Assert.Single(forfeit.PendingDisconnects);
        forfeit.NoteReconnect("game-1", "alice");
        Assert.Empty(forfeit.PendingDisconnects);
    }

    [Fact]
    public void NoteDisconnect_filters_bot_ids()
    {
        var sp = BuildProvider();
        var forfeit = sp.GetRequiredService<TournamentForfeitService>();
        forfeit.NoteDisconnect("game-1", "bot-eve");
        Assert.Empty(forfeit.PendingDisconnects);
    }

    [Fact]
    public async Task SweepOnce_skips_non_tournament_games()
    {
        var sp = BuildProvider();
        var forfeit = sp.GetRequiredService<TournamentForfeitService>();
        forfeit.NoteDisconnect(Guid.NewGuid().ToString(), "alice");
        BackdateDisconnect(forfeit, forfeit.PendingDisconnects.First().Key.GameId, "alice", TimeSpan.FromSeconds(forfeit.ReconnectGracePeriodSeconds + 5));
        var forfeited = await forfeit.SweepOnceAsync();
        // No matching tournament match → the entry is dropped without
        // any forfeit being recorded.
        Assert.Equal(0, forfeited);
        Assert.Empty(forfeit.PendingDisconnects);
    }

    /// <summary>
    /// Rewrites the captured disconnect timestamp via reflection so we
    /// don't have to wait for the grace window to elapse in real time.
    /// </summary>
    private static void BackdateDisconnect(TournamentForfeitService svc, string gameId, string playerId, TimeSpan howFarBack)
    {
        var field = typeof(TournamentForfeitService)
            .GetField("_disconnects", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = field!.GetValue(svc) as System.Collections.Concurrent.ConcurrentDictionary<(string, string), DateTime>;
        var key = (gameId, playerId);
        dict![key] = DateTime.UtcNow - howFarBack;
    }

    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(AppContext.BaseDirectory, "test-data",
            $"forfeit-svc-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.Configure<TournamentForfeitOptions>(o =>
        {
            o.ReconnectGracePeriodSeconds = 1;
            o.ForfeitSweepIntervalSeconds = 1;
        });
        services.Configure<RatingOptions>(o => { });
        services.AddScoped<PlayerRatingService>();
        services.AddScoped<TournamentService>();
        services.AddSingleton<TournamentForfeitService>();
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }
        return sp;
    }
}
