using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #121 (Lead C-2 extension) — validated <c>handCount</c> handshake parsing, first-creator-wins
/// config binding, and <c>botDifficulty</c> join semantics.
///
/// <para>The Lead ruling extends C-2 to the optional lobby params <c>seed</c> (Ferro/#127) and
/// <c>handCount</c> (this lane); absent defaults remain random / 4 respectively, and the visible
/// <c>rulePreset</c> is tolerated-not-applied. Binding is <b>first-creator-wins</b>: whoever creates
/// the runtime game fixes its config, and a later joiner's differing <c>seed</c>/<c>handCount</c>/
/// <c>botDifficulty</c> can never silently re-configure a bound (potentially live) game.</para>
///
/// <list type="bullet">
///   <item><c>handCount</c> accepts the canonical create-time values {1,4,8,16} (Changsha spec
///   §6.3 caps a match at 16); a legacy/requested <c>32</c> is normalized to the authoritative
///   <c>16</c>; any other/absent value ⇒ the runtime default of 4. (#130 tracks any future >16.)</item>
///   <item>A late joiner cannot re-cap (<c>handCount</c>) or re-skin (<c>botDifficulty</c>) a bound
///   game — pre-#121 the endpoint re-applied <c>SetBotStrategyAsync</c> on every re-bind, which let
///   any late joiner mutate a live game's bots.</item>
///   <item><c>MaxHands</c> round-trips through persistence + hydration (it is part of the full-state
///   JSON snapshot).</item>
/// </list>
/// </summary>
public sealed class AutotableConfigJoinSemanticsTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"config-join-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Persistence:Provider", "Sqlite");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(o =>
            {
                o.BotTurnDelayMs = 1;
                o.BotClaimDelayMs = 1;
                o.BotPickupDelayMs = 1;
                o.ClaimWindowTimeoutMs = 50;
                o.DealBatchDelayMs = 0;
                o.PersistSnapshots = false;
            }));
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

    // ── handCount whitelist parsing ────────────────────────────────────────────────

    [Theory, Trait("Category", "Autotable"), Trait("Contract", "C-2")]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(32, 16)] // #130 — canonical §6.3 caps at 16; legacy 32 normalizes to authoritative 16
    public async Task Handshake_HandCount_SetsAuthoritativeMaxHands(int handCount, int expectedMaxHands)
    {
        var gameId = $"hc-{Guid.NewGuid():N}";
        await using var session = await ConnectAsync($"seat=0&botCount=3&handCount={handCount}", gameId);
        var runtimeGameId = await SeatTakeAndBindAsync(session, seat: 0, gameId);

        Assert.True(_factory!.Services.GetRequiredService<IChangshaGameRuntime>()
            .TryGetSnapshot(runtimeGameId, out var s) && s is not null);
        // The stored MaxHands is authoritative: a requested 32 surfaces as 16, never stored as 32
        // (which the engine's 4-round terminal would end early at 16 anyway).
        Assert.Equal(expectedMaxHands, s!.MaxHands);
    }

    [Theory, Trait("Category", "Autotable"), Trait("Contract", "C-2")]
    [InlineData("7")]    // not in the whitelist
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("100")]
    [InlineData("-4")]
    [InlineData("abc")]  // non-numeric
    [InlineData("")]     // empty
    public async Task Handshake_NonWhitelistedHandCount_FallsBackTo4(string handCount)
    {
        var gameId = $"hc-bad-{Guid.NewGuid():N}";
        await using var session = await ConnectAsync($"seat=0&botCount=3&handCount={handCount}", gameId);
        var runtimeGameId = await SeatTakeAndBindAsync(session, seat: 0, gameId);

        Assert.True(_factory!.Services.GetRequiredService<IChangshaGameRuntime>()
            .TryGetSnapshot(runtimeGameId, out var s) && s is not null);
        Assert.Equal(4, s!.MaxHands);
    }

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-2")]
    public async Task Handshake_AbsentHandCount_DefaultsTo4()
    {
        var gameId = $"hc-absent-{Guid.NewGuid():N}";
        await using var session = await ConnectAsync("seat=0&botCount=3", gameId);
        var runtimeGameId = await SeatTakeAndBindAsync(session, seat: 0, gameId);

        Assert.True(_factory!.Services.GetRequiredService<IChangshaGameRuntime>()
            .TryGetSnapshot(runtimeGameId, out var s) && s is not null);
        Assert.Equal(4, s!.MaxHands);
    }

    // ── first-creator-wins: seed + handCount ───────────────────────────────────────

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-2")]
    public async Task FirstCreatorWins_LaterJoinerHandCount_DoesNotReCapBoundGame()
    {
        var gameId = $"fcw-hc-{Guid.NewGuid():N}";
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();

        // Creator A binds the game with handCount=8.
        await using var creator = await ConnectAsync("seat=0&botCount=1&handCount=8", gameId);
        var runtimeGameIdA = await SeatTakeAndBindAsync(creator, seat: 0, gameId);
        Assert.True(runtime.TryGetSnapshot(runtimeGameIdA, out var afterCreate) && afterCreate is not null);
        Assert.Equal(8, afterCreate!.MaxHands);
        var seedAtCreate = afterCreate.Seed;

        // Late joiner B on the SAME relay gameId requests handCount=16 — must be ignored.
        await using var joiner = await ConnectAsync("seat=2&botCount=3&handCount=16", gameId);
        var runtimeGameIdB = await SeatTakeAndBindAsync(joiner, seat: 2, gameId);

        Assert.Equal(runtimeGameIdA, runtimeGameIdB); // same bound game, not re-created
        Assert.True(runtime.TryGetSnapshot(runtimeGameIdB, out var afterJoin) && afterJoin is not null);
        Assert.Equal(8, afterJoin!.MaxHands);        // first creator's handCount stands
        Assert.Equal(seedAtCreate, afterJoin.Seed);  // and the (create-once) seed is unchanged too
    }

    // ── botDifficulty join semantics ───────────────────────────────────────────────

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-2")]
    public async Task FirstCreatorWins_LaterJoinerBotDifficulty_CannotMutateBoundGame()
    {
        var gameId = $"fcw-diff-{Guid.NewGuid():N}";
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();

        // Creator A binds the game with botDifficulty=Easy.
        await using var creator = await ConnectAsync("seat=0&botCount=1&botDifficulty=Easy", gameId);
        var runtimeGameId = await SeatTakeAndBindAsync(creator, seat: 0, gameId);
        await WaitForAsync(() => runtime.GetActiveBotDifficulty(runtimeGameId) == "easy", 2000);
        Assert.Equal("easy", runtime.GetActiveBotDifficulty(runtimeGameId));

        // Late joiner B requests botDifficulty=Hard on the SAME game. Pre-#121 this re-applied
        // SetBotStrategyAsync and silently re-skinned the bots; now it is a no-op (first-creator-wins).
        await using var joiner = await ConnectAsync("seat=2&botCount=3&botDifficulty=Hard", gameId);
        _ = await SeatTakeAndBindAsync(joiner, seat: 2, gameId);

        // Give any (incorrect) re-apply time to land, then assert the difficulty is unchanged.
        await Task.Delay(300);
        Assert.Equal("easy", runtime.GetActiveBotDifficulty(runtimeGameId));
    }

    // ── persistence + hydration of MaxHands ────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Contract", "C-2")]
    public async Task MaxHands_PersistsAndHydrates_AcrossRuntimeRestart()
    {
        var sqlitePath = NewSqlitePath();
        string gameId;

        // Runtime instance A: create a game with handCount=16 (persisted on create).
        await using (var factoryA = BuildPersistentFactory(sqlitePath))
        {
            var runtimeA = factoryA.Services.GetRequiredService<IChangshaGameRuntime>();
            gameId = await runtimeA.CreateGameAsync(
                seed: 4242, botSeatIndexes: new[] { 1, 2, 3 },
                hostPlayerId: null, hostConnectionId: null, maxHands: 16);
            Assert.True(runtimeA.TryGetSnapshot(gameId, out var created) && created is not null);
            Assert.Equal(16, created!.MaxHands);
        }

        // Fresh runtime instance B on the same DB: hydrate and confirm MaxHands survived the restart.
        await using var factoryB = BuildPersistentFactory(sqlitePath);
        var runtimeB = factoryB.Services.GetRequiredService<IChangshaGameRuntime>();
        await runtimeB.HydrateAsync(factoryB.Services);

        Assert.True(runtimeB.TryGetSnapshot(gameId, out var hydrated) && hydrated is not null);
        Assert.Equal(16, hydrated!.MaxHands);

        TryDeleteSqlite(sqlitePath);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<WsSession> ConnectAsync(string query, string gameId)
    {
        using var http = _factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var response = await http.PostAsync("/api/identity", null);
        response.EnsureSuccessStatusCode();
        using var identity = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var playerId = identity.RootElement.GetProperty("playerId").GetString()!;
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(PlayerIdentityService.CookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["Cookie"] = cookie;
        var uri = new Uri(server.BaseAddress, $"autotable/ws?{query}&gameId={Uri.EscapeDataString(gameId)}");
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        var session = new WsSession(ws, playerId);
        await session.SendJoinAsync(gameId);
        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // initial UPDATE
        return session;
    }

    private async Task<string> SeatTakeAndBindAsync(WsSession session, int seat, string gameId)
    {
        await session.SendUpdateAsync(new object[] { new object[] { "seats", session.PlayerId, new { seat } } });
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtimeGameId = await WaitForBindingAsync(manager, gameId, timeoutMs: 3000);
        Assert.NotNull(runtimeGameId);
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        await WaitForAsync(() => runtime.TryGetSeatForPlayer(runtimeGameId!, session.PlayerId) == seat, 3000);
        Assert.Equal(seat, runtime.TryGetSeatForPlayer(runtimeGameId!, session.PlayerId));
        return runtimeGameId!;
    }

    private static async Task<string?> WaitForBindingAsync(AutotableConnectionManager manager, string relayGameId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var rid = manager.GetRuntimeGameIdBoundTo(relayGameId);
            if (rid is not null) return rid;
            await Task.Delay(25);
        }
        return manager.GetRuntimeGameIdBoundTo(relayGameId);
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }

    private static string NewSqlitePath()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, $"config-join-persist-{Guid.NewGuid():N}.db");
    }

    private static void TryDeleteSqlite(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Builds a persist-enabled factory pinned to SQLite at <paramref name="sqlitePath"/>
    /// (mirrors the DbContext-rebind pattern in GameCompletionLifecycleTests so the Postgres matrix
    /// cell still boots the SQLite provider this test seeds).</summary>
    private static WebApplicationFactory<Program> BuildPersistentFactory(string sqlitePath)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={sqlitePath}");
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
                    o.BotTurnDelayMs = 5_000;
                    o.BotClaimDelayMs = 5_000;
                    o.ClaimWindowTimeoutMs = 5_000;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = true;
                });
            });
        });
        _ = factory.Server;
        return factory;
    }

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
             && d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>)
             && d.ServiceType.GetGenericArguments()[0].FullName?.StartsWith("Mahjong.Autotable.Api") == true)
            ||
            d.ServiceType == typeof(DbContextOptions)
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

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public string PlayerId { get; }
        public WsSession(WebSocket ws, string playerId) { _ws = ws; PlayerId = playerId; }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SendUpdateAsync(object[] entries)
        {
            var msg = JsonSerializer.Serialize(new { type = "UPDATE", entries, full = false });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<JsonElement> ReadEnvelopeAsync(int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None); }
                catch { }
            }
            _ws.Dispose();
        }
    }
}
