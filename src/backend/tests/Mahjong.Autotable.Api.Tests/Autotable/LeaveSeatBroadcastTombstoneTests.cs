using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Ripley prodready-final audit follow-up (2026-06-03, memo
/// <c>.squad/decisions/inbox/ripley-prodready-final.md</c>) — pins the
/// L-10-leave-seat regression that Bishop's first runtime-only fix
/// (commit <c>1febbd8</c>) left unsolved.
///
/// <para><b>Symptom before this fix:</b> when player A presses the
/// in-game "Leave seat" button, the bundle's
/// <c>this.client.seats.set(playerId, { seat: null })</c> at
/// <c>game-ui.ts:580</c> emits a <c>seats[playerId] = { seat: null }</c>
/// UPDATE over the autotable WS. The endpoint routed this into
/// <see cref="IChangshaGameRuntime.ReleaseSeatAsync"/> which DOES clear
/// the runtime seat — but the runtime broadcasts <c>PlayerSeated</c> on
/// the SignalR <c>/hubs/changsha</c> channel, NOT the autotable WS the
/// bundle listens on. The bundle's local in-memory copy of player A's
/// seat was set to null client-side, but every OTHER connection still
/// saw <c>seats[playerA] = { seat: null }</c> (stored verbatim via the
/// passthrough relay) and <c>nicks[playerA] = "Seat 0"</c> (never
/// tombstoned), so the lobby seat-counter stayed at "4/4" and the
/// sidebar showed a ghost player. Only a page refresh recovered the
/// view (the disconnect path correctly emits tombstones via
/// <c>HandleDisconnectAsync</c>).</para>
///
/// <para><b>Fix under test:</b> the leave branch of
/// <c>TryHandleSeatTakeAsync</c> now mirrors the disconnect path —
/// after the runtime release it calls
/// <see cref="AutotableGameState.RemovePlayerEntries"/> on the per-game
/// relay store and broadcasts the resulting <c>(seats|nicks|mouse)[playerA]
/// = null</c> tombstone entries to peers via
/// <c>BroadcastToOthersAsync(..., full: false)</c>. The leave entry
/// itself is also dropped from the passthrough list (signalled via the
/// new boolean return) so a stale <c>{seat:null}</c> value doesn't
/// re-store under the tombstoned key.</para>
///
/// <para>The single end-to-end assertion runs the same wire-protocol
/// path the Playwright spec exercises:</para>
/// <list type="number">
///   <item>Player A and Player B join the same gameId.</item>
///   <item>A takes seat 0 (runtime binds; passthrough mirrors
///     <c>seats[A] = {seat: 0}</c> to B).</item>
///   <item>A sends <c>seats[A] = { seat: null }</c> to leave.</item>
///   <item>B observes <c>seats[A] = null</c> AND <c>nicks[A] = null</c>
///     in its receive stream within 5 seconds.</item>
/// </list>
/// </summary>
public sealed class LeaveSeatBroadcastTombstoneTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"leaveseat-bcast-{Guid.NewGuid():N}.db");

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

    // ─────────────────────────────────────────────────────────────────
    // Acceptance — peer sees seats / nicks tombstone within 5s
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance"), Trait("Audit", "Ripley-L10-followup")]
    public async Task LeaveSeat_BroadcastsSeatsAndNicksTombstones_ToPeer()
    {
        var gameId = $"leave-bcast-{Guid.NewGuid():N}";

        await using var alice = await OpenAndJoinAsync(gameId, seat: 0, botCount: 0);
        await using var bob = await OpenAndJoinAsync(gameId, seat: 1, botCount: 0);

        // Mirror the bundle's on-connect Collection ctor declarations
        // (src/client.ts:131-134): seats / nicks / mouse are perPlayer.
        // RemovePlayerEntries iterates _perPlayerKinds, so without these
        // declarations the relay store wouldn't know to clean per-player
        // keys on a leave. Real bundles always send these — the test
        // explicitly sends them too so the wire path matches production.
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "perPlayer", "seats", JsonSerializer.SerializeToElement(true) },
            new object[] { "perPlayer", "nicks", JsonSerializer.SerializeToElement(true) },
            new object[] { "perPlayer", "mouse", JsonSerializer.SerializeToElement(true) },
        });
        // Drain the echo of these declarations on Bob's side so they
        // don't sit ahead of the assertions.
        await DrainAsync(bob, timeoutMs: 250);

        // Alice takes seat 0. This binds the runtime AND stores
        // seats[Alice] / nicks[Alice] in the per-game relay store.
        // The take entry is mirrored to Bob via passthrough; we drain
        // his queue past that point so the tombstone is the next thing
        // he reads.
        var takeValue = JsonSerializer.SerializeToElement(new { seat = 0 });
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "seats", alice.PlayerId, takeValue }
        });

        // Also push a nicks entry so the tombstone path has something
        // to clear under nicks[Alice]. The bundle does this implicitly
        // via setNick during initial UI bootstrap.
        var nickValue = JsonSerializer.SerializeToElement("Alice");
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "nicks", alice.PlayerId, nickValue }
        });

        // Wait for the runtime to actually own seat 0 before we leave,
        // so the leave-branch's runtime release path is genuinely
        // exercised (not a no-op on an unbound seat).
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        await WaitForAsync(() =>
        {
            var rid = manager.GetRuntimeGameIdBoundTo(gameId);
            if (string.IsNullOrEmpty(rid)) return false;
            return runtime.TryGetSnapshot(rid!, out var s)
                && s is not null
                && string.Equals(s.Seats[0].PlayerId, alice.PlayerId, StringComparison.Ordinal);
        }, timeoutMs: 5000, "runtime did not bind seat 0 to Alice");

        // Drain Bob's stream to flush the take-seat passthrough echo and
        // any runtime-driven StateChanged full snapshots that fired in
        // response. We collect (kind, key, valueKind) tuples for any
        // remaining entries so the leave-tombstones land in a quiet
        // buffer.
        await DrainAsync(bob, timeoutMs: 600);

        // Alice presses Leave seat — the upstream wire shape is exactly
        // `{ "seat": null }` (per Player.svelte / game-ui.ts:580).
        var leaveValue = JsonDocument.Parse("{\"seat\":null}").RootElement.Clone();
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "seats", alice.PlayerId, leaveValue }
        });

        // Bob must observe BOTH seats[alice] = null AND nicks[alice] = null
        // within 5 seconds, on the autotable WS — without any page
        // reload. The tombstones are sent as delta UPDATEs (full: false).
        // The runtime's StateChanged push may also fire as a full UPDATE
        // around the same time; we accept either, but at least one
        // received envelope must contain the (kind, key) → null mapping.
        var (seatsCleared, nicksCleared) = await WaitForTombstonesAsync(
            bob, alice.PlayerId, timeoutMs: 5000);

        Assert.True(seatsCleared,
            $"Ripley L-10 follow-up: Bob never received a seats[{alice.PlayerId}] = null " +
            "tombstone (or null-equivalent in a full snapshot) within 5s of Alice's leave-seat push. " +
            "The autotable WS leave branch must broadcast per-player tombstones to peers — " +
            "mirroring HandleDisconnectAsync's RemovePlayerEntries / BroadcastToOthersAsync.");

        Assert.True(nicksCleared,
            $"Ripley L-10 follow-up: Bob never received a nicks[{alice.PlayerId}] = null " +
            "tombstone (or null-equivalent in a full snapshot) within 5s of Alice's leave-seat push. " +
            "Without this the sidebar's player nickname remains as a ghost entry until refresh.");
    }

    // ─────────────────────────────────────────────────────────────────
    // Acceptance — leave-seat does NOT re-broadcast the {seat:null}
    // entry verbatim (would re-store under playerId and undo tombstone)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance"), Trait("Audit", "Ripley-L10-followup")]
    public async Task LeaveSeat_DoesNotEcho_RawSeatNullPayload_BackToPeers()
    {
        var gameId = $"leave-noecho-{Guid.NewGuid():N}";

        await using var alice = await OpenAndJoinAsync(gameId, seat: 0, botCount: 0);
        await using var bob = await OpenAndJoinAsync(gameId, seat: 1, botCount: 0);

        var takeValue = JsonSerializer.SerializeToElement(new { seat = 0 });
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "seats", alice.PlayerId, takeValue }
        });

        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        await WaitForAsync(() =>
        {
            var rid = manager.GetRuntimeGameIdBoundTo(gameId);
            if (string.IsNullOrEmpty(rid)) return false;
            return runtime.TryGetSnapshot(rid!, out var s)
                && s is not null
                && string.Equals(s.Seats[0].PlayerId, alice.PlayerId, StringComparison.Ordinal);
        }, timeoutMs: 5000, "runtime did not bind seat 0 to Alice");

        await DrainAsync(bob, timeoutMs: 600);

        var leaveValue = JsonDocument.Parse("{\"seat\":null}").RootElement.Clone();
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "seats", alice.PlayerId, leaveValue }
        });

        // Collect everything Bob receives over a 1.5s window. The leave
        // path must NOT re-emit `seats[Alice] = { seat: null }` (an
        // object value) — only a tombstone `seats[Alice] = null` and/or
        // the runtime's full-snapshot with no Alice-keyed entry.
        var observed = await CollectAllAsync(bob, timeoutMs: 1500);

        foreach (var env in observed)
        {
            if (!env.TryGetProperty("entries", out var entries)) continue;
            for (var i = 0; i < entries.GetArrayLength(); i++)
            {
                var kind = entries[i][0].GetString();
                if (!string.Equals(kind, "seats", StringComparison.Ordinal)) continue;
                var key = entries[i][1].ValueKind == JsonValueKind.String
                    ? entries[i][1].GetString()
                    : entries[i][1].ToString();
                if (!string.Equals(key, alice.PlayerId, StringComparison.Ordinal)) continue;

                var value = entries[i][2];
                if (value.ValueKind == JsonValueKind.Null) continue; // tombstone — OK
                if (value.ValueKind != JsonValueKind.Object)
                {
                    Assert.Fail(
                        $"Unexpected non-object, non-null seats[{alice.PlayerId}] value: " +
                        $"{value.ValueKind}");
                }

                // Object value: must NOT carry the leave payload `{seat: null}`.
                // The runtime's translator emits `{seat: N}` (numeric) for an
                // OWNED seat; a `{seat: null}` echo means the passthrough has
                // re-stored under playerId and the tombstone is being
                // immediately undone.
                if (value.TryGetProperty("seat", out var seatProp)
                    && seatProp.ValueKind == JsonValueKind.Null)
                {
                    Assert.Fail(
                        $"Ripley L-10 follow-up: peer received seats[{alice.PlayerId}] = " +
                        "{seat:null} on the wire. The leave-seat branch must NOT passthrough " +
                        "the raw entry — it must own the broadcast via tombstone-only.");
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<RelaySession> OpenAndJoinAsync(string gameId, int seat, int botCount)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}&botCount={botCount}";
        var uri = new Uri(server.BaseAddress, path);
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        var session = new RelaySession(ws);
        await session.SendJoinAsync(gameId);
        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        session.PlayerId = joined.GetProperty("playerId").GetString()
            ?? throw new InvalidOperationException("JOINED envelope missing playerId.");
        var snapshot = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
        return session;
    }

    private static async Task DrainAsync(RelaySession session, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var remaining = (int)Math.Max(50,
                    (deadline - DateTime.UtcNow).TotalMilliseconds);
                _ = await session.ReadEnvelopeAsync(timeoutMs: Math.Min(remaining, 150));
            }
            catch
            {
                return;
            }
        }
    }

    private static async Task<List<JsonElement>> CollectAllAsync(RelaySession session, int timeoutMs)
    {
        var observed = new List<JsonElement>();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var remaining = (int)Math.Max(50,
                    (deadline - DateTime.UtcNow).TotalMilliseconds);
                var env = await session.ReadEnvelopeAsync(timeoutMs: Math.Min(remaining, 250));
                observed.Add(env);
            }
            catch
            {
                break;
            }
        }
        return observed;
    }

    private static async Task<(bool seatsCleared, bool nicksCleared)> WaitForTombstonesAsync(
        RelaySession session, string playerId, int timeoutMs)
    {
        var seatsCleared = false;
        var nicksCleared = false;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && (!seatsCleared || !nicksCleared))
        {
            JsonElement env;
            try
            {
                var remaining = (int)Math.Max(50,
                    (deadline - DateTime.UtcNow).TotalMilliseconds);
                env = await session.ReadEnvelopeAsync(timeoutMs: Math.Min(remaining, 500));
            }
            catch
            {
                continue;
            }

            if (env.ValueKind != JsonValueKind.Object) continue;
            if (!env.TryGetProperty("type", out var typeProp)) continue;
            if (typeProp.GetString() != "UPDATE") continue;
            if (!env.TryGetProperty("entries", out var entries)) continue;

            var isFull = env.TryGetProperty("full", out var fullProp)
                && fullProp.ValueKind == JsonValueKind.True;

            // Track which (kind, playerId) pairs are present in this
            // envelope so we can deduce "absent from full snapshot" as
            // an equivalent tombstone.
            var seatsPresentInThisFull = false;
            var nicksPresentInThisFull = false;

            for (var i = 0; i < entries.GetArrayLength(); i++)
            {
                var kind = entries[i][0].GetString();
                var key = entries[i][1].ValueKind == JsonValueKind.String
                    ? entries[i][1].GetString()
                    : entries[i][1].ToString();
                if (!string.Equals(key, playerId, StringComparison.Ordinal)) continue;

                var value = entries[i][2];
                var valueIsNull = value.ValueKind == JsonValueKind.Null;

                if (string.Equals(kind, "seats", StringComparison.Ordinal))
                {
                    if (valueIsNull) seatsCleared = true;
                    if (isFull) seatsPresentInThisFull = true;
                }
                else if (string.Equals(kind, "nicks", StringComparison.Ordinal))
                {
                    if (valueIsNull) nicksCleared = true;
                    if (isFull) nicksPresentInThisFull = true;
                }
            }

            // A full snapshot from the runtime that simply omits the
            // playerId key is also a valid post-tombstone observation —
            // peers must reconcile to "Alice has no per-player entries".
            if (isFull)
            {
                if (!seatsPresentInThisFull) seatsCleared = true;
                if (!nicksPresentInThisFull) nicksCleared = true;
            }
        }
        return (seatsCleared, nicksCleared);
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs, string reason)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        if (!predicate())
        {
            throw new Xunit.Sdk.XunitException(
                $"WaitForAsync timed out after {timeoutMs}ms: {reason}");
        }
    }

    private sealed class RelaySession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public string PlayerId { get; set; } = string.Empty;
        public RelaySession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SendUpdateAsync(IEnumerable<object[]> entries)
        {
            using var ms = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "UPDATE");
                writer.WritePropertyName("entries");
                writer.WriteStartArray();
                foreach (var e in entries)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue((string)e[0]);
                    switch (e[1])
                    {
                        case string s: writer.WriteStringValue(s); break;
                        case int i: writer.WriteNumberValue(i); break;
                        case long l: writer.WriteNumberValue(l); break;
                        default: throw new InvalidOperationException("unsupported key type");
                    }
                    if (e[2] is JsonElement je)
                    {
                        je.WriteTo(writer);
                    }
                    else
                    {
                        JsonSerializer.Serialize(writer, e[2]);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteBoolean("full", false);
                writer.WriteEndObject();
            }
            var bytes = ms.ToArray();
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
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None); }
                catch { }
            }
            _ws.Dispose();
        }
    }
}
