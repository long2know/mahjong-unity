using System.Text.Json;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.SignalR;
using static Mahjong.Autotable.Api.Tests.TestInfrastructure.LobbyRepairFixture;

namespace Mahjong.Autotable.Api.Tests.Players;

public sealed class LobbyPresenceTests
{
    [Fact, Trait("Category", "LobbyRepair")]
    public async Task DifferentRoomsAndLobbyOnlyPlayersShareAPrivateDeduplicatedRoster()
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var bob = await host.PlayerAsync();
        var lobbyOnly = await host.PlayerAsync();
        var offlineProfile = await host.PlayerAsync();
        Assert.Equal(3, new[] { alice.Id, bob.Id, lobbyOnly.Id }.Distinct().Count());
        var aliceRoom = await host.CreateRoomAsync(alice, 2);
        var bobRoom = await host.CreateRoomAsync(bob, 0);
        var aliceHub = await host.HubAsync(alice);
        var initial = await aliceHub.LobbyAsync();
        var bobHub = await host.HubAsync(bob);
        var lobbyHub = await host.HubAsync(lobbyOnly);
        var expected = new[] { alice.Id, bob.Id, lobbyOnly.Id }.OrderBy(id => id).ToArray();
        foreach (var peer in new[] { aliceHub, bobHub, lobbyHub })
        {
            var roster = await peer.LobbyAsync();
            Assert.Equal(expected, Players(roster).Select(player => player.GetProperty("playerId").GetString()).OrderBy(id => id));
            Assert.DoesNotContain(Players(roster), player => player.GetProperty("playerId").GetString() == offlineProfile.Id);
            Assert.True(roster.GetProperty("revision").GetInt64() > initial.GetProperty("revision").GetInt64());
            Assert.All(Players(roster), player =>
            {
                Assert.Equal(new[] { "avatarColor", "canReceiveInvites", "displayName", "playerId" },
                    player.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
                Assert.True(player.GetProperty("canReceiveInvites").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(player.GetProperty("displayName").GetString()));
                Assert.DoesNotContain("bot", player.GetProperty("playerId").GetString()!, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Empty(roster.GetProperty("invites").EnumerateArray());
        }
        Assert.Null(host.Runtime.TryGetSeatForPlayer(aliceRoom.RuntimeId, lobbyOnly.Id));
        Assert.Null(host.Runtime.TryGetSeatForPlayer(bobRoom.RuntimeId, lobbyOnly.Id));
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task MultipleTabsAndWsPlusHubDeduplicate_UntilTheLastRealTransportCloses()
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var observer = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(alice, 0);
        var observerHub = await host.HubAsync(observer);
        var rawOnly = Assert.Single(Players(await observerHub.LobbyAsync()),
            player => player.GetProperty("playerId").GetString() == alice.Id);
        Assert.False(rawOnly.GetProperty("canReceiveInvites").GetBoolean());
        var tabOne = await host.HubAsync(alice);
        var tabTwo = await host.HubAsync(alice);
        Assert.True(Assert.Single(Players(await observerHub.LobbyAsync()),
            player => player.GetProperty("playerId").GetString() == alice.Id).GetProperty("canReceiveInvites").GetBoolean());
        await tabOne.DisposeAsync();
        Assert.True(Assert.Single(Players(await observerHub.LobbyAsync()),
            player => player.GetProperty("playerId").GetString() == alice.Id).GetProperty("canReceiveInvites").GetBoolean());
        var beforeHubClosure = (await observerHub.LobbyAsync()).GetProperty("revision").GetInt64();
        await tabTwo.DisposeAsync();
        await UntilAsync(() => observerHub.Rosters.Any(roster =>
            roster.GetProperty("revision").GetInt64() > beforeHubClosure
            && Players(roster).Any(player => player.GetProperty("playerId").GetString() == alice.Id
                && !player.GetProperty("canReceiveInvites").GetBoolean())),
            "A WS-only player must remain online without falsely advertising invitation delivery.");
        var stillOnline = Assert.Single(Players(await observerHub.LobbyAsync()),
            player => player.GetProperty("playerId").GetString() == alice.Id);
        Assert.False(stillOnline.GetProperty("canReceiveInvites").GetBoolean());
        var revision = (await observerHub.LobbyAsync()).GetProperty("revision").GetInt64();
        await room.Creator.DisposeAsync();
        await UntilAsync(() => observerHub.Rosters.Any(roster => roster.GetProperty("revision").GetInt64() > revision
            && Players(roster).All(player => player.GetProperty("playerId").GetString() != alice.Id)),
            "Closing the final normal transport must remove the player within five seconds.");
        Assert.DoesNotContain(Players(await observerHub.LobbyAsync()),
            player => player.GetProperty("playerId").GetString() == alice.Id);
        var reconnect = await host.HubAsync(alice);
        Assert.Single(Players(await reconnect.LobbyAsync()),
            player => player.GetProperty("playerId").GetString() == alice.Id);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task MetadataHubDisconnectCannotTransferOrDestroyAnActiveWsOwnersPublicRoom()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var other = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        _ = await host.JoinAsync(other, room);
        var metadataHub = await host.HubAsync(owner);
        var observerHub = await host.HubAsync(other);
        _ = await metadataHub.InvokeAsync("SetGamePublic", room.Alias, true, "Owner remains connected");
        var before = await host.StateAsync(room);
        var rows = await host.StoredRuntimeIdsAsync();
        var revision = (await observerHub.LobbyAsync()).GetProperty("revision").GetInt64();
        await metadataHub.DisposeAsync();
        await UntilAsync(() => observerHub.Rosters.Any(roster => roster.GetProperty("revision").GetInt64() > revision
            && Players(roster).Any(player => player.GetProperty("playerId").GetString() == owner.Id
                && !player.GetProperty("canReceiveInvites").GetBoolean())), "Metadata transport closure was not observed.");
        var after = await host.StateAsync(room);
        Assert.Equal(before.CreatorPlayerId, after.CreatorPlayerId);
        Assert.Equal(owner.Id, after.CreatorPlayerId);
        Assert.True(after.IsPublic);
        Assert.Equal(0, host.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        Assert.Equal(room.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(room.Alias));
        Assert.Equal(rows, await host.StoredRuntimeIdsAsync());
        var metadata = await host.MetadataAsync(owner, room.Alias);
        Assert.True(metadata.GetProperty("viewerIsOwner").GetBoolean());
        Assert.True(metadata.GetProperty("canMakePublic").GetBoolean());
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task UnverifiedHubIdentityIsRejectedAndProfileChangesRepublishVerifiedNames()
    {
        await using var host = new LobbyRepairFixture();
        var observer = await host.PlayerAsync();
        var observerHub = await host.HubAsync(observer);
        var unsigned = await host.HubAsync(null, joinLobby: false);
        var rejection = await Assert.ThrowsAsync<HubException>(() => unsigned.LobbyAsync());
        Assert.Contains("identity-required", rejection.Message);
        Assert.Single(Players(await observerHub.LobbyAsync()));
        var alice = await host.PlayerAsync();
        var aliceHub = await host.HubAsync(alice);
        var revision = (await observerHub.LobbyAsync()).GetProperty("revision").GetInt64();
        _ = await aliceHub.InvokeAsync("UpdateProfile", "Roster Renamed", "#112233");
        await UntilAsync(() => observerHub.Rosters.Any(roster => roster.GetProperty("revision").GetInt64() > revision
            && Players(roster).Any(player => player.GetProperty("playerId").GetString() == alice.Id
                && player.GetProperty("displayName").GetString() == "Roster Renamed")),
            "A verified profile update must republish the roster.");
        var row = Assert.Single(Players(await observerHub.LobbyAsync()),
            player => player.GetProperty("playerId").GetString() == alice.Id);
        Assert.Equal("#112233", row.GetProperty("avatarColor").GetString());
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task RestartDoesNotConvertPersistedProfilesOrRoomOwnersIntoOnlinePresence()
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var bob = await host.PlayerAsync();
        _ = await host.CreateRoomAsync(alice, 0);
        _ = await host.HubAsync(alice);
        var bobHub = await host.HubAsync(bob);
        Assert.Equal(2, Players(await bobHub.LobbyAsync()).Length);
        await host.RestartAsync();
        var observer = await host.PlayerAsync();
        var observerHub = await host.HubAsync(observer);
        var onlyOnline = Assert.Single(Players(await observerHub.LobbyAsync()));
        Assert.Equal(observer.Id, onlyOnline.GetProperty("playerId").GetString());
        Assert.NotEqual(alice.Id, observer.Id);
        Assert.NotEqual(bob.Id, observer.Id);
    }

    private static JsonElement[] Players(JsonElement roster) => roster.GetProperty("players").EnumerateArray().ToArray();
}
