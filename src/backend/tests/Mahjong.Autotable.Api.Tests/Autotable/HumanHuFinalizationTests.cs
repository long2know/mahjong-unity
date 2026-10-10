using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tests.Changsha.Acceptance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TH = Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #137 (P0) — after a human Hu claim is ACCEPTED (#135), the hand must FINALIZE on the wire the
/// bundle listens on: the client must receive <c>result['current']</c> populated with the win so the
/// #result-modal opens and the playability gate counts the hand end (its hand-end latch keys off the
/// autotable-WS <c>result['current']</c> null→present transition, NOT the SignalR ScoringComplete).
///
/// <para>The suspected defect: <c>EndHand</c> is transient — <c>ResolveClaimWindowAsync</c> scores
/// (Phase→EndHand, fires StateChanged) then immediately advances via <c>StartNextHandOrEndAsync</c>,
/// and the fire-and-forget broadcast reads state via async <c>TryGetSnapshotCopyAsync</c> AFTER the
/// advance — so the translator (which emits <c>result['current']</c> only at Phase==EndHand) never
/// broadcasts the win, the modal never opens, and the gate stalls at handEnds=0.</para>
/// </summary>
public sealed class HumanHuFinalizationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"hu-final-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(o =>
            {
                o.BotTurnDelayMs = 1;
                o.BotClaimDelayMs = 1;
                o.BotPickupDelayMs = 1;
                o.ClaimWindowTimeoutMs = 60000;
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

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task HumanHuClaim_BroadcastsResultCurrent_ToClient_SoTheHandFinalizes()
    {
        var gameId = $"hu-final-{Guid.NewGuid():N}";
        var (session, runtime, runtimeGameId) = await ArrangeSeat0HuWindowAsync(gameId);
        await using (session)
        {
            // Drain everything queued up to the point of the claim so the capture window starts clean.
            await DrainAsync(session, 400);

            // The shipped bundle Hu click: claim[0] = { action:'claim', type:'Hu' }.
            await session.SendClaimAsync(seat: 0, action: "claim", type: "Hu");

            // Capture every UPDATE the client receives for the next few seconds and look for the
            // authoritative hand-end signal: result['current'] populated with the winner. This is
            // exactly what the bundle's result collection / the gate's hand-end latch consume.
            var sawResultCurrentPopulated = false;
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && !sawResultCurrentPopulated)
            {
                JsonElement env;
                try { env = await session.ReadEnvelopeAsync(timeoutMs: 600); }
                catch { continue; }
                if (env.ValueKind != JsonValueKind.Object) continue;
                if (!env.TryGetProperty("type", out var t) || t.GetString() != "UPDATE") continue;
                if (!env.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                for (var i = 0; i < entries.GetArrayLength(); i++)
                {
                    var e = entries[i];
                    if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3) continue;
                    if (e[0].GetString() != ChangshaCollectionKinds.Result) continue;
                    var key = e[1].ValueKind == JsonValueKind.String ? e[1].GetString() : e[1].ToString();
                    if (key != "current") continue;
                    if (e[2].ValueKind == JsonValueKind.Object) { sawResultCurrentPopulated = true; break; }
                }
            }

            // Authoritative runtime sanity: the win WAS declared (the claim was accepted).
            var final = await runtime.TryGetSnapshotCopyAsync(runtimeGameId);
            Assert.True(
                final is not null
                && (final.CurrentWin?.WinningSeatIndex == 0
                    || final.EventLog.Any(ev => ev.EventType == "win-declared" && ev.SeatIndex == 0)),
                "precondition: the human Hu claim must be accepted by the runtime.");

            Assert.True(sawResultCurrentPopulated,
                "#137: after the human's Hu claim was accepted, the client never received a populated " +
                "result['current'] over the autotable WS — so the #result-modal never opens and the " +
                "playability gate's hand-end latch never fires (handEnds stays 0 and play stalls). The " +
                "EndHand result must be broadcast to the bundle, not just emitted on SignalR ScoringComplete.");
            Assert.Equal(ChangshaPhase.EndHand, final!.Phase);
            Assert.Equal(1, final.HandNumber);
            Assert.NotNull(final.CurrentScore);
            Assert.Equal(new[] { 0 }, final.HandResultContinuation!.WaitingSeats(final));
        }
    }

    // ── Arrangement (mirrors HumanClaimWireContractTests) ───────────────────────────

    private async Task<(WsSession, IChangshaGameRuntime, string)> ArrangeSeat0HuWindowAsync(string gameId)
    {
        var session = await OpenAsync(seat: 0, gameId);
        await session.SendJoinAsync(gameId);
        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // initial full UPDATE
        await session.SendUpdateAsync(new object[] { new object[] { "seats", 0, new { seat = 0 } } });

        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var runtimeGameId = await WaitForBindingAsync(manager, gameId, 5000)
            ?? throw new InvalidOperationException("runtime never bound.");

        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var configured));
        Assert.Equal(new[] { 1, 2, 3 },
            configured!.Seats.Where(seat => seat.IsBot).Select(seat => seat.SeatIndex).Order());
        Assert.Equal(DealMode.Auto, configured.DealMode);
        var dealt = await WaitForAsync(() =>
            runtime.TryGetSnapshot(runtimeGameId, out var s) && s is not null
            && s.Phase == ChangshaPhase.AwaitingDiscard
            && s.Hands.Sum(h => h.ConcealedTiles.Count) == 14 + 13 + 13 + 13, 5000);
        Assert.True(dealt, "auto-deal did not reach a quiescent AwaitingDiscard.");

        // Seat 0 waits on Wan-1 for the win; seat 1 (bot) discards it — opening a real Hu window.
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var state) && state is not null);
        var discardTile = TH.Tid(Suit.Wan, 1, 0);
        AcceptanceFixture.OverrideHand(state!, 0, AcceptanceFixture.ThirteenTileWaitingForWan1().ToArray());
        AcceptanceFixture.OverrideHand(state!, 1, new[] { discardTile }.Concat(InnocuousHand(3)).ToArray());
        AcceptanceFixture.OverrideHand(state!, 2, InnocuousHand(1));
        AcceptanceFixture.OverrideHand(state!, 3, InnocuousHand(2));
        state!.ClaimWindow = null;
        state.MissedWinSeats.Clear();
        state.ActiveSeatIndex = 1;
        state.Phase = ChangshaPhase.AwaitingDiscard;

        await runtime.DiscardAsync(runtimeGameId, 1, discardTile);
        var opened = await runtime.TryGetSnapshotCopyAsync(runtimeGameId);
        Assert.NotNull(opened);
        Assert.Equal(ChangshaPhase.AwaitingClaim, opened!.Phase);
        Assert.Contains(opened.ClaimWindow!.Opportunities, o => o.SeatIndex == 0 && o.ClaimType == Tables.TableClaimType.Hu);

        return (session, runtime, runtimeGameId);
    }

    private static int[] InnocuousHand(int copy) => new[]
    {
        TH.Tid(Suit.Tiao, 1, copy), TH.Tid(Suit.Tiao, 4, copy), TH.Tid(Suit.Tiao, 7, copy),
        TH.Tid(Suit.Tiao, 2, copy), TH.Tid(Suit.Tiao, 5, copy), TH.Tid(Suit.Tiao, 8, copy),
        TH.Tid(Suit.Tiao, 3, copy), TH.Tid(Suit.Tiao, 6, copy), TH.Tid(Suit.Tiao, 9, copy),
        TH.Tid(Suit.Tong, 6, copy), TH.Tid(Suit.Tong, 7, copy), TH.Tid(Suit.Tong, 8, copy),
    };

    private async Task<WsSession> OpenAsync(int seat, string gameId)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}&dealMode=auto&botCount=3";
        var ws = await wsClient.ConnectAsync(new Uri(server.BaseAddress, path), CancellationToken.None);
        return new WsSession(ws);
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
            try { _ = await session.ReadEnvelopeAsync(timeoutMs: 80); }
            catch { return; }
        }
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SendUpdateAsync(object[] entries)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "UPDATE", entries, full = false }));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public Task SendClaimAsync(int seat, string action, string? type) =>
            SendUpdateAsync(new object[] { new object[] { "claim", seat, new { action, type } } });

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
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch { }
            }
            _ws.Dispose();
        }
    }
}
