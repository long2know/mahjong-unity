using System.Net.WebSockets;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using static Mahjong.Autotable.Api.Tests.TestInfrastructure.LobbyRepairFixture;

namespace Mahjong.Autotable.Api.Tests.Autotable;

public sealed class ExistingRoomJoinTests
{
    [Fact, Trait("Category", "LobbyRepair")]
    public async Task AliasCreationAndConcurrentAdmissionCannotStealTheCreatorsChosenChair()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var guests = new[] { await host.PlayerAsync(), await host.PlayerAsync() };
        var alias = $"creation-race-{Guid.NewGuid():N}";
        var creator = await host.SocketAsync(owner, alias, "seat=2&botCount=0&dealMode=manual", command: "NEW");
        await creator.WaitAsync(frame => Type(frame) == "JOINED");
        var arrivals = Task.WhenAll(guests.Select(guest => host.SocketAsync(guest, alias, "join=1")));
        await Task.WhenAll(arrivals, creator.UpdateAsync(["seats", owner.Id, new { seat = 2 }]));
        var peers = await arrivals;
        await UntilAsync(() => host.Manager.GetRuntimeGameIdBoundTo(alias) is not null,
            "Creator seat admission must establish one binding.");
        var runtimeId = host.Manager.GetRuntimeGameIdBoundTo(alias)!;
        var accepted = 0;
        var acceptedIds = new List<string> { owner.Id };
        for (var index = 0; index < peers.Length; index++)
        {
            var response = await peers[index].WaitAsync(frame => Type(frame) == "JOINED"
                || Entries(frame).Any(entry => entry[0].GetString() == "actionRejected"));
            if (Type(response) == "JOINED")
            {
                accepted++;
                acceptedIds.Add(guests[index].Id);
                Assert.Equal(alias, response.GetProperty("gameId").GetString());
                var seat = host.Runtime.TryGetSeatForPlayer(runtimeId, guests[index].Id);
                Assert.NotNull(seat);
                Assert.NotEqual(2, seat);
            }
            else
            {
                // A request preceding publication may fail closed, but must not mint another room.
                var rejection = Assert.Single(Entries(response), entry => entry[0].GetString() == "actionRejected");
                Assert.Equal("join", rejection[2].GetProperty("action").GetString());
                Assert.Equal("room-not-found", rejection[2].GetProperty("reason").GetString());
                await UntilAsync(() => peers[index].CloseStatus.HasValue, "A pre-publication rejection must close.");
                Assert.Equal(WebSocketCloseStatus.PolicyViolation, peers[index].CloseStatus);
            }
        }
        Assert.Equal(2, host.Runtime.TryGetSeatForPlayer(runtimeId, owner.Id));
        Assert.True(host.Runtime.TryGetSnapshot(runtimeId, out var state));
        Assert.Equal(accepted + 1, AssignedHumanIds(state!).Count());
        AssertHumanIdentitySet(state!, acceptedIds);
        Assert.Empty(state.Seats.Where(seat => seat.IsBot));
        Assert.Equal(1, host.Runtime.GameCount);
        Assert.Equal(new[] { runtimeId }, await host.StoredRuntimeIdsAsync());
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task ConcurrentJoinOnlyHumansReceiveDistinctSeatsInTheOriginalRuntime()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0, seat: 2);
        var newcomers = new[] { await host.PlayerAsync(), await host.PlayerAsync(), await host.PlayerAsync() };
        var peers = await Task.WhenAll(newcomers.Select(player => host.JoinAsync(player, room,
            "&seat=2&botCount=3&dealMode=auto&seed=11&handCount=16&baseUnit=99")));
        var state = await host.StateAsync(room);
        Assert.Equal(room.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(room.Alias));
        Assert.Equal(1, host.Runtime.GameCount);
        Assert.Equal(2, host.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        Assert.Equal(new[] { 0, 1, 3 }, newcomers.Select(player =>
            host.Runtime.TryGetSeatForPlayer(room.RuntimeId, player.Id)!.Value).OrderBy(seat => seat));
        Assert.All(state.Seats, seat => Assert.False(seat.IsBot));
        Assert.Equal(20260917, state.Seed);
        Assert.Equal(7, state.BaseUnit);
        Assert.Equal(4, state.MaxHands);
        Assert.Equal(DealMode.Manual, state.DealMode);
        foreach (var peer in peers)
        {
            var frames = peer.Frames.ToArray();
            var joined = Array.FindIndex(frames, frame => Type(frame) == "JOINED");
            Assert.True(joined >= 0);
            Assert.Equal(room.Alias, frames[joined].GetProperty("gameId").GetString());
            Assert.Contains(frames.Skip(joined + 1), IsFull);
        }
    }

    [Theory, Trait("Category", "LobbyRepair")]
    [InlineData("room-not-found")]
    [InlineData("room-full")]
    [InlineData("room-not-seating")]
    public async Task RejectedJoinIsExplicitPolicyCloseAndNeverCreatesASubstitute(string reason)
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, reason == "room-not-seating" ? 3 : 0);
        var alias = reason == "room-not-found" ? $"missing-{Guid.NewGuid():N}" : room.Alias;
        if (reason == "room-full")
        {
            room = await host.RestoreWithReservedSeatsAsync(room, 1, 2, 3);
        }
        if (reason == "room-not-seating")
            await UntilAsync(() => host.Runtime.TryGetSnapshot(room.RuntimeId, out var started)
                && started!.Phase != ChangshaPhase.Seating, "Precondition: room must have started.");
        var rows = await host.StoredRuntimeIdsAsync();
        var count = host.Runtime.GameCount;
        var newcomer = await host.PlayerAsync();
        var peer = await host.SocketAsync(newcomer, alias, "join=1");
        var rejected = await peer.WaitAsync(frame => Entries(frame).Any(entry => entry[0].GetString() == "actionRejected"));
        var entry = Assert.Single(Entries(rejected), item => item[0].GetString() == "actionRejected");
        Assert.Equal("current", entry[1].GetString());
        Assert.Equal(new[] { "action", "reason" }, entry[2].EnumerateObject().Select(p => p.Name).OrderBy(name => name));
        Assert.Equal("join", entry[2].GetProperty("action").GetString());
        Assert.Equal(reason, entry[2].GetProperty("reason").GetString());
        await UntilAsync(() => peer.CloseStatus.HasValue, "Rejected join must close the socket.");
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, peer.CloseStatus);
        Assert.DoesNotContain(peer.Frames, frame => Type(frame) == "JOINED");
        Assert.Equal(count, host.Runtime.GameCount);
        Assert.Equal(rows, await host.StoredRuntimeIdsAsync());
        if (reason == "room-not-found") Assert.Null(host.Manager.GetRuntimeGameIdBoundTo(alias));
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task JoinOnlyNewCommandCannotCreateARoom()
    {
        await using var host = new LobbyRepairFixture();
        var player = await host.PlayerAsync();
        var alias = $"never-created-{Guid.NewGuid():N}";
        var before = await host.StoredRuntimeIdsAsync();
        var peer = await host.SocketAsync(player, alias, "join=1&seat=0&botCount=3", command: "NEW");
        await peer.WaitAsync(frame => Entries(frame).Any(entry => entry[0].GetString() == "actionRejected"));
        await UntilAsync(() => peer.CloseStatus.HasValue, "join=1 NEW must fail closed.");
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, peer.CloseStatus);
        Assert.Equal(0, host.Runtime.GameCount);
        Assert.Equal(before, await host.StoredRuntimeIdsAsync());
        Assert.Null(host.Manager.GetRuntimeGameIdBoundTo(alias));
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task ExistingRuntimeGuidJoinsTheCanonicalAliasWithoutCreatingAnotherBinding()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var guest = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var rows = await host.StoredRuntimeIdsAsync();
        var peer = await host.SocketAsync(guest, room.RuntimeId.ToUpperInvariant(), "join=1");
        var joined = await peer.WaitAsync(frame => Type(frame) == "JOINED");
        Assert.Equal(room.Alias, joined.GetProperty("gameId").GetString());
        Assert.NotNull(host.Runtime.TryGetSeatForPlayer(room.RuntimeId, guest.Id));
        Assert.Equal(room.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(room.Alias));
        Assert.Null(host.Manager.GetRuntimeGameIdBoundTo(room.RuntimeId));
        Assert.Equal(rows, await host.StoredRuntimeIdsAsync());
        Assert.Equal(1, host.Runtime.GameCount);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task DuplicateIdentityCannotTakeASecondSeatOrReadTheActiveOwnersHand()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 3, dealMode: "auto");
        await UntilAsync(() => host.Runtime.TryGetSnapshot(room.RuntimeId, out var state)
            && state!.Phase == ChangshaPhase.AwaitingDiscard, "Precondition: the real automatic deal must complete.");
        var before = await host.StateAsync(room);
        Assert.Equal(14, before.Hands[0].ConcealedTiles.Count);
        var duplicate = await host.JoinAsync(owner, room);
        var full = await duplicate.WaitAsync(IsFull);
        var handEntries = Entries(full).Where(entry => entry[0].GetString() == "things"
            && entry[2].ValueKind == JsonValueKind.Object
            && entry[2].TryGetProperty("slotName", out var slot)
            && slot.GetString()!.StartsWith("hand.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(handEntries);
        var privateIds = before.Hands.SelectMany(hand => hand.ConcealedTiles).Select(tile => tile.ToString()).ToHashSet();
        Assert.All(handEntries, entry =>
        {
            Assert.Equal(JsonValueKind.String, entry[1].ValueKind);
            Assert.DoesNotContain(entry[1].GetString(), privateIds);
        });
        var tile = before.Hands[0].ConcealedTiles[0];
        await duplicate.UpdateAsync(["discard", 0, new { tileId = tile }]);
        var rejection = await duplicate.WaitAsync(frame => Entries(frame).Any(entry => entry[0].GetString() == "actionRejected"));
        Assert.Equal("connection-owns-no-seat",
            Assert.Single(Entries(rejection), entry => entry[0].GetString() == "actionRejected")[2].GetProperty("reason").GetString());
        var after = await host.StateAsync(room);
        Assert.Equal(before.StateVersion, after.StateVersion);
        Assert.Single(after.Seats, seat => seat.PlayerId == owner.Id && !seat.IsBot);
        Assert.Contains(tile, after.Hands[0].ConcealedTiles);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task DisconnectedOwnerResumesStartedRoomWithoutASecondSeatOrNewRuntime()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 3);
        await UntilAsync(() => host.Runtime.TryGetSnapshot(room.RuntimeId, out var ready)
            && ready!.Phase == ChangshaPhase.RollingDice, "Precondition: manual room must start.");
        var before = await host.StateAsync(room);
        await room.Creator.DisposeAsync();
        _ = await host.JoinAsync(owner, room, "&botCount=0&dealMode=auto");
        var after = await host.StateAsync(room);
        Assert.Equal(room.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(room.Alias));
        Assert.Equal(0, host.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        Assert.Single(after.Seats, seat => seat.PlayerId == owner.Id && !seat.IsBot);
        Assert.Equal(before.DealMode, after.DealMode);
        Assert.Equal(3, after.Seats.Count(seat => seat.IsBot));
        Assert.Equal(ChangshaPhase.RollingDice, after.Phase);
        Assert.Equal(1, host.Runtime.GameCount);
    }
}
