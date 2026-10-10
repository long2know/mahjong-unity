using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.SignalR;
using static Mahjong.Autotable.Api.Tests.TestInfrastructure.LobbyRepairFixture;

namespace Mahjong.Autotable.Api.Tests.Autotable;

public sealed class RoomBotQuotaTests
{
    [Theory, Trait("Category", "LobbyRepair")]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public async Task ExplicitQuota_WaitsForExactlyTheRequiredHumans_AndExcludesCreatorSeat(int bots, int creatorSeat)
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, bots, creatorSeat);
        var expectedBots = Enumerable.Range(0, 4).Where(seat => seat != creatorSeat).Take(bots).ToArray();
        var persisted = await host.StoredStateAsync(room);
        Assert.Equal(expectedBots, persisted.Seats.Where(seat => seat.IsBot).Select(seat => seat.SeatIndex));
        Assert.Equal(20260917, persisted.Seed);
        Assert.Equal(7, persisted.BaseUnit);
        Assert.Equal(DealMode.Manual, persisted.DealMode);
        var identities = new HashSet<string> { owner.Id };
        for (var humanCount = 1; humanCount <= 4 - bots; humanCount++)
        {
            var state = await host.StateAsync(room);
            Assert.Equal(expectedBots, state.Seats.Where(seat => seat.IsBot).Select(seat => seat.SeatIndex));
            AssertHumanIdentitySet(state, identities);
            var metadata = await host.MetadataAsync(owner, room.Alias);
            Assert.Equal(bots, metadata.GetProperty("botCount").GetInt32());
            Assert.Equal(humanCount, metadata.GetProperty("seatedCount").GetInt32());
            Assert.Equal(4 - bots - humanCount, metadata.GetProperty("openHumanSeats").GetInt32());
            if (humanCount == 4 - bots)
            {
                await UntilAsync(() => host.Runtime.TryGetSnapshot(room.RuntimeId, out var ready)
                    && ready!.Phase != ChangshaPhase.Seating, "A fully occupied room did not start.");
                break;
            }
            Assert.Equal(ChangshaPhase.Seating, state.Phase);
            var human = await host.PlayerAsync();
            Assert.True(identities.Add(human.Id));
            _ = await host.JoinAsync(human, room);
            Assert.Equal(room.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(room.Alias));
            Assert.NotNull(host.Runtime.TryGetSeatForPlayer(room.RuntimeId, human.Id));
        }
        Assert.Equal(1, host.Runtime.GameCount);
    }

    [Theory, Trait("Category", "LobbyRepair")]
    [InlineData(null, "", 3)]
    [InlineData(null, "&bots=true", 3)]
    [InlineData(3, "&bots=false", 0)]
    [InlineData(0, "&bots=true", 0)]
    [InlineData(4, "", 3)]
    public async Task LegacyBotsFlag_IsExplicitlySeparateFromTheOrdinaryQuota(int? count, string legacy, int expected)
    {
        await using var host = new LobbyRepairFixture();
        var room = await host.CreateRoomAsync(await host.PlayerAsync(), count, extraQuery: legacy);
        var state = await host.StateAsync(room);
        Assert.Equal(expected, state.Seats.Count(seat => seat.IsBot));
        AssertHumanIdentitySet(state, [room.Owner.Id]);
        if (expected == 0) Assert.Equal(ChangshaPhase.Seating, state.Phase);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task FourBotSpectatorStarts_ButALateSpectatorCannotFillAnExistingHumanRoom()
    {
        await using var host = new LobbyRepairFixture();
        var spectator = await host.PlayerAsync();
        var allBots = await host.CreateRoomAsync(spectator, 4, seat: -1);
        var state = await host.StateAsync(allBots);
        Assert.All(state.Seats, seat => Assert.True(seat.IsBot));
        Assert.Null(host.Runtime.TryGetSeatForPlayer(allBots.RuntimeId, spectator.Id));
        await UntilAsync(() => host.Runtime.TryGetSnapshot(allBots.RuntimeId, out var started)
            && started!.Phase != ChangshaPhase.Seating, "Four-bot spectator creation did not start.");

        var owner = await host.PlayerAsync();
        var humanRoom = await host.CreateRoomAsync(owner, 0);
        var late = await host.SocketAsync(spectator, humanRoom.Alias, "seat=-1&botCount=4&dealMode=auto&handCount=16");
        await late.WaitAsync(frame => Type(frame) == "JOINED");
        await late.WaitAsync(IsFull);
        var after = await host.StateAsync(humanRoom);
        Assert.Empty(after.Seats.Where(seat => seat.IsBot));
        Assert.Equal(ChangshaPhase.Seating, after.Phase);
        Assert.Equal(DealMode.Manual, after.DealMode);
        Assert.Equal(4, after.MaxHands);
    }

    [Theory, Trait("Category", "LobbyRepair")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PersistedBotSeatsAndConfigurationSurviveRestartAndConflictingJoin(int bots)
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, bots, seat: 2);
        var before = await host.StateAsync(room);
        var rows = await host.StoredRuntimeIdsAsync();
        await host.RestartAsync();
        var reconnectedOwner = host.ReopenHttp(owner);
        _ = await host.JoinAsync(reconnectedOwner, room,
            "&botCount=4&bots=true&seat=0&dealMode=auto&seed=99&handCount=16&baseUnit=11&botDifficulty=Hard");
        var after = await host.StateAsync(room);
        Assert.Equal(room.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(room.Alias));
        Assert.Equal(rows, await host.StoredRuntimeIdsAsync());
        Assert.Equal(before.Seats.Where(seat => seat.IsBot).Select(seat => seat.SeatIndex),
            after.Seats.Where(seat => seat.IsBot).Select(seat => seat.SeatIndex));
        Assert.Equal(before.Seed, after.Seed);
        Assert.Equal(before.MaxHands, after.MaxHands);
        Assert.Equal(before.BaseUnit, after.BaseUnit);
        Assert.Equal(before.DealMode, after.DealMode);
        Assert.Equal("easy", host.Runtime.GetActiveBotDifficulty(room.RuntimeId));
        Assert.Equal(2, host.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        Assert.Equal(ChangshaPhase.Seating, after.Phase);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task ExplicitFillCannotExpandAliasQuota_ButNativeLegacyFillStillWorks()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var hub = await host.HubAsync(owner);
        var denied = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("FillWithBots", room.RuntimeId));
        Assert.Contains("room-bot-quota-locked", denied.Message);
        Assert.Empty((await host.StateAsync(room)).Seats.Where(seat => seat.IsBot));

        var created = await hub.InvokeAsync("CreateGame", "changsha-v1", Array.Empty<int>(), 20260917);
        var nativeId = created.GetProperty("gameId").GetString()!;
        _ = await hub.InvokeAsync("TakeSeat", nativeId, 0);
        Assert.True((await hub.InvokeAsync("FillWithBots", nativeId)).GetProperty("success").GetBoolean());
        Assert.True(host.Runtime.TryGetSnapshot(nativeId, out var native));
        Assert.Equal(3, native!.Seats.Count(seat => seat.IsBot));
    }

    [Theory, Trait("Category", "LobbyRepair")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PrematureNativeAndWsStartCannotFillOrStartAnIncompleteAliasRoom(int bots)
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, bots);
        var hub = await host.HubAsync(owner);
        try { _ = await hub.InvokeAsync("StartGame", room.RuntimeId); }
        catch (HubException) { /* Rejection or no-op is permitted; starting/filling is not. */ }
        var afterNative = await host.StateAsync(room);
        Assert.Equal(ChangshaPhase.Seating, afterNative.Phase);
        Assert.Equal(bots, afterNative.Seats.Count(seat => seat.IsBot));
        var mark = room.Creator.Frames.Count;
        await room.Creator.UpdateAsync(["match", 0, new { dealCommand = "start" }]);
        await room.Creator.SendAsync(new { type = "JOIN", gameId = room.Alias });
        await UntilAsync(() => room.Creator.Frames.Skip(mark).Any(frame => Type(frame) == "JOINED"),
            "The same-socket JOIN must finish after the earlier match-start command.");
        var afterWs = await host.StateAsync(room);
        Assert.Equal(ChangshaPhase.Seating, afterWs.Phase);
        Assert.Equal(bots, afterWs.Seats.Count(seat => seat.IsBot));

        // A positive native control prevents a missing StartGame RPC from satisfying the negative gate.
        var native = (await hub.InvokeAsync("CreateGame", "changsha-v1", Array.Empty<int>(), 20260917))
            .GetProperty("gameId").GetString()!;
        _ = await hub.InvokeAsync("TakeSeat", native, 0);
        _ = await hub.InvokeAsync("FillWithBots", native);
        Assert.True((await hub.InvokeAsync("StartGame", native)).GetProperty("success").GetBoolean());
        Assert.True(host.Runtime.TryGetSnapshot(native, out var started));
        Assert.NotEqual(ChangshaPhase.Seating, started!.Phase);
    }
}
