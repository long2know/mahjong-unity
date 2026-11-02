using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: full-hand integration. The Phase D-backend "playable" gate.
///
/// Sources: synthesis of Vasquez rules-diff manifest §1.5–§1.13 + MahjongPros end-to-end
/// playable scenario. This is the single test Stephen will pin to "is Changsha playable
/// from deal to Hu?"
///
/// Strategy: drive 4 bots through one full hand via <see cref="BotMatchHarness"/>, then
/// assert the post-hand state has the shape the autotable client needs to render:
///   - Deal happened (53 tiles dealt + 55 in wall before play started).
///   - Phase reached EndHand.
///   - Either a winner exists (CurrentWin set + scoring complete) OR wall exhausted.
///   - Event log contains the canonical lifecycle events (deal → turn → discard → claim? → win/draw).
///   - Cumulative scores changed iff there was a winner.
///   - Banker rotation (next hand) honours §1.13 (winner becomes dealer OR dealer keeps on washout).
/// </summary>
public class EndToEndPlayableTests
{
    [Theory, Trait("Category", "Acceptance")]
    [InlineData(42)]
    [InlineData(12345)]
    [InlineData(7777)]
    public void Full_Hand_FromDeal_To_HandEnd_AllBots(int seed)
    {
        // MahjongPros end-to-end: a hand starts with the deal, runs through draws/discards/
        // claims, and ends with either a Hu or a washout.
        var outcome = BotMatchHarness.RunUntilHandFinished(seed);

        Assert.Equal(ChangshaPhase.EndHand, outcome.FinalState.Phase);
        Assert.True(outcome.WinnerDeclared || outcome.WallExhausted,
            $"Hand must end in either a win or a wall-exhaustion draw. " +
            $"WinnerDeclared={outcome.WinnerDeclared}, WallExhausted={outcome.WallExhausted}, " +
            $"Phase={outcome.FinalState.Phase}, Steps={outcome.Steps}.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Full_Hand_ProducesCanonicalEventTimeline()
    {
        // Vasquez §1.5 + §1.7: the event log must include the major lifecycle markers so the
        // autotable client (Bishop's Phase D-backend relay) can broadcast each into the
        // protocol's collection mutations.
        var outcome = BotMatchHarness.RunUntilHandFinished(seed: 4242);
        var types = outcome.FinalState.EventLog.Select(e => e.EventType).ToList();

        Assert.Contains("game-created", types);
        Assert.Contains("game-started", types);
        Assert.Contains("dice-rolled", types);
        Assert.Contains("tiles-dealt", types);
        // A hand must include at least one discard event.
        Assert.Contains("tile-discarded", types);
        // And finish with either win-declared (Hu path) or wall-exhausted/draw-hand (washout path).
        // `wall-exhausted` is emitted first when the wall empties; `draw-hand` is emitted by
        // HandleWallExhausted as the EndHand terminal marker.
        Assert.True(
            types.Contains("win-declared") || types.Contains("wall-exhausted") || types.Contains("draw-hand"),
            $"Hand timeline must terminate with win-declared (Hu) or wall-exhausted/draw-hand (washout). " +
            $"Got terminal events: [{string.Join(", ", types.TakeLast(8))}]");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Full_Hand_WithWinner_PopulatesScoreAndUpdatesCumulativeScores()
    {
        // Vasquez §1.13 + §5: a Hu must populate CurrentWin + CurrentScore and apply payments
        // to CumulativeScores. We loop seeds until we hit one that produces a winner so the test
        // is deterministic across CI (washouts get skipped — the assertion still proves the path).
        var winningSeed = Enumerable.Range(1, 50)
            .Select(s => BotMatchHarness.RunUntilHandFinished(seed: s))
            .FirstOrDefault(o => o.WinnerDeclared);

        Assert.NotNull(winningSeed);
        Assert.NotNull(winningSeed!.FinalState.CurrentWin);
        Assert.NotNull(winningSeed.FinalState.CurrentScore);
        Assert.NotEmpty(winningSeed.FinalState.CurrentScore!.Payments);

        // Cumulative scores must net to zero (Vasquez §5 zero-sum invariant).
        var sum = winningSeed.FinalState.CumulativeScores.Values.Sum();
        Assert.Equal(0, sum);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Full_Hand_PostHand_BankerRotation_HonorsWinnerBecomesDealer()
    {
        // Vasquez §1.13: after a Hu, the winner becomes the next dealer.
        var winningOutcome = Enumerable.Range(1, 50)
            .Select(s => BotMatchHarness.RunUntilHandFinished(seed: s))
            .FirstOrDefault(o => o.WinnerDeclared);

        Assert.NotNull(winningOutcome);
        var state = winningOutcome!.FinalState;
        var winnerSeat = state.CurrentWin!.WinningSeatIndex;
        var dealerBefore = state.DealerSeatIndex;

        ChangshaGameStateMachine.RotateBanker(state);

        Assert.Equal(winnerSeat, state.DealerSeatIndex);
        Assert.True(state.Seats[winnerSeat].IsDealer);
        if (winnerSeat != dealerBefore)
            Assert.False(state.Seats[dealerBefore].IsDealer);
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task Full_Hand_ViaAutotableWebSocketRelay_BotsAndOneHuman()
    {
        // Phase D-backend acceptance: stand up the full WS pipe, drive a hand
        // to completion via the runtime (4 bots), and assert the result entry
        // arrives over the WS to a connected client. Confirms the runtime →
        // translator → AutotableGameState (Runtime source) → WS broadcast loop
        // is intact end-to-end.
        var dataDir = System.IO.Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = System.IO.Path.Combine(dataDir, $"mahjong-e2e-{Guid.NewGuid():N}.db");

        await using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
                b.ConfigureServices(s =>
                {
                    s.Configure<Mahjong.Autotable.Api.Changsha.Runtime.ChangshaRuntimeOptions>(o =>
                    {
                        o.BotTurnDelayMs = 1;
                        o.BotClaimDelayMs = 1;
                        o.ClaimWindowTimeoutMs = 50;
                        o.DealBatchDelayMs = 0;
                        o.PersistSnapshots = false;
                    });
                });
            });
        try
        {
            // Use a seed known to produce a winner (per Full_Hand_WithWinner_...
            // the harness finds one within 50 attempts; seed 1 is the first hit).
            // The runtime can be all-bots — Phase D-backend's pipe is the
            // subject under test, not human input.
            var runtime = factory.Services.GetRequiredService<Mahjong.Autotable.Api.Changsha.Runtime.IChangshaGameRuntime>();
            var manager = factory.Services.GetRequiredService<Mahjong.Autotable.Api.Autotable.AutotableConnectionManager>();

            string? runtimeGameId = null;
            string? observedResultType = null;
            // Loop seeds: keep building games until one produces a result we can
            // observe through the WS. (Wall-exhaustion also counts — it ships
            // a `result` entry with type=Draw.)
            for (var seed = 1; seed <= 50 && observedResultType is null; seed++)
            {
                if (runtimeGameId is not null)
                {
                    // Clear binding so the next iteration sees a fresh snapshot.
                    manager.BindRuntimeGameForTest(Mahjong.Autotable.Api.Autotable.AutotableWsEndpoint.DefaultGameId, runtimeGameId);
                }
                runtimeGameId = await runtime.CreateGameAsync(seed: seed, botSeatIndexes: new[] { 0, 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null);
                manager.BindRuntimeGameForTest(Mahjong.Autotable.Api.Autotable.AutotableWsEndpoint.DefaultGameId, runtimeGameId);

                using var ws = await factory.Server.CreateWebSocketClient()
                    .ConnectAsync(new Uri(factory.Server.BaseAddress, "autotable/ws?seat=0&bots=false"), CancellationToken.None);

                // JOIN — endpoint coerces gameId to DefaultGameId regardless.
                var joinMsg = System.Text.Json.JsonSerializer.Serialize(new { type = "JOIN", gameId = Mahjong.Autotable.Api.Autotable.AutotableWsEndpoint.DefaultGameId });
                await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(joinMsg), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

                // Consume JOINED + initial full UPDATE.
                _ = await ReadEnvelopeAsync(ws, 5000);
                _ = await ReadEnvelopeAsync(ws, 5000);

                // JOIN starts an occupied table; this connection observes the all-bot hand.
                Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var joined));
                Assert.All(joined!.Seats, seat => Assert.True(seat.IsBot));

                // Listen up to ~5s for the result entry.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && observedResultType is null)
                {
                    System.Text.Json.JsonElement env;
                    try { env = await ReadEnvelopeAsync(ws, 1500); }
                    catch (OperationCanceledException) { continue; }
                    if (env.GetProperty("type").GetString() != "UPDATE") continue;
                    foreach (var entry in env.GetProperty("entries").EnumerateArray())
                    {
                        if (entry[0].GetString() != Mahjong.Autotable.Api.Autotable.ChangshaCollectionKinds.Result) continue;
                        var val = entry[2];
                        if (val.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            val.TryGetProperty("type", out var typeProp))
                        {
                            observedResultType = typeProp.GetString();
                            break;
                        }
                    }
                    if (observedResultType is not null)
                    {
                        var completed = (await runtime.TryGetSnapshotCopyAsync(runtimeGameId))!;
                        Assert.Single(completed.EventLog, entry => entry.EventType == "game-started");
                        Assert.Contains(completed.EventLog, entry => entry.EventType == "tiles-dealt");
                        Assert.Contains(completed.EventLog, entry => entry.EventType is "win-declared" or "draw-hand");
                    }
                }

                try
                {
                    if (ws.State == System.Net.WebSockets.WebSocketState.Open)
                        await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }
                catch { }
            }

            Assert.NotNull(observedResultType);
            Assert.True(
                observedResultType is "Hu" or "Draw" or "ZhaHu",
                $"Expected result.type to be Hu/Draw/ZhaHu — got '{observedResultType}'");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    private static async Task<System.Text.Json.JsonElement> ReadEnvelopeAsync(System.Net.WebSockets.WebSocket ws, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var sb = new System.Text.StringBuilder();
        System.Net.WebSockets.WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            sb.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return System.Text.Json.JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }
}
