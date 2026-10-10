using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase J Wave 5 — <see cref="PlayerProfileService.RecordGameCompletedAsync"/>
/// aggregation contract tests (Vasquez).
///
/// <para>The runtime calls <c>RecordGameCompletedAsync</c> exactly once per
/// game-completed transition (see
/// <c>ChangshaGameRuntime.EmitGameCompletedAsync</c>). The frontend stats
/// panel re-reads <see cref="PlayerStats"/> after that broadcast lands —
/// so any miscount here is directly visible to the player as a wrong
/// "Games Played" / "Win Streak" number on the post-game modal.</para>
///
/// <para>These three facts pin the three monotonic counters that the
/// service mutates:
/// <list type="number">
///   <item><c>GamesPlayed</c> increments for every non-bot seat (4 humans
///         → 4 row updates).</item>
///   <item><c>GamesWon</c> + <c>CurrentWinStreak</c> increment only for
///         winners; <c>LongestWinStreak</c> tracks the all-time maximum
///         (the post-game modal shows both current + longest).</item>
///   <item>A losing game resets <c>CurrentWinStreak</c> to 0 without
///         touching <c>LongestWinStreak</c> (frontend reads the gap to
///         show "your best streak was X").</item>
/// </list></para>
///
/// <para><b>Why hit the service directly.</b> The runtime spins up a real
/// hub, a bot scheduler, a state machine and an EF context per game — far
/// too much surface for a 200ms unit test. The service is the only writer
/// for <see cref="PlayerStats"/>, so exercising it directly with
/// hand-crafted <c>finalScores</c> + <c>winners</c> arguments pins the
/// math without re-validating the runtime. (Wave 5 runtime → service
/// wiring is independently covered by
/// <see cref="MatchmakingLobbyEndpointTests"/> via the lobby endpoint.)</para>
/// </summary>
[Collection("DbSerial")]
public class PlayerStatsAggregationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-stats-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = false;
                });
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

    private PlayerProfileService GetService()
    {
        Assert.NotNull(_factory);
        return _factory!.Services.GetRequiredService<PlayerProfileService>();
    }

    private async Task<PlayerStats> ReadStatsAsync(string playerId)
    {
        // Read uncached + no-tracking so we see whatever the latest
        // SaveChanges wrote, not a stale tracked entity from a prior call.
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stats = await db.PlayerStats.AsNoTracking().FirstOrDefaultAsync(s => s.PlayerId == playerId);
        Assert.NotNull(stats);
        return stats!;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. GamesPlayed increments for every non-bot seat
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-5")]
    public async Task GameCompleted_Increments_GamesPlayed_ForAllPlayers()
    {
        // 4-seat game ends → all 4 humans see GamesPlayed += 1, regardless
        // of who won. Final scores ARE NOT split by win/loss here; the
        // runtime always passes the full scoreboard so the service can
        // also bump HighestSingleGameScore on the non-winners (a strong
        // loss is still personally noteworthy).
        var svc = GetService();
        var p1 = "tp1-" + Guid.NewGuid().ToString("N");
        var p2 = "tp2-" + Guid.NewGuid().ToString("N");
        var p3 = "tp3-" + Guid.NewGuid().ToString("N");
        var p4 = "tp4-" + Guid.NewGuid().ToString("N");

        var finalScores = new Dictionary<string, int>
        {
            [p1] = 100,
            [p2] = 50,
            [p3] = -25,
            [p4] = -125,
        };
        // p1 wins; the others all see GamesPlayed+=1 and CurrentWinStreak
        // pinned at 0 (they started at 0).
        var winners = new HashSet<string> { p1 };

        await svc.RecordGameCompletedAsync(finalScores, winners);

        foreach (var id in new[] { p1, p2, p3, p4 })
        {
            var stats = await ReadStatsAsync(id);
            Assert.Equal(1, stats.GamesPlayed);
            Assert.NotNull(stats.LastGameAt);
        }

        // TotalScore should mirror the per-seat sum exactly — basic
        // arithmetic check that the long-typed accumulator works.
        var p1Stats = await ReadStatsAsync(p1);
        Assert.Equal(100L, p1Stats.TotalScore);
        Assert.Equal(100, p1Stats.HighestSingleGameScore);

        // Negative final scores must still update TotalScore (Changsha
        // payouts net to zero by design — losers eat losses too).
        var p4Stats = await ReadStatsAsync(p4);
        Assert.Equal(-125L, p4Stats.TotalScore);
        // HighestSingleGameScore must NOT regress below 0 just because the
        // first game was a loss — the field tracks the all-time best
        // single-game score, default 0, and any negative score keeps the
        // default in place.
        Assert.Equal(0, p4Stats.HighestSingleGameScore);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Winners get GamesWon + CurrentWinStreak + LongestWinStreak
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-5")]
    public async Task WinningPlayer_GetsGamesWon_AndStreakIncrement()
    {
        // After three consecutive wins:
        //   GamesPlayed = 3, GamesWon = 3,
        //   CurrentWinStreak = 3, LongestWinStreak = 3
        // The post-game modal renders both streak counters, so the
        // "==" check is more than cosmetic.
        var svc = GetService();
        var winner = "tw-" + Guid.NewGuid().ToString("N");
        var loser = "tl-" + Guid.NewGuid().ToString("N");

        for (var round = 1; round <= 3; round++)
        {
            await svc.RecordGameCompletedAsync(
                new Dictionary<string, int> { [winner] = round * 100, [loser] = -round * 100 },
                new HashSet<string> { winner });
        }

        var stats = await ReadStatsAsync(winner);
        Assert.Equal(3, stats.GamesPlayed);
        Assert.Equal(3, stats.GamesWon);
        Assert.Equal(3, stats.CurrentWinStreak);
        Assert.Equal(3, stats.LongestWinStreak);

        // HighestSingleGameScore = max(100, 200, 300) = 300; round 3 win
        // was the biggest.
        Assert.Equal(300, stats.HighestSingleGameScore);
        Assert.Equal(100L + 200L + 300L, stats.TotalScore);

        // The loser must NOT have GamesWon incremented (this is the
        // negative side of the same fact — easy to regress if winner-
        // selection logic ever moves from set-contains to first-id).
        var loserStats = await ReadStatsAsync(loser);
        Assert.Equal(3, loserStats.GamesPlayed);
        Assert.Equal(0, loserStats.GamesWon);
        Assert.Equal(0, loserStats.CurrentWinStreak);
        Assert.Equal(0, loserStats.LongestWinStreak);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. A losing game resets CurrentWinStreak; LongestWinStreak survives
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-5")]
    public async Task LosingPlayer_StreakResetsTo_Zero_ButLongestSurvives()
    {
        // Set up a player with a 2-game win streak, then have them lose.
        // The CurrentWinStreak must reset to 0 but the LongestWinStreak
        // must stay at 2 — frontend uses the gap to show "your best
        // streak was 2".
        var svc = GetService();
        var player = "tps-" + Guid.NewGuid().ToString("N");
        var opp = "tpo-" + Guid.NewGuid().ToString("N");

        // Win × 2.
        await svc.RecordGameCompletedAsync(
            new Dictionary<string, int> { [player] = 100, [opp] = -100 },
            new HashSet<string> { player });
        await svc.RecordGameCompletedAsync(
            new Dictionary<string, int> { [player] = 100, [opp] = -100 },
            new HashSet<string> { player });

        var afterWins = await ReadStatsAsync(player);
        Assert.Equal(2, afterWins.CurrentWinStreak);
        Assert.Equal(2, afterWins.LongestWinStreak);

        // Loss → streak resets, longest survives.
        await svc.RecordGameCompletedAsync(
            new Dictionary<string, int> { [player] = -100, [opp] = 100 },
            new HashSet<string> { opp });

        var afterLoss = await ReadStatsAsync(player);
        Assert.Equal(3, afterLoss.GamesPlayed);
        Assert.Equal(2, afterLoss.GamesWon);
        Assert.Equal(0, afterLoss.CurrentWinStreak);
        Assert.Equal(2, afterLoss.LongestWinStreak);

        // And confirm a future win revives the streak counter from 0
        // without polluting LongestWinStreak (the new streak is shorter
        // than the previous best).
        await svc.RecordGameCompletedAsync(
            new Dictionary<string, int> { [player] = 50, [opp] = -50 },
            new HashSet<string> { player });

        var afterRevival = await ReadStatsAsync(player);
        Assert.Equal(1, afterRevival.CurrentWinStreak);
        Assert.Equal(2, afterRevival.LongestWinStreak);

        // ── Bot filter sanity ────────────────────────────────────────
        // Bot seats (player ids starting with "bot-") must NOT be
        // persisted at all — they have no profile and no PK reservation.
        // Easy regression: someone refactors the filter to a Contains()
        // call and accidentally lets a `bot-east` row land in the DB.
        var botId = "bot-east-" + Guid.NewGuid().ToString("N");
        await svc.RecordGameCompletedAsync(
            new Dictionary<string, int> { [botId] = 100, [player] = -100 },
            new HashSet<string> { botId });

        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var botRows = await db.PlayerStats.AsNoTracking().Where(s => s.PlayerId == botId).CountAsync();
        Assert.Equal(0, botRows);
        var botProfileRows = await db.PlayerProfiles.AsNoTracking().Where(p => p.PlayerId == botId).CountAsync();
        Assert.Equal(0, botProfileRows);
    }
}
