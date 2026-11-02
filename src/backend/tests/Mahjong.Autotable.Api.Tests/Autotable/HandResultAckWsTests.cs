using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Tests.RulesQualification;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Mahjong.Autotable.Api.Tests.RulesQualification.HudsonOwnTurnWsFixture;

namespace Mahjong.Autotable.Api.Tests.Autotable;

public sealed class HandResultAckWsTests
{
    [Theory]
    [InlineData(DealMode.Auto)]
    [InlineData(DealMode.Manual)]
    public async Task RealWsContinue_HoldsResultForAllOwnersAndRejectsOldHandOnNextResult(DealMode mode)
    {
        await using var host = new HudsonOwnTurnWsFixture();
        await using var table = await OpenTable(host, mode);
        await using var second = await Connect(host, table.RoomId, mode, seat: 1);
        await Win(host, table);
        var full = await table.Peer.BarrierAsync();
        ViewerAuthorityAssertions.Expect(full, table.RoomId, 0);
        var result = Entry(full, "result");
        var continuation = result.GetProperty("continuation");
        Assert.Equal(new[] { 0, 1 }, Seats(continuation, "waitingSeats"));
        Assert.Equal(table.GameId, continuation.GetProperty("gameId").GetString());
        Assert.NotEqual(table.RoomId, continuation.GetProperty("gameId").GetString());
        var scores = result.GetProperty("score").GetRawText();
        var wall = table.State.Wall.ToArray();

        // JOIN, metadata and a forged result push cannot stand in for Continue.
        await table.Peer.UpdateAsync([
            new object[] { "nicks", table.Peer.PlayerId, "refreshed" },
            new object[] { "result", "current", new { continuation = (object?)null } }
        ]);
        var refreshed = Entry(await table.Peer.BarrierAsync(), "result");
        Assert.Equal(continuation.GetRawText(), refreshed.GetProperty("continuation").GetRawText());
        Assert.Equal(scores, refreshed.GetProperty("score").GetRawText());
        Assert.Equal(wall, table.State.Wall);
        Assert.Equal(1, table.State.HandNumber);

        await Ack(table.Peer, continuation);
        var waiting = Entry(await table.Peer.BarrierAsync(), "result").GetProperty("continuation");
        Assert.Equal(new[] { 0 }, Seats(waiting, "acknowledgedSeats"));
        Assert.Equal(new[] { 1 }, Seats(waiting, "waitingSeats"));
        Assert.Equal(ChangshaPhase.EndHand, table.State.Phase);
        await Ack(table.Peer, continuation);
        await Rejected(table.Peer, "hand-result-already-acknowledged");
        await second.UpdateAsync([new object[] { "handResultAck", "current", new
        {
            gameId = table.GameId, handNumber = 1, resultToken = Guid.NewGuid().ToString("N")
        } }]);
        await Rejected(second, "stale-hand-result");
        Assert.Equal(wall, table.State.Wall);

        await Ack(second, continuation);
        Assert.Equal(JsonValueKind.Null, Entry(await second.BarrierAsync(), "result").ValueKind);
        Assert.Equal(2, table.State.HandNumber);
        Assert.Equal(mode == DealMode.Manual ? ChangshaPhase.RollingDice : ChangshaPhase.AwaitingDiscard, table.State.Phase);
        Assert.Single(table.State.EventLog, entry => entry.EventType == "banker-rotated");
        await Ack(table.Peer, continuation);
        await Rejected(table.Peer, "stale-hand-result");
        Assert.Equal(2, table.State.HandNumber);

        await Win(host, table);
        var next = Entry(await table.Peer.BarrierAsync(), "result").GetProperty("continuation");
        Assert.Equal(2, next.GetProperty("handNumber").GetInt32());
        Assert.NotEqual(continuation.GetProperty("resultToken").GetString(), next.GetProperty("resultToken").GetString());
        await Ack(second, continuation);
        await Rejected(second, "stale-hand-result");
        Assert.Equal(new[] { 0, 1 }, table.State.HandResultContinuation!.WaitingSeats(table.State));
        await Task.WhenAll(Ack(table.Peer, next), Ack(second, next));
        await Task.WhenAll(table.Peer.BarrierAsync(), second.BarrierAsync());
        Assert.Equal(3, table.State.HandNumber);
        Assert.Equal(2, table.State.EventLog.Count(entry => entry.EventType == "banker-rotated"));
    }

