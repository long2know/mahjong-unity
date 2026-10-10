using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Leaderboard;

/// <summary>
/// Phase J Wave 6 — <c>GET /api/leaderboard</c> contract tests (Vasquez).
///
/// <para>Bishop's Wave-6 leaderboard joins <see cref="PlayerStats"/> with
/// <see cref="PlayerProfile"/>, filters by <c>minGames</c>, sorts by the
/// requested axis, and paginates via <c>limit</c> + <c>offset</c>. The
/// response envelope is
/// <c>{ "total": &lt;int&gt;, "rows": [ { rank, playerId, displayName,
/// avatarColor, gamesPlayed, gamesWon, winRate, totalScore,
/// highestSingleGameScore, longestWinStreak } ] }</c>.
/// The frontend's <c>leaderboard.ts</c> normaliser keys off all ten row
/// fields plus <c>total</c>.</para>
///
/// <para>These four facts pin the contract:
/// <list type="number">
///   <item>Default ordering — sort omitted → <c>gamesWon DESC</c>, asserted
///         monotonically on the returned slice.</item>
///   <item><c>minGames=5</c> default — players with &lt; 5 games are
///         filtered out; the 6-game + 10-game seeds appear, the 2-game +
///         4-game seeds do not.</item>
///   <item><c>sort=winRate</c> — explicit axis switch; the player with the
///         higher <c>winRate</c> comes first.</item>
///   <item><c>limit=10&amp;offset=20</c> — 60 seeds → page returns 10 rows
///         starting at <c>rank=21</c> (offset is the rank-1 base).</item>
/// </list></para>
///
/// <para><b>Seeding strategy.</b> Each test opens its own temp-SQLite DB
/// and writes <see cref="PlayerProfile"/> + <see cref="PlayerStats"/> rows
/// directly via the runtime's <see cref="AppDbContext"/>. Going through
/// <c>PlayerProfileService.GetOrCreateAsync</c> would inject deterministic
/// default names which still works fine — but writing stats directly lets
/// us prescribe exact <c>GamesPlayed</c> / <c>GamesWon</c> values without
/// simulating any gameplay flow.</para>
/// </summary>
[Collection("DbSerial")]
public class LeaderboardEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-leaderboard-{Guid.NewGuid():N}.db");

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

    // ────────────────────────────────────────────────────────────────────
    //  1. Default sort = gamesWon DESC
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-J-6")]
    public async Task Leaderboard_ReturnsTopByGamesWon_ByDefault()
    {
        // Bishop's LeaderboardService.ParseSort defaults to GamesWon when
        // the query string is omitted or unknown. We seed 10 players with
        // strictly increasing GamesWon values (5..14) and >= 5 GamesPlayed
        // each (so the default minGames=5 filter passes everyone), then
        // assert the returned slice is sorted gamesWon DESC.
        await SeedPlayersAsync(playerCount: 10,
            gamesWon: i => 5 + i,
            gamesPlayed: i => 20);

        var doc = await GetLeaderboardAsync("/api/leaderboard");
        var rows = doc.GetProperty("rows");
        Assert.Equal(10, rows.GetArrayLength());

        // Monotonic-descending assertion: for every adjacent pair, the
        // higher-rank row's gamesWon must be >= the lower-rank one's. This
        // is the contract observable side of OrderByDescending(r =>
        // r.GamesWon).ThenByDescending(r => r.WinRate).ThenBy(r =>
        // r.PlayerId).
        var enumerated = rows.EnumerateArray().ToList();
        for (var i = 1; i < enumerated.Count; i++)
        {
            var prev = enumerated[i - 1].GetProperty("gamesWon").GetInt32();
            var curr = enumerated[i].GetProperty("gamesWon").GetInt32();
            Assert.True(prev >= curr,
                $"Row {i - 1} ({prev}) must have gamesWon >= row {i} ({curr}) under default sort.");
        }

        // The first row carries gamesWon=14 (the highest seed) and rank=1.
        Assert.Equal(14, enumerated[0].GetProperty("gamesWon").GetInt32());
        Assert.Equal(1, enumerated[0].GetProperty("rank").GetInt32());

        // Wire-shape contract — pin every field the frontend's
        // leaderboard.ts:normalizeRow reads. A drop here would silently
        // strand the leaderboard column it backs.
        var first = enumerated[0];
        Assert.Equal(JsonValueKind.Number, first.GetProperty("rank").ValueKind);
        Assert.Equal(JsonValueKind.String, first.GetProperty("playerId").ValueKind);
        Assert.Equal(JsonValueKind.String, first.GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.String, first.GetProperty("avatarColor").ValueKind);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("gamesPlayed").ValueKind);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("gamesWon").ValueKind);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("winRate").ValueKind);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("totalScore").ValueKind);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("highestSingleGameScore").ValueKind);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("longestWinStreak").ValueKind);
        // Top-level envelope: `total` is paging-independent population count.
        Assert.Equal(10, doc.GetProperty("total").GetInt32());
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. minGames default = 5 → players below threshold filtered out
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-J-6")]
    public async Task Leaderboard_FiltersOut_PlayersBelowMinGames()
    {
        // Bishop's LeaderboardService.DefaultMinGames = 5. We seed four
        // players with GamesPlayed = 2, 4, 6, 10. The default endpoint
        // call (no minGames query parameter) must return exactly the two
        // players at 6 and 10 — the others are filtered out by the
        // `Where(s => s.GamesPlayed >= minGames)` clause in the service.
        await SeedPlayerAsync("p-002", gamesPlayed: 2, gamesWon: 1);
        await SeedPlayerAsync("p-004", gamesPlayed: 4, gamesWon: 2);
        await SeedPlayerAsync("p-006", gamesPlayed: 6, gamesWon: 3);
        await SeedPlayerAsync("p-010", gamesPlayed: 10, gamesWon: 5);

        var doc = await GetLeaderboardAsync("/api/leaderboard");

        // total == 2 (the count of post-filter rows). Without the filter
        // it would be 4; a regression there would fail this first because
        // `total` is asserted before `rows` so a missing filter surfaces
        // immediately.
        Assert.Equal(2, doc.GetProperty("total").GetInt32());
        var rows = doc.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());

        var returnedIds = rows.EnumerateArray()
            .Select(r => r.GetProperty("playerId").GetString())
            .ToHashSet();
        Assert.Contains("p-006", returnedIds);
        Assert.Contains("p-010", returnedIds);
        Assert.DoesNotContain("p-002", returnedIds);
        Assert.DoesNotContain("p-004", returnedIds);

        // Sanity: setting minGames=0 explicitly returns everyone — this
        // proves the filter is what's hiding rows, not some other
        // accidental cap. minGames=0 is also the admin / debug view.
        var allDoc = await GetLeaderboardAsync("/api/leaderboard?minGames=0");
        Assert.Equal(4, allDoc.GetProperty("total").GetInt32());
        Assert.Equal(4, allDoc.GetProperty("rows").GetArrayLength());
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. sort=winRate orders by computed win-rate desc
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-J-6")]
    public async Task Leaderboard_SortBy_WinRate_OrdersCorrectly()
    {
        // Bishop's LeaderboardService projects `winRate = GamesPlayed > 0
        // ? (double)GamesWon / GamesPlayed : 0.0` SQL-side. With
        // sort=winRate, the service orders by descending winRate (then
        // gamesPlayed, then playerId). We seed two players:
        //   • A — 8 wins / 10 played → winRate 0.8
        //   • B — 6 wins / 10 played → winRate 0.6
        // A must come back first; both win-rate values must match the
        // 0.8/0.6 projection (so a future refactor that ever rounds or
        // re-types this field surfaces immediately).
        await SeedPlayerAsync("player-A", gamesPlayed: 10, gamesWon: 8);
        await SeedPlayerAsync("player-B", gamesPlayed: 10, gamesWon: 6);

        var doc = await GetLeaderboardAsync("/api/leaderboard?sort=winRate");
        var rows = doc.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());

        var enumerated = rows.EnumerateArray().ToList();
        Assert.Equal("player-A", enumerated[0].GetProperty("playerId").GetString());
        Assert.Equal("player-B", enumerated[1].GetProperty("playerId").GetString());

        var aWinRate = enumerated[0].GetProperty("winRate").GetDouble();
        var bWinRate = enumerated[1].GetProperty("winRate").GetDouble();
        // Equality with a small epsilon — SQLite floats round-trip through
        // double so the 0.8 / 0.6 projections are exact in this case, but
        // an epsilon keeps the assertion robust against future EF Core /
        // SQLite driver changes that might tweak precision.
        Assert.True(Math.Abs(aWinRate - 0.8) < 0.0001,
            $"player-A winRate should be 0.8; saw {aWinRate}.");
        Assert.True(Math.Abs(bWinRate - 0.6) < 0.0001,
            $"player-B winRate should be 0.6; saw {bWinRate}.");
        Assert.True(aWinRate > bWinRate,
            "sort=winRate must order higher-winRate rows first.");

        // Rank is 1-based and reflects the position within the *sorted*
        // result set. A and B fill ranks 1 + 2 respectively.
        Assert.Equal(1, enumerated[0].GetProperty("rank").GetInt32());
        Assert.Equal(2, enumerated[1].GetProperty("rank").GetInt32());
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. limit + offset paginate over the sorted result set
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-J-6")]
    public async Task Leaderboard_RespectsLimitAndOffset()
    {
        // Seed 60 players with strictly-monotonic GamesWon (each above
        // minGames=5 so nothing is filtered). offset=20 + limit=10 must
        // return 10 rows starting at rank=21 — the 21st-best-by-default-
        // sort player. Bishop's service caps `limit` at MaxLimit=100, but
        // never silently shifts `offset` past the end; here both knobs
        // land well inside the seeded population.
        await SeedPlayersAsync(playerCount: 60,
            gamesWon: i => 100 + i,   // unique per seed so sort order is unambiguous
            gamesPlayed: i => 200);

        var doc = await GetLeaderboardAsync("/api/leaderboard?limit=10&offset=20");

        // The filtered total reflects the full 60-player population
        // (everyone passes the minGames default of 5); it is NOT capped
        // by `limit` so the frontend can render "Page 3 of 6"-style
        // navigation. A regression that conflates `total` with the page
        // size would surface immediately here.
        Assert.Equal(60, doc.GetProperty("total").GetInt32());

        var rows = doc.GetProperty("rows");
        Assert.Equal(10, rows.GetArrayLength());

        var enumerated = rows.EnumerateArray().ToList();
        // First rank must be 21 (offset 20, 1-based). This is the
        // load-bearing invariant — without it the frontend would render
        // wrong page numbers in its rank column.
        Assert.Equal(21, enumerated[0].GetProperty("rank").GetInt32());
        Assert.Equal(30, enumerated[^1].GetProperty("rank").GetInt32());

        // GamesWon is monotonically descending across the slice (sort
        // default is gamesWon DESC). Players were seeded with gamesWon =
        // 100..159; the 1st rank has gamesWon=159, rank=21 has gamesWon=139.
        var topGamesWon = enumerated[0].GetProperty("gamesWon").GetInt32();
        Assert.Equal(139, topGamesWon);
        for (var i = 1; i < enumerated.Count; i++)
        {
            var prev = enumerated[i - 1].GetProperty("gamesWon").GetInt32();
            var curr = enumerated[i].GetProperty("gamesWon").GetInt32();
            Assert.True(prev > curr,
                $"Slice row {i - 1} ({prev}) must have gamesWon > row {i} ({curr}) on default sort over unique seeds.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> GetLeaderboardAsync(string url)
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// Writes a single <see cref="PlayerProfile"/> + <see cref="PlayerStats"/>
    /// pair directly through the runtime's <see cref="AppDbContext"/>.
    /// Bypasses <c>PlayerProfileService.GetOrCreateAsync</c> so the stats
    /// counters can be set explicitly without simulating any gameplay flow.
    /// </summary>
    private async Task SeedPlayerAsync(
        string playerId,
        int gamesPlayed,
        int gamesWon,
        long totalScore = 0,
        int highestSingleGameScore = 0,
        int longestWinStreak = 0,
        string? displayName = null,
        string? avatarColor = null)
    {
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PlayerProfiles.Add(new PlayerProfile
        {
            PlayerId = playerId,
            DisplayName = displayName ?? $"Test {playerId}",
            AvatarColor = avatarColor ?? "#1E88E5",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        db.PlayerStats.Add(new PlayerStats
        {
            PlayerId = playerId,
            GamesPlayed = gamesPlayed,
            GamesWon = gamesWon,
            TotalScore = totalScore,
            HighestSingleGameScore = highestSingleGameScore,
            LongestWinStreak = longestWinStreak,
            CurrentWinStreak = 0,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Bulk-seeds <paramref name="playerCount"/> players whose stats are
    /// produced by the supplied <paramref name="gamesWon"/> +
    /// <paramref name="gamesPlayed"/> projections. Uses a single
    /// <c>SaveChangesAsync</c> for speed.
    /// </summary>
    private async Task SeedPlayersAsync(
        int playerCount,
        Func<int, int> gamesWon,
        Func<int, int> gamesPlayed)
    {
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < playerCount; i++)
        {
            // Deterministic ids so failed-test diagnostics are reproducible.
            var id = $"seed-{i:D3}";
            db.PlayerProfiles.Add(new PlayerProfile
            {
                PlayerId = id,
                DisplayName = $"Seed {i}",
                AvatarColor = "#1E88E5",
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            });
            db.PlayerStats.Add(new PlayerStats
            {
                PlayerId = id,
                GamesPlayed = gamesPlayed(i),
                GamesWon = gamesWon(i),
            });
        }
        await db.SaveChangesAsync();
    }
}
