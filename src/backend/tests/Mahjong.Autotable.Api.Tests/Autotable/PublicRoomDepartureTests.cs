using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Players;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

[Trait("Regression", "R1")]
public sealed class PublicRoomDepartureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerDeparture_TransfersToLowestLiveHuman_AndSuccessorCanUnpublish(bool explicitLeave)
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, botCount: 0, seat: 2);
        var ownerConnection = Connection(fixture, owner.Id);
        var higher = await fixture.PlayerAsync();
        _ = await SeatAsync(fixture, room, higher, 3);
        var lower = await fixture.PlayerAsync();
        var lowerSocket = await SeatAsync(fixture, room, lower, 1);
        var ownerHub = await fixture.HubAsync(owner);
        Assert.True((await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, "R1 succession"))
            .GetProperty("success").GetBoolean());

        var before = await fixture.StateAsync(room);
        Assert.Equal(ChangshaPhase.Seating, before.Phase);
        Assert.Equal(owner.Id, before.CreatorPlayerId);
        Assert.Equal(new[] { "human-0", lower.Id, owner.Id, higher.Id },
            before.Seats.Select(seat => seat.PlayerId));
        Assert.All(before.Seats, seat => Assert.False(seat.IsBot));
        Assert.Equal(3, Assert.Single(await fixture.LobbyAsync(owner)).GetProperty("seatedCount").GetInt32());

        ChangshaGameState? afterRelease = null;
        if (explicitLeave)
        {
            await room.Creator.UpdateAsync(["seats", owner.Id, new { seat = (int?)null }]);
            await FenceAsync(room.Creator, lowerSocket);
            afterRelease = await fixture.StateAsync(room);
        }
        await DisconnectAsync(fixture, room, owner, room.Creator);

        var after = await fixture.StateAsync(room);
        Assert.Null(fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, ownerConnection.Id.ToString("N")));
        Assert.Equal(lower.Id, after.CreatorPlayerId);
        if (explicitLeave)
        {
            Assert.Equal(lower.Id, Assert.IsType<ChangshaGameState>(afterRelease).CreatorPlayerId);
            Assert.Null(fixture.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        }
        else
        {
            Assert.Equal(owner.Id, after.Seats[2].PlayerId);
        }
        Assert.Equal(lower.Id, (await fixture.StoredStateAsync(room)).CreatorPlayerId);
        var listed = Assert.Single(await fixture.LobbyAsync(lower));
        Assert.Equal(room.Alias, listed.GetProperty("gameId").GetString());
        Assert.Equal(2, listed.GetProperty("seatedCount").GetInt32());
        Assert.Equal(2, listed.GetProperty("openHumanSeats").GetInt32());
        await Assert.ThrowsAsync<HubException>(() =>
            ownerHub.InvokeAsync("SetGamePublic", room.Alias, false, null));

        var successorHub = await fixture.HubAsync(lower);
        var unpublished = await successorHub.InvokeAsync("SetGamePublic", room.Alias, false, null);
        Assert.True(unpublished.GetProperty("success").GetBoolean());
        Assert.False(unpublished.GetProperty("isPublic").GetBoolean());
        Assert.Empty(await fixture.LobbyAsync(lower));
        Assert.False((await fixture.StoredStateAsync(room)).IsPublic);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SoleOwnerDeparture_RemovesDiscoveryAndPersistsTerminalState(bool explicitLeave)
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, botCount: 0, seat: 3);
        var ownerConnection = Connection(fixture, owner.Id);
        var observer = await fixture.PlayerAsync();
        var observerSocket = await fixture.ObserveAsync(observer, room);
        var ownerHub = await fixture.HubAsync(owner);
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, "R1 empty room");
        Assert.Equal(3, fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, ownerConnection.Id.ToString("N")));
        Assert.Null(fixture.Runtime.TryGetSeatForPlayer(room.RuntimeId, observer.Id));
        Assert.Equal(ChangshaPhase.Seating, (await fixture.StateAsync(room)).Phase);
        Assert.Equal(1, Assert.Single(await fixture.LobbyAsync(owner)).GetProperty("seatedCount").GetInt32());

        var removedAtRelease = false;
        if (explicitLeave)
        {
            var ownerRevision = ownerConnection.Authority.Revision;
            var observerRevision = Connection(fixture, observer.Id).Authority.Revision;
            await room.Creator.UpdateAsync(["seats", owner.Id, new { seat = (int?)null }]);
            var ownerTerminal = await room.Creator.WaitAsync(frame =>
                LobbyRepairFixture.Type(frame) == "UPDATE"
                && frame.GetProperty("viewer").GetProperty("roomId").ValueKind == JsonValueKind.Null);
            ViewerAuthorityAssertions.Expect(ownerTerminal, null, null, ownerRevision + 1);
            var observerTerminal = await observerSocket.WaitAsync(frame =>
                LobbyRepairFixture.Type(frame) == "UPDATE"
                && frame.GetProperty("viewer").GetProperty("roomId").ValueKind == JsonValueKind.Null);
            ViewerAuthorityAssertions.Expect(observerTerminal, null, null, observerRevision + 1);
            removedAtRelease = !fixture.Runtime.TryGetSnapshot(room.RuntimeId, out _);
            var storedAtRelease = await fixture.StoredStateAsync(room);
            Assert.True(storedAtRelease.IsGameComplete);
            Assert.Equal(ChangshaPhase.GameComplete, storedAtRelease.Phase);
        }
        await DisconnectAsync(fixture, room, owner, room.Creator);
        await DisconnectAsync(fixture, room, observer, observerSocket);

        Assert.False(fixture.Runtime.TryGetSnapshot(room.RuntimeId, out _));
        if (explicitLeave) Assert.True(removedAtRelease, "The explicit leave must complete removal before disconnect.");
        Assert.Null(fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, ownerConnection.Id.ToString("N")));
        Assert.Empty(await fixture.LobbyAsync(owner));
        var stored = await fixture.StoredStateAsync(room);
        Assert.True(stored.IsGameComplete);
        Assert.Equal(ChangshaPhase.GameComplete, stored.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataHubLeaveAndDisconnect_CannotReleaseOrSucceedTheLiveOwner(bool anotherHuman)
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, botCount: 0, seat: 2);
        var ownerConnection = Connection(fixture, owner.Id);
        if (anotherHuman) _ = await SeatAsync(fixture, room, await fixture.PlayerAsync(), 0);
        var metadataHub = await fixture.HubAsync(owner);
        _ = await metadataHub.InvokeAsync("SetGamePublic", room.Alias, true, "R1 metadata observer");
        var hubConnectionId = Assert.IsType<string>(metadataHub.Connection.ConnectionId);
        Assert.Null(fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, hubConnectionId));
        var before = await fixture.StateAsync(room);

        await fixture.Runtime.ReleaseSeatAsync(room.RuntimeId, owner.Id, hubConnectionId);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.StateAsync(room)));
        await metadataHub.DisposeAsync();
        var presence = fixture.Services.GetRequiredService<LobbyPresenceService>();
        await LobbyRepairFixture.UntilAsync(() => !presence.CanReceiveInvites(owner.Id),
            "The metadata hub's actual disconnect callback did not finish.");

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.StateAsync(room)));
        Assert.Equal(2, fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, ownerConnection.Id.ToString("N")));
        Assert.Equal(owner.Id, (await fixture.StoredStateAsync(room)).CreatorPlayerId);
        Assert.Equal(anotherHuman ? 2 : 1,
            Assert.Single(await fixture.LobbyAsync(owner)).GetProperty("seatedCount").GetInt32());
        var activeOwnerHub = await fixture.HubAsync(owner);
        Assert.True((await activeOwnerHub.InvokeAsync("SetGamePublic", room.Alias, false, null))
            .GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateOwnerTabLeaveAndDisconnect_CannotReleaseOrSucceedTheLiveOwner(bool anotherHuman)
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, botCount: 0, seat: 2);
        var ownerConnection = Connection(fixture, owner.Id);
        if (anotherHuman) _ = await SeatAsync(fixture, room, await fixture.PlayerAsync(), 0);
        var ownerHub = await fixture.HubAsync(owner);
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, "R1 duplicate observer");
        var duplicate = await fixture.SocketAsync(owner, room.Alias, "seat=2&botCount=0");
        var joined = await duplicate.WaitAsync(frame => LobbyRepairFixture.Type(frame) == "JOINED");
        Assert.Equal(owner.Id, joined.GetProperty("playerId").GetString());
        await duplicate.WaitAsync(LobbyRepairFixture.IsFull);
        var duplicateConnection = Connection(fixture, owner.Id, except: ownerConnection.Id);
        Assert.Null(duplicateConnection.ViewerSeat);
        Assert.Null(fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, duplicateConnection.Id.ToString("N")));
        var before = await fixture.StateAsync(room);

        await duplicate.UpdateAsync(["seats", owner.Id, new { seat = (int?)null }]);
        await FenceAsync(duplicate, room.Creator);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.StateAsync(room)));
        await duplicate.DisposeAsync();
        // Await an idempotent replay with the real observer's transport ID, not an invented seat owner.
        await fixture.Runtime.HandleDisconnectAsync(owner.Id, duplicateConnection.Id.ToString("N"));

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.StateAsync(room)));
        Assert.Equal(2, fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, ownerConnection.Id.ToString("N")));
        Assert.Equal(owner.Id, (await fixture.StoredStateAsync(room)).CreatorPlayerId);
        Assert.Equal(anotherHuman ? 2 : 1,
            Assert.Single(await fixture.LobbyAsync(owner)).GetProperty("seatedCount").GetInt32());
        Assert.True((await ownerHub.InvokeAsync("SetGamePublic", room.Alias, false, null))
            .GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task NonOwnerExplicitLeave_ReleasesOnlyItsOwnSeatWithoutChangingPublicOwner()
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, botCount: 0, seat: 3);
        var guest = await fixture.PlayerAsync();
        var guestSocket = await SeatAsync(fixture, room, guest, 0);
        var guestConnection = Connection(fixture, guest.Id);
        var ownerHub = await fixture.HubAsync(owner);
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, null);

        await guestSocket.UpdateAsync(["seats", guest.Id, new { seat = (int?)null }]);
        await FenceAsync(guestSocket, room.Creator);
        await DisconnectAsync(fixture, room, guest, guestSocket);

        Assert.Null(fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, guestConnection.Id.ToString("N")));
        Assert.Null(fixture.Runtime.TryGetSeatForPlayer(room.RuntimeId, guest.Id));
        Assert.Equal(owner.Id, (await fixture.StateAsync(room)).CreatorPlayerId);
        Assert.Equal(owner.Id, (await fixture.StoredStateAsync(room)).CreatorPlayerId);
        Assert.Equal(1, Assert.Single(await fixture.LobbyAsync(owner)).GetProperty("seatedCount").GetInt32());
    }

    [Fact]
    public async Task PrivateOwnerExplicitLeave_DoesNotTransferOrRemoveTheRoom()
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, botCount: 0, seat: 2);
        var guest = await fixture.PlayerAsync();
        var guestSocket = await SeatAsync(fixture, room, guest, 0);
        Assert.False((await fixture.StateAsync(room)).IsPublic);

        await room.Creator.UpdateAsync(["seats", owner.Id, new { seat = (int?)null }]);
        await FenceAsync(room.Creator, guestSocket);
        await DisconnectAsync(fixture, room, owner, room.Creator);

        Assert.Null(fixture.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        Assert.Equal(owner.Id, (await fixture.StateAsync(room)).CreatorPlayerId);
        Assert.Equal(owner.Id, (await fixture.StoredStateAsync(room)).CreatorPlayerId);
        Assert.Empty(await fixture.LobbyAsync(guest));
    }

    [Fact]
    public async Task PublicOwnerExplicitLeave_AfterSeatingDoesNotReleaseOrTransfer()
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, botCount: 0, seat: 1);
        var ownerConnection = Connection(fixture, owner.Id);
        var ownerHub = await fixture.HubAsync(owner);
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, null);
        var witness = await SeatAsync(fixture, room, await fixture.PlayerAsync(), 0);
        _ = await SeatAsync(fixture, room, await fixture.PlayerAsync(), 2);
        _ = await SeatAsync(fixture, room, await fixture.PlayerAsync(), 3);
        await LobbyRepairFixture.UntilAsync(() =>
            fixture.Runtime.TryGetSnapshot(room.RuntimeId, out var state)
            && state?.Phase == ChangshaPhase.RollingDice, "Four real humans must start the manual deal.");
        var before = await fixture.StateAsync(room);

        await room.Creator.UpdateAsync(["seats", owner.Id, new { seat = (int?)null }]);
        await FenceAsync(room.Creator, witness);

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.StateAsync(room)));
        Assert.Equal(1, fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, ownerConnection.Id.ToString("N")));
        Assert.Equal(owner.Id, (await fixture.StoredStateAsync(room)).CreatorPlayerId);
    }

    private static async Task<LobbyWsPeer> SeatAsync(
        LobbyRepairFixture fixture, LobbyRoom room, LobbyPlayer player, int seat)
    {
        var socket = await fixture.SocketAsync(player, room.Alias, $"seat={seat}&botCount=0");
        var joined = await socket.WaitAsync(frame => LobbyRepairFixture.Type(frame) == "JOINED");
        Assert.Equal(player.Id, joined.GetProperty("playerId").GetString());
        Assert.Equal(room.Alias, joined.GetProperty("gameId").GetString());
        await socket.UpdateAsync(["seats", player.Id, new { seat }]);
        var connection = Connection(fixture, player.Id);
        await LobbyRepairFixture.UntilAsync(() =>
            fixture.Runtime.TryGetSeatForConnection(room.RuntimeId, connection.Id.ToString("N")) == seat,
            "The signed human's real transport must acquire the requested seat.");
        Assert.Equal(seat, fixture.Runtime.TryGetSeatForPlayer(room.RuntimeId, player.Id));
        return socket;
    }

    private static AutotableConnection Connection(LobbyRepairFixture fixture, string playerId, Guid? except = null)
    {
        var field = typeof(AutotableConnectionManager).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var connections = Assert.IsType<ConcurrentDictionary<Guid, AutotableConnection>>(
            field.GetValue(fixture.Manager));
        return Assert.Single(connections.Values, connection => connection.PlayerId == playerId && connection.Id != except);
    }

    private static async Task FenceAsync(LobbyWsPeer sender, LobbyWsPeer witness)
    {
        var marker = Guid.NewGuid().ToString("N");
        await sender.UpdateAsync(["r1DepartureFence", marker, true]);
        await witness.WaitAsync(frame => LobbyRepairFixture.Entries(frame).Any(entry =>
            entry[0].GetString() == "r1DepartureFence" && entry[1].GetString() == marker));
    }

    private static async Task DisconnectAsync(
        LobbyRepairFixture fixture, LobbyRoom room, LobbyPlayer player, LobbyWsPeer socket)
    {
        await socket.DisposeAsync();
        var presence = fixture.Services.GetRequiredService<LobbyPresenceService>();
        await LobbyRepairFixture.UntilAsync(() => !presence.IsJoined(player.Id, room.RuntimeId),
            "Transport cleanup must finish, including public-room retirement, before asserting departure.");
    }
}
