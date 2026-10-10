using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Autotable.Api.Tests.Matchmaking;

public sealed class RoomMetadataAliasTests
{
    [Fact, Trait("Category", "LobbyRepair")]
    public async Task AliasRuntimeAndSettingsReadsAgreeOnMinimalSignedGuestMetadata()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var other = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 1);
        var alias = await host.MetadataAsync(owner, room.Alias);
        var runtime = await host.MetadataAsync(owner, room.RuntimeId);
        var normalizedRuntime = await host.MetadataAsync(owner, room.RuntimeId.ToUpperInvariant());
        var compatibility = await host.MetadataAsync(owner, room.Alias, "/settings");
        Assert.Equal(MetadataValues(alias), MetadataValues(runtime));
        Assert.Equal(MetadataValues(alias), MetadataValues(normalizedRuntime));
        Assert.Equal(MetadataValues(alias), MetadataValues(compatibility));
        Assert.Equal(new[] {
            "botCount", "canInvite", "canMakePublic", "gameId", "isPublic", "openHumanSeats", "ownerId",
            "phase", "publicName", "seatedCount", "viewerCanManageVoice", "viewerIsOwner", "voiceEnabled",
        }, alias.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal(room.Alias, alias.GetProperty("gameId").GetString());
        Assert.NotEqual(room.RuntimeId, alias.GetProperty("gameId").GetString());
        Assert.Equal(owner.Id, alias.GetProperty("ownerId").GetString());
        Assert.True(alias.GetProperty("viewerIsOwner").GetBoolean());
        Assert.True(alias.GetProperty("canMakePublic").GetBoolean());
        Assert.True(alias.GetProperty("canInvite").GetBoolean());
        Assert.False(alias.GetProperty("viewerCanManageVoice").GetBoolean());
        Assert.Equal("Seating", alias.GetProperty("phase").GetString());
        Assert.Equal(1, alias.GetProperty("botCount").GetInt32());
        Assert.Equal(1, alias.GetProperty("seatedCount").GetInt32());
        Assert.Equal(2, alias.GetProperty("openHumanSeats").GetInt32());
        var outsider = await host.MetadataAsync(other, room.Alias);
        Assert.False(outsider.GetProperty("viewerIsOwner").GetBoolean());
        Assert.False(outsider.GetProperty("canMakePublic").GetBoolean());
        Assert.False(outsider.GetProperty("canInvite").GetBoolean());
        using var voice = await owner.Http.PostAsJsonAsync($"/api/games/{room.Alias}/settings/voice", new { enabled = true });
        Assert.Equal(HttpStatusCode.Unauthorized, voice.StatusCode);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task MetadataFailureNeverCreatesRooms_AndUnsignedCallerCannotRead()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var whitespaceOwner = await host.PlayerAsync();
        var whitespaceRoom = await host.CreateRoomAsync(whitespaceOwner, 0, roomId: $"hudson room-{Guid.NewGuid():N}");
        var whitespaceMetadata = await host.MetadataAsync(whitespaceOwner, whitespaceRoom.Alias);
        Assert.Equal(whitespaceRoom.Alias, whitespaceMetadata.GetProperty("gameId").GetString());
        Assert.True(whitespaceMetadata.GetProperty("viewerIsOwner").GetBoolean());
        Assert.Equal(0, host.Runtime.TryGetSeatForPlayer(whitespaceRoom.RuntimeId, whitespaceOwner.Id));
        var before = await host.StoredRuntimeIdsAsync();
        using var anonymous = await host.Http().GetAsync($"/api/games/{room.Alias}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var invalid = await owner.Http.GetAsync("/api/games/" + new string('x', 65));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await owner.Http.GetAsync("/api/games/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingSettings = await owner.Http.GetAsync("/api/games/does-not-exist/settings");
        Assert.Equal(HttpStatusCode.NotFound, missingSettings.StatusCode);
        using var wrongAliasCase = await owner.Http.GetAsync($"/api/games/{room.Alias.ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.NotFound, wrongAliasCase.StatusCode);
        Assert.Equal(before, await host.StoredRuntimeIdsAsync());
        Assert.Equal(2, host.Runtime.GameCount);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task PublicMutationNormalizesAliasMetadata_AndEnforcesCreatorAndPhase()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var visitor = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 1);
        var ownerHub = await host.HubAsync(owner);
        var visitorHub = await host.HubAsync(visitor);
        Assert.Empty(await host.LobbyAsync(visitor));
        var published = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, "  Shared table  ");
        Assert.True(published.GetProperty("success").GetBoolean());
        Assert.Equal(room.Alias, published.GetProperty("gameId").GetString());
        Assert.Equal("Shared table", published.GetProperty("publicName").GetString());
        Assert.True(published.GetProperty("isPublic").GetBoolean());
        var listed = Assert.Single(await host.LobbyAsync(visitor));
        Assert.Equal(room.Alias, listed.GetProperty("gameId").GetString());
        Assert.Equal(1, listed.GetProperty("seatedCount").GetInt32());
        Assert.Equal(1, listed.GetProperty("botCount").GetInt32());
        Assert.Equal(2, listed.GetProperty("openHumanSeats").GetInt32());
        await Assert.ThrowsAsync<HubException>(() => visitorHub.InvokeAsync("SetGamePublic", room.Alias, false, null));
        await Assert.ThrowsAsync<HubException>(() => ownerHub.InvokeAsync("SetGamePublic", "unknown-room", true, null));
        Assert.True((await host.MetadataAsync(owner, room.Alias)).GetProperty("isPublic").GetBoolean());
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.RuntimeId, false, null);
        Assert.Empty(await host.LobbyAsync(visitor));
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, "Started table");
        _ = await host.JoinAsync(visitor, room);
        _ = await host.JoinAsync(await host.PlayerAsync(), room);
        await LobbyRepairFixture.UntilAsync(() => host.Runtime.TryGetSnapshot(room.RuntimeId, out var state)
            && state!.Phase != ChangshaPhase.Seating, "Precondition: all human places must be filled.");
        Assert.False((await host.MetadataAsync(owner, room.Alias)).GetProperty("canMakePublic").GetBoolean());
        await Assert.ThrowsAsync<HubException>(() => ownerHub.InvokeAsync("SetGamePublic", room.Alias, false, null));
        Assert.Empty(await host.LobbyAsync(visitor));
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task PublicAvailabilityExcludesReservedHumansAndBots_AndRandomSelectionDoesNotSeat()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var visitor = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 1);
        room = await host.RestoreWithReservedSeatsAsync(room, 2);
        owner = room.Owner;
        visitor = host.ReopenHttp(visitor);
        var ownerHub = await host.HubAsync(owner);
        var visitorHub = await host.HubAsync(visitor);
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, null);
        var listed = Assert.Single(await host.LobbyAsync(visitor));
        Assert.Equal(1, listed.GetProperty("botCount").GetInt32());
        Assert.Equal(1, listed.GetProperty("seatedCount").GetInt32());
        Assert.Equal(1, listed.GetProperty("openHumanSeats").GetInt32());
        var found = await visitorHub.InvokeAsync("FindJoinableGame", "changsha");
        Assert.True(found.GetProperty("matched").GetBoolean());
        Assert.Equal(room.Alias, found.GetProperty("gameId").GetString());
        Assert.Null(host.Runtime.TryGetSeatForPlayer(room.RuntimeId, visitor.Id));
        var legacy = await visitorHub.InvokeAsync("JoinRandom", "changsha");
        Assert.True(legacy.GetProperty("matched").GetBoolean());
        Assert.Equal(room.Alias, legacy.GetProperty("gameId").GetString());
        Assert.Equal(3, legacy.GetProperty("seatIndex").GetInt32());
        Assert.Equal(3, host.Runtime.TryGetSeatForPlayer(room.RuntimeId, visitor.Id));
        Assert.Equal(ChangshaPhase.Seating, (await host.StateAsync(room)).Phase);
        Assert.Empty(await host.LobbyAsync(owner));
        Assert.False((await ownerHub.InvokeAsync("FindJoinableGame", "changsha")).GetProperty("matched").GetBoolean());
    }

    private static IEnumerable<string> MetadataValues(JsonElement value) =>
        value.EnumerateObject().OrderBy(property => property.Name)
            .Select(property => property.Name + ":" + property.Value.GetRawText());
}
