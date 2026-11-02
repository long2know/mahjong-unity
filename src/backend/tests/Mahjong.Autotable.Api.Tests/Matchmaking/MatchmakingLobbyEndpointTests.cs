using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Matchmaking;

/// <summary>
/// Phase J Wave 5 — <c>GET /api/matchmaking/lobby</c> endpoint contract
/// tests (Vasquez).
///
/// <para>Bishop's new MVC controller (<c>MatchmakingController</c>) is the
/// REST face of the public matchmaking lobby — the frontend polls it every
/// 5s (<c>matchmaking.ts:MATCHMAKING_POLL_MS</c>) to render the list of
/// joinable public games. The wire shape must stay stable across waves
/// since the frontend treats the response as the source of truth and
/// discards any game whose payload fails the type-guard
/// (<c>matchmaking.ts:isPublicGame</c>).</para>
///
/// <para>This file pins four facts:
/// <list type="number">
///   <item>Empty-runtime baseline — <c>{ games: [] }</c>, 200 OK.</item>
///   <item>Filter — only IsPublic + Seating-phase games appear. Three
///         games span the truth-table axes: (public, seating),
///         (public, NOT seating), (private, seating). Exactly the first
///         must come back.</item>
///   <item>Cap at 50 entries — create 60 public games, expect 50 back.
///         Aligns with <c>MatchmakingService.LobbyCap</c>.</item>
///   <item>Sort order — newest CreatedAt first. Insert 3 games with
///         <see cref="Task.Delay(int)"/> spacing so their CreatedUtc
///         timestamps land in a known ascending order, then assert the
///         response order is the inverse.</item>
/// </list></para>
///
/// <para><b>Wire-shape DTO.</b> Each <c>games[]</c> entry must carry:
/// <c>gameId, publicName, creatorDisplayName, seatedCount, maxSeats,
/// variant, createdAt</c>. Each property is asserted by name (not by
/// index) so the test surfaces a contract drift even if Bishop ever
/// reorders the record positional args. Note: this snake-vs-camel detail
/// is non-trivial — the controller relies on ASP.NET's default
/// camelCase JSON contract; deserialising into the matching property
/// shape proves both directions of the wire.</para>
///
/// <para><b>Concurrency caveat.</b> <c>SetGamePublicAsync</c> requires the
/// caller's id to match <c>state.CreatorPlayerId</c>, which
/// <c>CreateGameAsync</c> sets from the <c>hostConnectionId</c> argument.
/// Passing a unique non-null host id per create solves this. We then
/// invoke the runtime's snapshot mutation paths directly (instead of
/// going through the SignalR hub) because the hub layer would require a
/// SignalR client that exercises infrastructure orthogonal to the
/// matchmaking endpoint.</para>
/// </summary>
public class MatchmakingLobbyEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-lobby-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Persistence:Provider", "Sqlite");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
                    // PersistSnapshots=false — keep the runtime in-memory; the
                    // lobby endpoint reads `_games` directly, not the DB.
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

    private IChangshaGameRuntime Runtime()
    {
        Assert.NotNull(_factory);
        return _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
    }

    /// <summary>
    /// Creates a public Changsha game seated by <paramref name="hostId"/>.
    /// Mirrors the production flow: CreateGame → SetGamePublic. Returns
    /// the assigned gameId.
    /// </summary>
    private async Task<string> CreatePublicGameAsync(string hostId, string? publicName = "Test Game")
    {
        var runtime = Runtime();
        // Phase J Wave 6 — hostId is the persistent player id used by
        // SetGamePublicAsync's callerPlayerId check. Connection-id is not
        // used by the lobby snapshot path so we leave it null.
        var gameId = await runtime.CreateGameAsync(seed: 0, botSeatIndexes: [], hostPlayerId: hostId, hostConnectionId: null);
        await runtime.SetGamePublicAsync(gameId, callerPlayerId: hostId, isPublic: true, publicName: publicName, default);
        return gameId;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Empty runtime → 200 + empty games array
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Matchmaking"), Trait("Wave", "Phase-J-5")]
    public async Task MatchmakingLobby_Returns200_WithEmptyList_WhenNoPublicGames()
    {
        // Cold-start: no games at all in the runtime → response is shaped
        // `{ games: [] }`. The frontend explicitly handles `games` being
        // an empty array (renders the "no public games — create one"
        // empty-state). A missing `games` key would also break that
        // empty-state branch, so we assert presence + emptiness.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/api/matchmaking/lobby");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("games", out var games),
            "Response must have a `games` property even when empty.");
        Assert.Equal(JsonValueKind.Array, games.ValueKind);
        Assert.Equal(0, games.GetArrayLength());
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Filter — only public + Seating-phase games are listed
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Matchmaking"), Trait("Wave", "Phase-J-5")]
    public async Task MatchmakingLobby_Includes_OnlyPublicLobbyPhaseGames()
    {
        var runtime = Runtime();

        // Game 1 — public + Seating: must appear.
        var publicSeatingId = await CreatePublicGameAsync("host-1-" + Guid.NewGuid().ToString("N"), "Visible Game");

        // Game 2 — public, then push out of Seating phase: must NOT appear.
        var publicStartedId = await CreatePublicGameAsync("host-2-" + Guid.NewGuid().ToString("N"), "Started Game");
        Assert.True(runtime.TryGetSnapshot(publicStartedId, out var startedState));
        Assert.NotNull(startedState);
        // Phase mutation outside the instance lock is safe for SnapshotLobbyGames,
        // which is intentionally lock-free and accepts the inconsistent read.
        startedState!.Phase = ChangshaPhase.Dealing;

        // Game 3 — private (IsPublic stays false): must NOT appear.
        // CreateGameAsync defaults IsPublic=false; skipping SetGamePublic
        // leaves the game in the private state.
        var privateId = await runtime.CreateGameAsync(seed: 0, botSeatIndexes: [],
            hostPlayerId: "host-3-" + Guid.NewGuid().ToString("N"), hostConnectionId: null);

        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var body = await client.GetStringAsync("/api/matchmaking/lobby");
        using var doc = JsonDocument.Parse(body);
        var games = doc.RootElement.GetProperty("games");

        var returnedIds = games.EnumerateArray()
            .Select(g => g.GetProperty("gameId").GetString())
            .ToList();

        // Exactly one game returned, and it's the public+seating one.
        Assert.Single(returnedIds);
        Assert.Equal(publicSeatingId, returnedIds[0]);
        Assert.DoesNotContain(publicStartedId, returnedIds);
        Assert.DoesNotContain(privateId, returnedIds);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Cap at MatchmakingService.LobbyCap (50)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Matchmaking"), Trait("Wave", "Phase-J-5")]
    public async Task MatchmakingLobby_RespectsCap_At50Games()
    {
        // Spam 60 public seating games (10 over the cap). The endpoint
        // must hard-cap at LobbyCap=50 so a malicious / runaway producer
        // can't blow up the frontend's render pipeline. The cap is also
        // a denial-of-service shield: the JSON payload is bounded
        // regardless of `_games.Count`.
        var runtime = Runtime();
        for (var i = 0; i < 60; i++)
        {
            await CreatePublicGameAsync("cap-host-" + i + "-" + Guid.NewGuid().ToString("N"), $"Game #{i}");
        }

        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var body = await client.GetStringAsync("/api/matchmaking/lobby");
        using var doc = JsonDocument.Parse(body);
        var games = doc.RootElement.GetProperty("games");
        Assert.Equal(50, games.GetArrayLength());
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Sort order — newest CreatedAt first
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Matchmaking"), Trait("Wave", "Phase-J-5")]
    public async Task MatchmakingLobby_SortedByCreatedAt_DescendingNewestFirst()
    {
        // The runtime's SnapshotLobbyGames sorts entries by descending
        // CreatedUtc so the frontend renders the most-recent game at the
        // top of the lobby list. CreatedUtc is set from DateTime.UtcNow
        // inside ChangshaGameInstance and is read-only — so we space out
        // the creates with a small Task.Delay to guarantee strict ordering
        // even on platforms where DateTime.UtcNow has 1ms resolution.
        var oldestId = await CreatePublicGameAsync("old-host-" + Guid.NewGuid().ToString("N"), "Oldest");
        await Task.Delay(20);
        var middleId = await CreatePublicGameAsync("mid-host-" + Guid.NewGuid().ToString("N"), "Middle");
        await Task.Delay(20);
        var newestId = await CreatePublicGameAsync("new-host-" + Guid.NewGuid().ToString("N"), "Newest");

        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var body = await client.GetStringAsync("/api/matchmaking/lobby");
        using var doc = JsonDocument.Parse(body);
        var games = doc.RootElement.GetProperty("games");
        Assert.Equal(3, games.GetArrayLength());

        var ids = games.EnumerateArray()
            .Select(g => g.GetProperty("gameId").GetString())
            .ToList();
        Assert.Equal(new[] { newestId, middleId, oldestId }, ids);

        // CreatedAt must be strictly descending as well — sanity check on
        // the field itself, not just the implied order.
        var createdAts = games.EnumerateArray()
            .Select(g => g.GetProperty("createdAt").GetDateTime())
            .ToList();
        Assert.True(createdAts[0] > createdAts[1], "First entry must be newer than second.");
        Assert.True(createdAts[1] > createdAts[2], "Second entry must be newer than third.");

        // ── Wire-shape contract assertion (single point) ────────────────
        // Pick any one entry and verify every property the frontend reads
        // is both present AND of the expected JSON kind. A missing field
        // here would cause matchmaking.ts:isPublicGame to silently filter
        // the entry out — a regression that's invisible without this test.
        var first = games[0];
        Assert.Equal(JsonValueKind.String, first.GetProperty("gameId").ValueKind);
        Assert.Equal(JsonValueKind.String, first.GetProperty("publicName").ValueKind);
        // creatorDisplayName resolves through PlayerProfileService which
        // auto-creates a profile + assigns a default name. It must be a
        // non-null string.
        Assert.Equal(JsonValueKind.String, first.GetProperty("creatorDisplayName").ValueKind);
        Assert.False(string.IsNullOrEmpty(first.GetProperty("creatorDisplayName").GetString()));
        Assert.Equal(JsonValueKind.Number, first.GetProperty("seatedCount").ValueKind);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("maxSeats").ValueKind);
        Assert.Equal(4, first.GetProperty("maxSeats").GetInt32());
        Assert.Equal(0, first.GetProperty("botCount").GetInt32());
        Assert.Equal(4, first.GetProperty("openHumanSeats").GetInt32());
        Assert.Equal(JsonValueKind.String, first.GetProperty("variant").ValueKind);
        Assert.Equal("Changsha", first.GetProperty("variant").GetString());
        Assert.Equal(JsonValueKind.String, first.GetProperty("createdAt").ValueKind);
    }
}
