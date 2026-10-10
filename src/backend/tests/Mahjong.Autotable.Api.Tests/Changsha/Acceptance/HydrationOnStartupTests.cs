using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase I Wave 2 — runtime hydration acceptance suite.
///
/// Verifies <see cref="IChangshaGameRuntime.HydrateAsync"/> + Bishop's
/// <c>Program.cs</c> startup wiring re-populate <c>_games</c> from
/// <c>ChangshaGames.StateJson</c> on process boot, so a server restart no
/// longer wipes active hands. Closes the top-listed gap in
/// <c>docs/known-limitations.md</c>.
///
/// <list type="bullet">
///   <item>Test 1 drives the production flow: factory #1 creates a game with
///         <c>PersistSnapshots = true</c>, persists, disposes; factory #2
///         spins up against the same SQLite file and runs hydration
///         automatically via the <c>Program.cs</c> startup hook.</item>
///   <item>Tests 2/3 synthesize the state directly via raw SQLite insertion.
///         Reason: orchestrating a kong-replacement / robbed-kong scenario
///         to persist at the exact moment the relevant flags are set is
///         fragile (the runtime drives <c>StartNextHandOrEndAsync</c>
///         immediately after a win, which re-deals and clears CurrentWin).
///         Direct insertion isolates the hydration round-trip contract — it
///         tests exactly the JSON ↔ domain mapping that Bishop's memo
///         §3 ("Serializer round-trip — Phase I/H new state") promises.</item>
/// </list>
///
/// All three tests live behind <c>PersistSnapshots = true</c>; the default
/// harness override of <c>false</c> from <c>ChangshaHubTestHarness</c> is
/// flipped on per-test.
/// </summary>
[Collection("DbSerial")]
public class HydrationOnStartupTests
{
    // Production-side serializer options: System.Text.Json default
    // PropertyNamingPolicy is CamelCase, matching ChangshaGameRuntime.SnapshotJson.
    // We mirror it here so the round-trip fixtures use the byte-identical wire
    // shape that the runtime would write.
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ────────────────────────────────────────────────────────────────────
    //  1. Round-trip a mid-hand active game via the production flow
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Hydration_RoundTripsActiveGame()
    {
        var sqlitePath = NewSqlitePath();

        string gameId;
        int handNumber;
        int dealerSeatIndex;
        int handsCount;

        // Phase 1 — boot factory #1, drive a game, capture the persisted snapshot,
        // dispose. PersistSnapshots = true forces each runtime op to write the
        // current ChangshaGameState through to the ChangshaGames row.
        await using (var factory = BuildFactory(sqlitePath, persist: true))
        {
            var runtime = factory.Services.GetRequiredService<IChangshaGameRuntime>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            gameId = await runtime.CreateGameAsync(
                seed: 0xACED,
                botSeatIndexes: new[] { 0, 1, 2, 3 },
                hostPlayerId: null, hostConnectionId: null,
                cts.Token);

            Assert.True(runtime.TryGetSnapshot(gameId, out var preStart));
            preStart!.DealerSeatIndex = 0;
            foreach (var s in preStart.Seats) s.IsDealer = s.SeatIndex == 0;
            preStart.DealMode = DealMode.Auto;

            await runtime.StartGameAsync(gameId, cts.Token);

            Assert.True(runtime.TryGetSnapshot(gameId, out var snap));
            Assert.NotNull(snap);
            Assert.Equal(ChangshaPhase.AwaitingDiscard, snap!.Phase);

            handNumber = snap.HandNumber;
            dealerSeatIndex = snap.DealerSeatIndex;
            handsCount = snap.Hands.Count;

            Assert.True(runtime.GameCount >= 1,
                "Factory #1 runtime must have ≥1 game in memory before dispose.");
        }

        // Phase 2 — boot factory #2 against the same SQLite file. Bishop's
        // Program.cs startup wiring calls IChangshaGameRuntime.HydrateAsync,
        // so by the time the factory is up the runtime already replayed the
        // persisted row into _games. Asserting GameCount == 1 verifies the
        // hydration path fired AND only fired once for this gameId.
        await using (var factory = BuildFactory(sqlitePath, persist: true))
        {
            var runtime = factory.Services.GetRequiredService<IChangshaGameRuntime>();

            Assert.Equal(1, runtime.GameCount);

            Assert.True(runtime.TryGetSnapshot(gameId, out var hydrated),
                $"Game {gameId} should be hydrated from SQLite into runtime _games.");
            Assert.NotNull(hydrated);
            Assert.Equal(handNumber, hydrated!.HandNumber);
            Assert.Equal(dealerSeatIndex, hydrated.DealerSeatIndex);
            Assert.Equal(handsCount, hydrated.Hands.Count);
            // Phase round-trips — must not have decayed to EndGame on the way.
            Assert.NotEqual(ChangshaPhase.EndGame, hydrated.Phase);
        }

        TryDeleteSqlite(sqlitePath);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Round-trip LastDrawWasKongReplacement (Phase I Wave 1 carrier flag)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Hydration_RoundTripsLastDrawWasKongReplacement()
    {
        var sqlitePath = NewSqlitePath();
        var gameId = Guid.NewGuid();

        // Synthesize a mid-hand state that is paused mid-kong-replacement:
        // dealer has declared a concealed kong on Tiao-9 and drawn the
        // replacement Tong-5; LastDrawWasKongReplacement = true is the
        // carrier flag the state machine reads when deriving WinContext on
        // the next bot action.
        var state = BuildKongReplacementMidHandState(gameId.ToString());
        Assert.True(state.LastDrawWasKongReplacement,
            "Fixture precondition: LastDrawWasKongReplacement must be true.");

        await InsertSnapshotAsync(sqlitePath, gameId, state);

        await using var factory = BuildFactory(sqlitePath, persist: true);
        var runtime = factory.Services.GetRequiredService<IChangshaGameRuntime>();

        Assert.Equal(1, runtime.GameCount);

        Assert.True(runtime.TryGetSnapshot(gameId.ToString(), out var hydrated));
        Assert.NotNull(hydrated);
        Assert.True(hydrated!.LastDrawWasKongReplacement,
            "LastDrawWasKongReplacement must round-trip through hydration unchanged. " +
            "If this regresses, ChangshaGameState's JSON contract for the carrier flag " +
            "has drifted — see Bishop's Phase I Wave 2 memo §3.");

        // Sanity: the kong meld also round-trips so the hand can resume.
        var dealerHand = hydrated.Hands.Single(h => h.SeatIndex == 0);
        Assert.Single(dealerHand.Melds);
        Assert.Equal(4, dealerHand.Melds[0].TileIds.Count);

        TryDeleteSqlite(sqlitePath);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Round-trip WinResult.AllPatterns + IsRobbedKong (Phase H Wave 2 / I Wave 1)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Hydration_RoundTripsAllPatternsAndIsRobbedKong()
    {
        var sqlitePath = NewSqlitePath();
        var gameId = Guid.NewGuid();

        // Synthesize a paused hand-end state with a fully populated WinResult:
        //   Pattern        = FullFlush (structural Big Win — headline)
        //   AllPatterns    = [FullFlush, HeavenlyHand]  (Phase H W2 stacking list +
        //                                                Phase I W1 contextual flag)
        //   IsRobbedKong   = true  (Phase H W2 §2.2 robbing-the-added-kong flag)
        // This is the wire shape EmitScoringAndHandFinishedAsync sends to clients,
        // and the snapshot row written by PersistSnapshotAsync inside DeclareWinAsync
        // (line 656) BEFORE StartNextHandOrEndAsync clears CurrentWin.
        var state = BuildRobbedKongWinFrozenState(gameId.ToString());
        Assert.NotNull(state.CurrentWin);
        Assert.True(state.CurrentWin!.IsRobbedKong, "Fixture precondition.");
        Assert.Contains(WinPattern.FullFlush, state.CurrentWin.AllPatterns);

        await InsertSnapshotAsync(sqlitePath, gameId, state);

        await using var factory = BuildFactory(sqlitePath, persist: true);
        var runtime = factory.Services.GetRequiredService<IChangshaGameRuntime>();

        Assert.Equal(1, runtime.GameCount);

        Assert.True(runtime.TryGetSnapshot(gameId.ToString(), out var hydrated));
        Assert.NotNull(hydrated);
        Assert.NotNull(hydrated!.CurrentWin);

        var win = hydrated.CurrentWin!;
        Assert.True(win.IsRobbedKong,
            "IsRobbedKong must round-trip through hydration unchanged " +
            "(Phase H Wave 2 §2.2 contract — Bishop's memo §3).");
        Assert.Equal(WinPattern.FullFlush, win.Pattern);
        Assert.Contains(WinPattern.FullFlush, win.AllPatterns);
        Assert.Contains(WinPattern.HeavenlyHand, win.AllPatterns);
        Assert.True(win.AllPatterns.Count >= 2,
            $"AllPatterns must round-trip with ≥2 entries (FullFlush + HeavenlyHand). " +
            $"Got [{string.Join(",", win.AllPatterns)}].");

        TryDeleteSqlite(sqlitePath);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Phase I Wave 3 — filter excludes WallExhausted (draw-terminal)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-3")]
    public async Task Hydration_ExcludesWallExhaustedRows()
    {
        // Phase I Wave 2 open Q closed: Bishop's HydrateAsync now skips both
        // `EndGame` and `WallExhausted` (a hand whose wall ran out is
        // functionally finished — the runtime only drains it forward when
        // actively playing). This test inserts two synthesized rows and
        // verifies the filter:
        //   - Row A → Phase = WallExhausted  ⇒ must NOT hydrate.
        //   - Row B → Phase = AwaitingDiscard ⇒ must hydrate.

        var sqlitePath = NewSqlitePath();
        var wallExhaustedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();

        var wallExhaustedState = BuildSimpleState(wallExhaustedId.ToString(), ChangshaPhase.WallExhausted);
        var activeState = BuildSimpleState(activeId.ToString(), ChangshaPhase.AwaitingDiscard);

        Assert.Equal(ChangshaPhase.WallExhausted, wallExhaustedState.Phase);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, activeState.Phase);

        await InsertSnapshotAsync(sqlitePath, wallExhaustedId, wallExhaustedState);
        await InsertSnapshotAsync(sqlitePath, activeId, activeState);

        await using var factory = BuildFactory(sqlitePath, persist: true);
        var runtime = factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // Only the active row hydrates.
        Assert.Equal(1, runtime.GameCount);

        Assert.False(runtime.TryGetSnapshot(wallExhaustedId.ToString(), out var skipped),
            "WallExhausted rows must be skipped by HydrateAsync (Phase I Wave 3 contract).");
        Assert.Null(skipped);

        Assert.True(runtime.TryGetSnapshot(activeId.ToString(), out var hydrated),
            "Active (AwaitingDiscard) rows must still hydrate — only terminal phases are skipped.");
        Assert.NotNull(hydrated);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, hydrated!.Phase);

        TryDeleteSqlite(sqlitePath);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Fixtures
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal mid-hand state whose Phase is overridden to the
    /// given value. Used by the Phase I Wave 3 WallExhausted filter test —
    /// the only contract under test there is "rows whose persisted Phase ∈
    /// {EndGame, WallExhausted} are skipped during hydration", so the rest of
    /// the state can be whatever the state machine's StartGame + RollDice +
    /// Deal happen to produce.</summary>
    private static ChangshaGameState BuildSimpleState(string gameId, ChangshaPhase phase)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 0xBEEF, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.GameId = gameId;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(0xBEEF));
        ChangshaGameStateMachine.Deal(state);
        state.Phase = phase;
        return state;
    }