    [Fact]
    public async Task SpectatorsDuplicateTabsOtherRoomsAndOtherTransportCannotAcknowledge()
    {
        await using var host = new HudsonOwnTurnWsFixture();
        await using var table = await OpenTable(host, DealMode.Manual);
        await using var second = await Connect(host, table.RoomId, DealMode.Manual, seat: 1);
        await Win(host, table);
        var continuation = Entry(await table.Peer.BarrierAsync(), "result").GetProperty("continuation");
        var original = JsonSerializer.Serialize(await host.SnapshotAsync(table));

        await using var duplicate = await Connect(host, table.RoomId, DealMode.Manual, playerId: table.Peer.PlayerId);
        ViewerAuthorityAssertions.Expect(await duplicate.BarrierAsync(), table.RoomId, null);
        await Ack(duplicate, continuation);
        await Rejected(duplicate, "connection-owns-no-seat");
        await using var spectator = await Connect(host, table.RoomId, DealMode.Manual,
            playerId: second.PlayerId, spectator: true);
        ViewerAuthorityAssertions.Expect(await spectator.BarrierAsync(), table.RoomId, null);
        await Ack(spectator, continuation);
        await Rejected(spectator, "spectator-owns-no-seat");

        await using var other = await OpenTable(host, DealMode.Manual);
        await Ack(other.Peer, continuation);
        await Rejected(other.Peer, "stale-game");
        await table.Peer.UpdateAsync([new object[] { "handResultAck", "current", new
        {
            gameId = table.GameId, handNumber = 1,
            resultToken = continuation.GetProperty("resultToken").GetString(),
            seat = 1, playerId = second.PlayerId
        } }]);
        await Rejected(table.Peer, "invalid-hand-result-ack");
        await table.Peer.UpdateAsync([new object[] { "handResultAck", 0, new
        {
            gameId = table.GameId, handNumber = 1, resultToken = continuation.GetProperty("resultToken").GetString()
        } }]);
        await Rejected(table.Peer, "invalid-hand-result-ack");

        await using var hub = await host.ConnectHubAsync(second.PlayerId);
        var rejected = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            hub.InvokeAsync("AcknowledgeHandResult", table.GameId, 1,
                continuation.GetProperty("resultToken").GetString()));
        Assert.Contains("connection-owns-no-seat", rejected.Message, StringComparison.Ordinal);
        await hub.InvokeAsync("AcknowledgeDeal", table.GameId, 1);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            hub.InvokeAsync("StartGame", table.GameId));
        Assert.Equal(original, JsonSerializer.Serialize(await host.SnapshotAsync(table)));
        Assert.Equal(ChangshaPhase.EndHand, table.State.Phase);
    }

    [Fact]
    public async Task LegitimateReconnectGetsHeldResultAndDoesNotAutomaticallyAcknowledge()
    {
        await using var host = new HudsonOwnTurnWsFixture();
        await using var table = await OpenTable(host, DealMode.Manual);
        await using var second = await Connect(host, table.RoomId, DealMode.Manual, seat: 1);
        await Win(host, table);
        var continuation = Entry(await table.Peer.BarrierAsync(), "result").GetProperty("continuation");
        await Ack(table.Peer, continuation);
        await table.Peer.BarrierAsync();
        var previousConnection = ViewerAuthorityAssertions.GrantedConnection(host.Manager, host.Runtime, table.GameId, 1);
        await second.DisposeAsync();
        await host.Runtime.HandleDisconnectAsync(second.PlayerId, previousConnection.Id.ToString("N"));
        Assert.Equal(new[] { 1 }, table.State.HandResultContinuation!.WaitingSeats(table.State));

        await using var returning = await Connect(host, table.RoomId, DealMode.Manual, playerId: second.PlayerId);
        var full = await returning.BarrierAsync();
        ViewerAuthorityAssertions.Expect(full, table.RoomId, 1);
        var resumed = Entry(full, "result").GetProperty("continuation");
        Assert.Equal(continuation.GetProperty("resultToken").GetString(), resumed.GetProperty("resultToken").GetString());
        Assert.Equal(new[] { 0 }, Seats(resumed, "acknowledgedSeats"));
        Assert.Equal(new[] { 1 }, Seats(resumed, "waitingSeats"));
        await returning.BarrierAsync();
        Assert.Equal(1, table.State.HandNumber);
        await Ack(returning, resumed);
        await returning.BarrierAsync();
        Assert.Equal(2, table.State.HandNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserRestartRestoresReadiness_AndUpgradesLegacyAliasBeforeResume(bool legacySnapshot)
    {
        await using var host = new HudsonOwnTurnWsFixture(persist: true);
        await using var table = await OpenTable(host, DealMode.Auto);
        await using var second = await Connect(host, table.RoomId, DealMode.Auto, seat: 1);
        await using var third = await Connect(host, table.RoomId, DealMode.Auto, seat: 2);
        await using var fourth = await Connect(host, table.RoomId, DealMode.Auto, seat: 3);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, table.State.Phase);
        for (var step = 0; step < 512 && table.State.Phase != ChangshaPhase.EndHand; step++)
        {
            if (table.State.Phase == ChangshaPhase.AwaitingClaim)
            {
                foreach (var seat in table.State.ClaimWindow!.Opportunities.Select(option => option.SeatIndex).Distinct().ToArray())
                    if (table.State.Phase == ChangshaPhase.AwaitingClaim)
                        await host.Runtime.PassAsync(table.GameId, seat);
            }
            else
            {
                Assert.Equal(ChangshaPhase.AwaitingDiscard, table.State.Phase);
                await host.Runtime.DiscardAsync(table.GameId, table.State.ActiveSeatIndex,
                    table.State.Hands[table.State.ActiveSeatIndex].ConcealedTiles[0]);
            }
        }
        Assert.Equal(ChangshaPhase.EndHand, table.State.Phase);
        var before = Entry(await table.Peer.BarrierAsync(), "result").GetProperty("continuation");
        if (!legacySnapshot)
        {
            await Ack(table.Peer, before);
            await table.Peer.BarrierAsync();
        }
        await table.Peer.DisposeAsync();
        await second.DisposeAsync();
        await third.DisposeAsync();
        await fourth.DisposeAsync();
        if (legacySnapshot)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = Guid.Parse(table.GameId);
            var row = await db.ChangshaGames.SingleAsync(game => game.Id == id);
            var old = JsonNode.Parse(row.StateJson)!.AsObject();
            old.Remove("requireHandResultAcknowledgements");
            old.Remove("handResultContinuation");
            row.StateJson = old.ToJsonString();
            db.ChangshaGameEvents.RemoveRange(await db.ChangshaGameEvents.Where(entry => entry.GameId == id).ToListAsync());
            await db.SaveChangesAsync();
        }
        await host.RestartAsync();
        await using var restored = await Connect(host, table.RoomId, DealMode.Auto, playerId: table.Peer.PlayerId);
        var full = await restored.BarrierAsync();
        ViewerAuthorityAssertions.Expect(full, table.RoomId, 0);
        var readiness = Entry(full, "result").GetProperty("continuation");
        Assert.Equal(table.GameId, host.Manager.GetRuntimeGameIdBoundTo(table.RoomId));
        Assert.Equal(1, readiness.GetProperty("handNumber").GetInt32());
        Assert.Equal(legacySnapshot ? new[] { 0, 1, 2, 3 } : new[] { 1, 2, 3 }, Seats(readiness, "waitingSeats"));
        Assert.Equal(legacySnapshot ? [] : new[] { 0 }, Seats(readiness, "acknowledgedSeats"));
        Assert.True(host.Runtime.TryGetSnapshot(table.GameId, out var state));
        Assert.True(state!.RequireHandResultAcknowledgements);
        Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        if (!legacySnapshot)
            Assert.Equal(before.GetProperty("resultToken").GetString(), readiness.GetProperty("resultToken").GetString());
    }

    [Fact]
    public async Task FinalWin_UsesGameCompleteWithoutAckOrExtraDeal()
    {
        await using var host = new HudsonOwnTurnWsFixture();
        await using var table = await OpenTable(host, DealMode.Auto);
        await Win(host, table, cap: 1);
        var full = await table.Peer.BarrierAsync();
        Assert.True(Entry(full, "gameComplete").GetProperty("isComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Entry(full, "result").ValueKind);
        Assert.Equal(ChangshaPhase.GameComplete, table.State.Phase);
        Assert.Null(table.State.HandResultContinuation);
        Assert.NotNull(table.State.CurrentScore);
        Assert.DoesNotContain(table.State.EventLog, entry => entry.EventType == "tiles-dealt");
    }

    private static async Task<HumanTable> OpenTable(HudsonOwnTurnWsFixture host, DealMode mode)
    {
        var room = $"result-ws-{Guid.NewGuid():N}";
        var peer = await Connect(host, room, mode, seat: 0);
        var id = host.Manager.GetRuntimeGameIdBoundTo(room)!;
        Assert.True(host.Runtime.TryGetSnapshot(id, out var state));
        Assert.True(state!.RequireHandResultAcknowledgements);
        return new(room, id, 0, state, peer);
    }

    private static async Task<WsPeer> Connect(HudsonOwnTurnWsFixture host, string room,
        DealMode mode, int? seat = null, string? playerId = null, bool spectator = false)
    {
        playerId ??= $"result-player-{Guid.NewGuid():N}";
        var socket = await host.OpenSocketAsync(
            $"variant=changsha&bots=false&botCount=0&dealMode={mode.ToString().ToLowerInvariant()}&handCount=4&seed=20260912{(spectator ? "&seat=-1" : "")}",
            playerId);
        var peer = new WsPeer(socket, room, playerId);
        try
        {
            await peer.BarrierAsync();
            if (seat.HasValue)
            {
                await peer.UpdateAsync([new object[] { "seats", playerId, new { seat = seat.Value } }]);
                await peer.BarrierAsync();
            }
            return peer;
        }
        catch
        {
            await peer.DisposeAsync();
            throw;
        }
    }

    private static async Task Win(HudsonOwnTurnWsFixture host, HumanTable table, int cap = 4)
    {
        var draw = ArrangeBeforeDraw(table, "hu-14");
        table.State.MaxHands = cap;
        await host.AdvanceToDrawAsync(table, draw);
        await table.Peer.UpdateAsync([new object[] { "ownTurn", table.Seat, new
        {
            gameId = table.GameId, expectedVersion = table.State.StateVersion, action = "hu"
        } }]);
        await table.Peer.BarrierAsync();
    }

    private static Task Ack(WsPeer peer, JsonElement continuation) =>
        peer.UpdateAsync([new object[] { "handResultAck", "current", new
        {
            gameId = continuation.GetProperty("gameId").GetString(),
            handNumber = continuation.GetProperty("handNumber").GetInt32(),
            resultToken = continuation.GetProperty("resultToken").GetString()
        } }]);

    private static async Task Rejected(WsPeer peer, string reason)
    {
        var mark = peer.Frames.Count;
        await peer.BarrierAsync();
        var entry = peer.Frames.Skip(mark).SelectMany(Entries)
            .Last(value => value[0].GetString() == "actionRejected");
        Assert.Equal("handResultAck", entry[2].GetProperty("action").GetString());
        Assert.Equal(reason, entry[2].GetProperty("reason").GetString());
    }

    private static int[] Seats(JsonElement continuation, string name) =>
        continuation.GetProperty(name).EnumerateArray().Select(value => value.GetInt32()).ToArray();

    private static JsonElement Entry(JsonElement snapshot, string kind) =>
        Entries(snapshot).Last(entry => entry[0].GetString() == kind && entry[1].GetString() == "current")[2].Clone();

    private static IEnumerable<JsonElement> Entries(JsonElement frame) =>
        frame.TryGetProperty("entries", out var entries) ? entries.EnumerateArray().ToArray() : [];
}
