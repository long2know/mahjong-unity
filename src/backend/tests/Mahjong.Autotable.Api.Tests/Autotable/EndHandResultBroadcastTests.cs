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
/// #137 — the autotable WS must broadcast EVERY hand's transient EndHand result to the
/// client. A spectator watching an all-bot 4-hand game must receive a populated
/// <c>result['current']</c> (a null→present flip) for each of the 4 hands, because the
/// bundle's hand-end observer latches on exactly that flip.
///
/// <para><b>Regression.</b> The fire-and-forget WS broadcast was a re-read: the runtime
/// fired <c>StateChanged</c> at EndHand (under the per-game lock), but the broadcast task
/// re-acquired the lock and read the state LATER — after <c>StartNextHandOrEndAsync</c> had
/// already rotated the banker into the next hand's <c>RollingDice</c> and tombstoned
/// <c>result['current']</c>. The transient EndHand result was silently dropped, the observer
/// under-counted hand-ends, and the 4-hand playability gate stalled at <c>handEnds &lt; 4</c>
/// even though the game completed. The runtime now hands the snapshot it froze at the mutation
/// instant straight to an ordered per-connection broadcast drainer, so no transition is lost.</para>
/// </summary>
public sealed class EndHandResultBroadcastTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"endhand-bcast-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(o =>
            {
                // Fast bots so the 4-hand game races through EndHand→next-hand quickly —
                // exactly the tight transient window the broadcast race preyed on.
                o.BotTurnDelayMs = 1;
                o.BotClaimDelayMs = 1;
                o.BotPickupDelayMs = 1;
                o.ClaimWindowTimeoutMs = 20;
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
    public async Task AllBotFourHandGame_BroadcastsResultCurrent_ForEveryHand()
    {
        var gameId = $"endhand-bcast-{Guid.NewGuid():N}";
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        // ?botCount=4 with no ?seat= auto-promotes to spectator; the server fills all four
        // seats with bots and self-plays (TryAutoDealForSpectatorAsync). handCount=4 => MaxHands=4.
        var path = $"autotable/ws?gameId={Uri.EscapeDataString(gameId)}&botCount=4&dealMode=auto&handCount=4";
        var ws = await wsClient.ConnectAsync(new Uri(server.BaseAddress, path), CancellationToken.None);
        await using var session = new WsSession(ws);
        await session.SendJoinAsync(gameId);

        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var manager = _factory.Services.GetRequiredService<AutotableConnectionManager>();

        // Latch every null→present flip of result['current'] over the wire — identical to the
        // bundle's installHandEndObserver. Each flip is one hand-end the client can render.
        var handEndFlips = 0;
        var resultPopulated = false;
        var gameComplete = false;
        var deadline = DateTime.UtcNow.AddSeconds(90);

        while (DateTime.UtcNow < deadline && handEndFlips < 4)
        {
            JsonElement env;
            try { env = await session.ReadEnvelopeAsync(timeoutMs: 2000); }
            catch
            {
                var ridle = manager.GetRuntimeGameIdBoundTo(gameId);
                if (ridle is not null && runtime.TryGetSnapshot(ridle, out var si) && si is not null && si.IsGameComplete)
                {
                    gameComplete = true;
                    break;
                }
                continue;
            }
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
                if (e[2].ValueKind == JsonValueKind.Object)
                {
                    if (!resultPopulated) { resultPopulated = true; handEndFlips++; }
                }
                else
                {
                    resultPopulated = false; // explicit tombstone (or absent)
                }
            }
        }

        // Sanity: the all-bot game actually completed (it isn't a case of the table never dealing).
        var rid = manager.GetRuntimeGameIdBoundTo(gameId);
        if (!gameComplete && rid is not null && runtime.TryGetSnapshot(rid, out var s) && s is not null)
            gameComplete = s.IsGameComplete;
        Assert.True(gameComplete || handEndFlips >= 4,
            "precondition: the all-bot spectator game never reached GameComplete (the table didn't self-play).");

        Assert.True(handEndFlips >= 4,
            $"#137: the spectator received result['current'] populated for only {handEndFlips} of 4 hands. " +
            "The transient EndHand result is being dropped by the fire-and-forget WS broadcast (re-read after " +
            "RotateBanker tombstones it) — the client's hand-end observer under-counts and the 4-hand " +
            "playability gate stalls at handEnds<4 even though the game completes.");
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