    /// <summary>Builds a mid-hand state where the dealer has just declared a
    /// concealed kong on Tiao-9 and drawn Tong-5 as the replacement tile.
    /// LastDrawWasKongReplacement = true is the carrier flag of interest.</summary>
    private static ChangshaGameState BuildKongReplacementMidHandState(string gameId)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 0xB055, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.GameId = gameId;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(0xB055));
        ChangshaGameStateMachine.Deal(state);

        // Replace dealer hand with a hand that holds a concealed Tiao-9 quad
        // plus shape that's not winning pre-replacement (no 258 pair).
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].Melds.Clear();
        state.Hands[0].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Tiao, 9, 0), Tid(Suit.Tiao, 9, 1),
            Tid(Suit.Tiao, 9, 2), Tid(Suit.Tiao, 9, 3),
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0),
            Tid(Suit.Tong, 5, 0),
        });

        // Ensure Tong-5 copy 1 is at the back of the wall (DrawFromBack will pull it).
        var replacementTile = Tid(Suit.Tong, 5, 1);
        state.Wall.RemoveAll(t => t == replacementTile);
        state.Wall.Add(replacementTile);
        state.WallBackIndex = state.Wall.Count - 1;

        // Drive the actual kong + replacement-draw transition through the
        // production state machine. This sets LastDrawWasKongReplacement = true
        // and puts the dealer in AwaitingDiscard with 11 concealed + 1 kong meld.
        var tiao9Logical = Logical(Suit.Tiao, 9);
        ChangshaGameStateMachine.DeclareConcealedKong(state, seatIndex: 0, tiao9Logical);

        return state;
    }

    /// <summary>Builds a frozen "hand just won via robbing-the-added-kong"
    /// state, with CurrentWin populated. Captures the wire shape persisted by
    /// DeclareWinAsync's PersistSnapshot — i.e., before StartNextHandOrEndAsync
    /// runs and clears CurrentWin via the next deal.</summary>
    private static ChangshaGameState BuildRobbedKongWinFrozenState(string gameId)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 0xBADD, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.GameId = gameId;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(0xBADD));
        ChangshaGameStateMachine.Deal(state);

        // The actual hand structure here doesn't matter for hydration — what
        // matters is the CurrentWin record round-trips. We synthesize it
        // directly with the wire shape EmitScoringAndHandFinishedAsync would
        // produce for a robbing-the-added-kong win.
        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = 1,
            SourceSeatIndex = 0,
            Method = WinMethod.Discard,
            Pattern = WinPattern.FullFlush,
            // Phase H W2 — populated in enum-declaration order; Phase I W1 extends
            // the list with the 5 contextual Big Win flags. The HeavenlyHand entry
            // is contrived for this fixture; the contract under test is "AllPatterns
            // serialises and deserialises as an IReadOnlyList<WinPattern> with
            // entries preserved", not whether the combination is logically possible.
            AllPatterns = new List<WinPattern>
            {
                WinPattern.FullFlush,
                WinPattern.HeavenlyHand,
            },
            IsRobbedKong = true,
            WinningTileId = Tid(Suit.Wan, 5, 0),
        };

        return state;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private static string NewSqlitePath()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, $"phase-i-w2-hydration-{Guid.NewGuid():N}.db");
    }

    private static void TryDeleteSqlite(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static WebApplicationFactory<Program> BuildFactory(string sqlitePath, bool persist)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={sqlitePath}");
            // Phase K Wave 23 — Vasquez. Pin the provider to SQLite even
            // when the db-providers matrix is running the Postgres cell
            // (which sets `Persistence__Provider=Postgres` process-wide).
            // These hydration tests INSERT into ChangshaGames via a raw
            // Microsoft.Data.Sqlite connection (see InsertSnapshotAsync
            // below) and would otherwise see "SQLite Error 1: 'no such
            // table'" on the Postgres cell because the factory
            // bootstrapped Postgres instead of the per-test SQLite file.
            //
            // <para>This test exercises the JSON ↔ domain hydration
            // round-trip through the SQLite path specifically; the
            // equivalent Postgres-side contract for replay-snapshot
            // round-tripping is held by
            // <c>Phase_K_W12.Bishop.ReplayStorePersistenceFacts</c>.</para>
            //
            // <para>Implementation note: we both override configuration
            // (via <see cref="IConfigurationBuilder.AddInMemoryCollection"/>)
            // AND tear-down/rebuild the EF DbContext registrations in
            // <see cref="IServiceCollection"/>. The configuration override
            // alone is not always sufficient because
            // <c>WebApplicationFactory&lt;Program&gt;</c> on the new
            // minimal-hosting model has subtle ordering for env vs
            // user-config sources; the explicit
            // <c>AddDbContext&lt;SqliteAppDbContext&gt;</c> below is the
            // authoritative pin.</para>
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("Persistence:Provider", "Sqlite"),
                    new KeyValuePair<string, string?>("ConnectionStrings:Sqlite", $"Data Source={sqlitePath}"),
                });
            });
            builder.ConfigureServices(services =>
            {
                RebindToSqlite(services, sqlitePath);
                services.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 5_000;       // Park bot turns well past test wall-clock
                    o.BotClaimDelayMs = 5_000;
                    o.ClaimWindowTimeoutMs = 5_000;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = persist;
                });
            });
        });
        // Force startup: spinning up Factory.Server runs Program.cs through
        // app.Run() prologue, which is where HydrateAsync fires.
        _ = factory.Server;
        return factory;
    }

    /// <summary>Strips the AppDbContext / PostgresAppDbContext /
    /// SqlServerAppDbContext registrations that Program.cs installed
    /// (whose provider is determined by the live env var
    /// <c>Persistence__Provider</c>) and re-registers a SQLite-only
    /// stack pinned to <paramref name="sqlitePath"/>. Required because
    /// these hydration tests perform raw <see cref="Microsoft.Data.Sqlite.SqliteConnection"/>
    /// inserts against the test's temp SQLite file and must run against
    /// the SQLite provider regardless of which matrix cell is firing.</summary>
    private static void RebindToSqlite(IServiceCollection services, string sqlitePath)
    {
        var toRemove = services.Where(d =>
            d.ServiceType.FullName is
                "Mahjong.Autotable.Api.Data.AppDbContext" or
                "Mahjong.Autotable.Api.Persistence.PostgresAppDbContext" or
                "Mahjong.Autotable.Api.Persistence.SqlServerAppDbContext" or
                "Mahjong.Autotable.Api.Persistence.SqliteAppDbContext"
            ||
            (d.ServiceType.IsGenericType
             && d.ServiceType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<>)
             && d.ServiceType.GetGenericArguments()[0].FullName?.StartsWith("Mahjong.Autotable.Api") == true)
            ||
            d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions)
        ).ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<Mahjong.Autotable.Api.Persistence.SqliteAppDbContext>(options =>
        {
            options.UseSqlite($"Data Source={sqlitePath}", sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(Mahjong.Autotable.Api.Persistence.SqliteAppDbContext).Assembly.GetName().Name);
            });
        });
        services.AddScoped<Mahjong.Autotable.Api.Data.AppDbContext>(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Persistence.SqliteAppDbContext>());
    }

    /// <summary>Direct SQLite insertion of a serialized snapshot. Uses the same
    /// JSON shape Bishop's HydrateAsync expects (CamelCase, default System.Text.Json),
    /// targeting the same connection string the factory will mount.</summary>
    private static async Task InsertSnapshotAsync(string sqlitePath, Guid gameId, ChangshaGameState state)
    {
        // Spin up a one-shot factory just to run DatabaseBootstrapper (which is
        // the only path that creates the ChangshaGames table — we can't insert
        // before the schema exists). PersistSnapshots = false keeps this boot
        // hermetic; no auto-runtime side effects.
        await using (var bootstrapFactory = BuildFactory(sqlitePath, persist: false))
        {
            // Spinning up Factory.Server above already ran the bootstrapper.
            _ = bootstrapFactory.Services;
        }

        var json = JsonSerializer.Serialize(state, SnapshotJson);
        var nowUtc = DateTime.UtcNow.ToString("o");

        var csb = new SqliteConnectionStringBuilder { DataSource = sqlitePath };
        await using var conn = new SqliteConnection(csb.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ChangshaGames
                (Id, RuleSet, Seed, StateJson, StateVersion, CurrentHandNumber, CurrentRoundNumber, CreatedUtc, UpdatedUtc)
            VALUES
                (@id, @ruleSet, @seed, @stateJson, @stateVersion, @hand, @round, @created, @updated);";
        cmd.Parameters.AddWithValue("@id", gameId.ToString());
        cmd.Parameters.AddWithValue("@ruleSet", "changsha-v1");
        cmd.Parameters.AddWithValue("@seed", state.Seed);
        cmd.Parameters.AddWithValue("@stateJson", json);
        cmd.Parameters.AddWithValue("@stateVersion", 1);
        cmd.Parameters.AddWithValue("@hand", state.HandNumber);
        cmd.Parameters.AddWithValue("@round", 1);
        cmd.Parameters.AddWithValue("@created", nowUtc);
        cmd.Parameters.AddWithValue("@updated", nowUtc);
        await cmd.ExecuteNonQueryAsync();
    }
}
