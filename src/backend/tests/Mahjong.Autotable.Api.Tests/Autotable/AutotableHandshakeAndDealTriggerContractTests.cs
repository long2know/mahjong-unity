using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #120 handoff (Ferro/WP-E) → Bishop/WP-A characterization gate for the two backend items
/// Ferro flagged on <c>AutotableWsEndpoint</c>. Both are pinned here as <b>correct current
/// behavior</b>; neither warranted a production change, because closing them the way the handoff
/// sketched would drift the frozen C-1/C-2 wire contracts.
///
/// <list type="number">
///   <item><b>C-1 — the implicit deal trigger is a bare <c>match[0]</c> heuristic.</b>
///   The endpoint fires the deal when a seated client pushes <c>match[0]={dealer,honba,conditions}</c>
///   during <see cref="ChangshaPhase.Seating"/> — <em>no explicit <c>dealCommand:'start'</c> is
///   required</em> (though it is also accepted, see <c>ManualDealPlumbingAndAutoAckTests</c>). The
///   existing suite only exercised the explicit <c>dealCommand</c> form; this class pins the bare
///   heuristic the real UI actually emits (<c>world.deal()</c>), and proves the trigger is
///   <b>phase-gated + idempotent</b> — a second <c>match</c> push after the deal is a safe no-op, so
///   "treat any pre-deal match push as Deal" cannot re-deal or corrupt a running hand. Making
///   <c>dealCommand</c> mandatory would reject the current FE's vanilla push and drift C-1, so the
///   backend keeps accepting both forms.</item>
///
///   <item><b>C-2 — the handshake params (post Lead C-2 extension).</b>
///   <c>gameId, seat, botCount, variant, dealMode, botDifficulty</c> plus the now-extended
///   optional <c>seed</c> (Ferro/#127) and <c>handCount</c> (this lane) are read at handshake;
///   <c>rulePreset</c> lives on the <em>page</em> URL but is deliberately tolerated-not-applied.
///   This class proves the endpoint applies a whitelisted <c>handCount</c> to
///   <see cref="ChangshaGameState.MaxHands"/>, tolerates <c>rulePreset</c>/unknown params without
///   crashing, and defaults <c>dealMode</c> to Manual when omitted. (Absent <c>handCount</c> ⇒ 4;
///   absent <c>seed</c> ⇒ random. First-creator-wins + <c>handCount</c> whitelist coverage lives in
///   <see cref="AutotableConfigJoinSemanticsTests"/>.)</item>
/// </list>
/// </summary>
public sealed class AutotableHandshakeAndDealTriggerContractTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"handshake-dealtrigger-{Guid.NewGuid():N}.db");

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
                    o.BotPickupDelayMs = 1;
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

    // ── C-1 — bare match[0] deal trigger (no dealCommand) ─────────────────────────

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public async Task BareMatchPush_NoDealCommand_TriggersDeal_FromSeating()
    {
        // The real autotable bundle's Deal button (world.deal()) emits match[0] =
        // { dealer, honba, conditions } with NO dealCommand field. This is the C-1
        // deal trigger Ferro flagged as a "bare heuristic". Prove it fires the deal.
        var (session, runtime, runtimeGameId) = await ConnectSeatAndBindAsync(
            "seat=0&dealMode=manual&botCount=3");
        await using var _ = session;

        // Vanilla deal push — key 0, numeric dealer, and crucially NO dealCommand.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealer = 0, honba = 0, conditions = new { } } }
        });

        var dealt = await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId, out var s) || s is null) return false;
            // Manual mode parks in RollingDice once the deal is triggered.
            return s.Phase == ChangshaPhase.RollingDice && s.DealMode == DealMode.Manual;
        }, timeoutMs: 3000);

        Assert.True(dealt,
            "A bare match[0]={dealer,honba,conditions} push (no dealCommand) must trigger the deal " +
            "from Seating — this is the C-1 heuristic the real UI emits via world.deal().");
    }

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public async Task SecondMatchPush_AfterDeal_IsIdempotentNoOp()
    {
        // "The backend treats any pre-deal client match push as Deal" is SAFE because the
        // trigger is gated on Phase == Seating. Once a game is dealt, a repeated match push
        // (e.g. a double-clicked Deal, or the bundle re-broadcasting honba) must NOT re-deal
        // or throw — it is a no-op that leaves the running hand untouched.
        var (session, runtime, runtimeGameId) = await ConnectSeatAndBindAsync(
            "seat=0&dealMode=auto&botCount=3");
        await using var _ = session;

        // First deal (auto) — reaches AwaitingDiscard with all 53 tiles dealt.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealer = 0, honba = 0, conditions = new { } } }
        });
        var dealtOnce = await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId, out var s) || s is null) return false;
            return s.Phase == ChangshaPhase.AwaitingDiscard
                && s.Hands.Sum(h => h.ConcealedTiles.Count) == 14 + 13 + 13 + 13;
        }, timeoutMs: 3000);
        Assert.True(dealtOnce, "Pre-condition: auto-deal must land in AwaitingDiscard with 53 tiles.");

        // Capture the dealt hand + counters. Seat 0 is the human dealer and it is its turn,
        // so bots do not mutate the state — it is stable to compare against.
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var before) && before is not null);
        var handBefore = before!.Hands.Single(h => h.SeatIndex == 0).ConcealedTiles.ToArray();
        var handNumberBefore = before.HandNumber;
        var phaseBefore = before.Phase;

        // Second match push — the game is already past Seating; must be a no-op.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealer = 0, honba = 0, conditions = new { } } }
        });
        // Also try the explicit form to prove neither trigger re-deals post-Seating.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealCommand = "start" } }
        });

        // Give any (incorrect) re-deal time to manifest.
        await Task.Delay(300);

        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var after) && after is not null);
        var handAfter = after!.Hands.Single(h => h.SeatIndex == 0).ConcealedTiles.ToArray();

        Assert.Equal(phaseBefore, after.Phase);
        Assert.Equal(handNumberBefore, after.HandNumber);
        Assert.Equal(handBefore, handAfter); // no re-shuffle / re-deal
        Assert.Equal(WebSocketState.Open, session.SocketState); // connection survived
    }

    // ── C-2 — six-param handshake; unfrozen params ignored ────────────────────────

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-2")]
    public async Task Handshake_AppliesWhitelistedHandCount_ToleratesRulePreset()
    {
        // Post Lead C-2 extension: `?handCount=` (whitelisted) IS now consumed → MaxHands.
        // `?rulePreset=` remains tolerated-not-applied, and unknown params must not crash the
        // handshake. `?seed=` forwarding is owned + tested by Ferro/#127 (co-signed), so this
        // test does not assert seed behaviour — it only proves seed is tolerated in the query.
        var (session, runtime, runtimeGameId) = await ConnectSeatAndBindAsync(
            "seat=0&dealMode=auto&botCount=3&handCount=8&seed=999&rulePreset=aggressive-east");
        await using var _ = session;

        await session.SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealer = 0, honba = 0, conditions = new { } } }
        });

        var dealt = await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId, out var s) || s is null) return false;
            return s.Phase == ChangshaPhase.AwaitingDiscard
                && s.Hands.Sum(h => h.ConcealedTiles.Count) == 14 + 13 + 13 + 13;
        }, timeoutMs: 3000);
        Assert.True(dealt,
            "The handshake must tolerate rulePreset/seed page-url params and still complete a deal.");

        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var snap) && snap is not null);
        Assert.Equal(8, snap!.MaxHands); // handCount=8 is now consumed → MaxHands=8 (was ignored pre-C-2 extension).
    }

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-2")]
    public async Task Handshake_OmittingDealMode_DefaultsToManual()
    {
        // C-2: Manual is the WS default. A handshake with no ?dealMode= must land the runtime in
        // the manual ceremony (RollingDice) on the bare deal trigger, not auto-deal.
        var (session, runtime, runtimeGameId) = await ConnectSeatAndBindAsync(
            "seat=0&botCount=3"); // note: no dealMode
        await using var _ = session;

        await session.SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealer = 0, honba = 0, conditions = new { } } }
        });

        var manualDefault = await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId, out var s) || s is null) return false;
            return s.DealMode == DealMode.Manual && s.Phase == ChangshaPhase.RollingDice;
        }, timeoutMs: 3000);

        Assert.True(manualDefault,
            "With no ?dealMode= the WS handshake must default to Manual (C-2) — the bare deal " +
            "trigger then parks the runtime in RollingDice, not an atomic auto-deal.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Opens a WS connection with the supplied query (a fresh gameId is appended),
    /// completes JOIN + seat-take, waits for the runtime binding, and drains setup frames.</summary>
    private async Task<(WsSession Session, IChangshaGameRuntime Runtime, string RuntimeGameId)>
        ConnectSeatAndBindAsync(string query)
    {
        var gameId = $"c120-{Guid.NewGuid():N}";
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?{query}&gameId={Uri.EscapeDataString(gameId)}";
        var uri = new Uri(server.BaseAddress, path);
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        var session = new WsSession(ws);

        await session.SendJoinAsync(gameId);
        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // initial full UPDATE

        // Take seat 0 → binds the relay gameId to a Changsha runtime game.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "seats", 0, new { seat = 0 } }
        });

        var manager = _factory.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var runtimeGameId = await WaitForBindingAsync(manager, gameId, timeoutMs: 3000);
        Assert.NotNull(runtimeGameId);

        await DrainAsync(session, timeoutMs: 400);
        return (session, runtime, runtimeGameId!);
    }

    private static async Task<string?> WaitForBindingAsync(
        AutotableConnectionManager manager, string relayGameId, int timeoutMs)
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

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private static async Task DrainAsync(WsSession session, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { _ = await session.ReadEnvelopeAsync(timeoutMs: 50); }
            catch { return; }
        }
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WebSocketState SocketState => _ws.State;
        public WsSession(WebSocket ws) { _ws = ws; }

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
