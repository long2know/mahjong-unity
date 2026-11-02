using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mahjong.Autotable.Api.Tests.Autotable;

public sealed class AutotableWsSeatAuthorizationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _databasePath;

    public Task InitializeAsync()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDirectory);
        _databasePath = Path.Combine(dataDirectory, $"ws-seat-auth-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_databasePath}");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => services.Configure<ChangshaRuntimeOptions>(options =>
            {
                options.BotTurnDelayMs = 60_000;
                options.BotClaimDelayMs = 60_000;
                options.BotPickupDelayMs = 60_000;
                options.ClaimWindowTimeoutMs = 60_000;
                options.DealBatchDelayMs = 0;
                options.PersistSnapshots = false;
            }));
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try
        {
            if (_databasePath is not null && File.Exists(_databasePath))
                File.Delete(_databasePath);
        }
        catch
        {
            // Best-effort cleanup of the isolated test database.
        }
        return Task.CompletedTask;
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task SpectatorDiscard_ExactExploit_IsRejectedAndStateVersionUnchanged()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Auto);
        var runtime = Runtime;

        Assert.True(runtime.TryGetSnapshot(game.RuntimeGameId, out var liveState));
        EnsureTileInSeatHand(liveState!, seat: 0, tileId: 8);
        var before = await SnapshotAsync(game.RuntimeGameId);

        await using var spectator = await OpenJoinedAsync(
            game.RelayGameId,
            "seat=-1&bots=false&botCount=0&dealMode=auto",
            NewPlayerId("spectator"));
        await game.Owner.DrainAsync();

        await spectator.SendRawAsync(
            """{"type":"UPDATE","entries":[["discard",0,{"tileId":8}]],"full":false}""");

        await AssertRejectedWithoutMutationAsync(
            spectator, game.RuntimeGameId, before,
            expectedAction: "discard",
            expectedReason: "spectator-owns-no-seat",
            expectedRequestedSeat: 0,
            expectedOwnedSeat: null);

        var after = await SnapshotAsync(game.RuntimeGameId);
        Assert.Contains(8, after.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles);
        Assert.DoesNotContain(after.DiscardPile, discard => discard.TileId == 8);
        Assert.False(await game.Owner.ContainsActionRejectedAsync(250));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task WrongOwnerDiscard_IsRejectedAndStateVersionUnchanged()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Auto);
        var before = await SnapshotAsync(game.RuntimeGameId);
        var tileId = before.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];

        await game.Owner.SendUpdateAsync(
            new object[] { "discard", 1, new { tileId } });

        await AssertRejectedWithoutMutationAsync(
            game.Owner, game.RuntimeGameId, before,
            expectedAction: "discard",
            expectedReason: "seat-not-owned-by-connection",
            expectedRequestedSeat: 1,
            expectedOwnedSeat: 0);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task SpoofedQuerySeat_DoesNotAuthorizeDiscard()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Auto);
        var before = await SnapshotAsync(game.RuntimeGameId);
        var tileId = before.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];

        await using var attacker = await OpenJoinedAsync(
            game.RelayGameId,
            "seat=0&bots=false&botCount=0&dealMode=auto",
            NewPlayerId("query-spoof"));
        await attacker.SendUpdateAsync(
            new object[] { "discard", 0, new { tileId } });

        await AssertRejectedWithoutMutationAsync(
            attacker, game.RuntimeGameId, before,
            expectedAction: "discard",
            expectedReason: "connection-owns-no-seat",
            expectedRequestedSeat: 0,
            expectedOwnedSeat: null);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task ValidOwnerDiscard_MutatesOnlyOwnedSeat()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Auto);
        var before = await SnapshotAsync(game.RuntimeGameId);
        var tileId = before.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];

        await game.Owner.SendUpdateAsync(
            new object[] { "discard", 0, new { tileId } });

        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(game.RuntimeGameId, out var state)
            && state is not null
            && state.StateVersion > before.StateVersion
            && state.DiscardPile.Any(discard => discard.SeatIndex == 0 && discard.TileId == tileId)));

        var after = await SnapshotAsync(game.RuntimeGameId);
        Assert.True(after.StateVersion > before.StateVersion);
        Assert.DoesNotContain(tileId, after.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles);
        Assert.False(await game.Owner.ContainsActionRejectedAsync(250));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task ReconnectOwner_DiscardRemainsAuthorized()
    {
        var ownerPlayerId = NewPlayerId("reconnect-owner");
        await using var game = await CreateBoundGameAsync(DealMode.Auto, ownerPlayerId);
        await game.Owner.DisposeAsync();

        Assert.Equal(0, Runtime.TryGetSeatForPlayer(game.RuntimeGameId, ownerPlayerId));

        await using var reconnect = await OpenJoinedAsync(
            game.RelayGameId,
            "bots=false&botCount=0&dealMode=auto",
            ownerPlayerId);
        var before = await SnapshotAsync(game.RuntimeGameId);
        var tileId = before.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];

        await reconnect.SendUpdateAsync(
            new object[] { "discard", 0, new { tileId } });

        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(game.RuntimeGameId, out var state)
            && state is not null
            && state.StateVersion > before.StateVersion
            && state.DiscardPile.Any(discard => discard.SeatIndex == 0 && discard.TileId == tileId)));
        Assert.False(await reconnect.ContainsActionRejectedAsync(250));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task Spectator_ClaimTypesAndPass_AreRejected()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Auto);
        await using var spectator = await OpenJoinedAsync(
            game.RelayGameId,
            "seat=-1&bots=false&botCount=0&dealMode=auto",
            NewPlayerId("claim-spectator"));

        foreach (var attempt in ClaimAttempts)
        {
            var before = await SnapshotAsync(game.RuntimeGameId);
            await spectator.SendUpdateAsync(
                new object[] { "claim", 0, attempt.Payload });

            await AssertRejectedWithoutMutationAsync(
                spectator, game.RuntimeGameId, before,
                expectedAction: attempt.ExpectedAction,
                expectedReason: "spectator-owns-no-seat",
                expectedRequestedSeat: 0,
                expectedOwnedSeat: null);
        }
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task WrongOwner_ClaimTypesAndPass_AreRejected()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Auto);

        foreach (var attempt in ClaimAttempts)
        {
            var before = await SnapshotAsync(game.RuntimeGameId);
            await game.Owner.SendUpdateAsync(
                new object[] { "claim", 1, attempt.Payload });

            await AssertRejectedWithoutMutationAsync(
                game.Owner, game.RuntimeGameId, before,
                expectedAction: attempt.ExpectedAction,
                expectedReason: "seat-not-owned-by-connection",
                expectedRequestedSeat: 1,
                expectedOwnedSeat: 0);
        }
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task Spectator_RollDice_IsRejected()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Manual);
        await using var spectator = await OpenJoinedAsync(
            game.RelayGameId,
            "seat=-1&bots=false&botCount=0&dealMode=manual",
            NewPlayerId("roll-spectator"));
        var before = await SnapshotAsync(game.RuntimeGameId);

        await spectator.SendUpdateAsync(
            new object[] { "pickup", "rollDice", new { seatIndex = 0 } });

        await AssertRejectedWithoutMutationAsync(
            spectator, game.RuntimeGameId, before,
            expectedAction: "pickup.rollDice",
            expectedReason: "spectator-owns-no-seat",
            expectedRequestedSeat: 0,
            expectedOwnedSeat: null);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task WrongOwner_RollDice_IsRejected()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Manual);
        var before = await SnapshotAsync(game.RuntimeGameId);

        await game.Owner.SendUpdateAsync(
            new object[] { "pickup", "rollDice", new { seatIndex = 1 } });

        await AssertRejectedWithoutMutationAsync(
            game.Owner, game.RuntimeGameId, before,
            expectedAction: "pickup.rollDice",
            expectedReason: "seat-not-owned-by-connection",
            expectedRequestedSeat: 1,
            expectedOwnedSeat: 0);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task Spectator_ManualPickup_IsRejected()
    {
        await using var game = await CreateManualPickupGameAsync();
        await using var spectator = await OpenJoinedAsync(
            game.RelayGameId,
            "seat=-1&bots=false&botCount=0&dealMode=manual",
            NewPlayerId("pickup-spectator"));
        var before = await SnapshotAsync(game.RuntimeGameId);
        var count = ChangshaGameStateMachine.ExpectedPickupCount(before.Phase);

        await spectator.SendUpdateAsync(
            new object[] { "pickup", "take", new { seatIndex = 0, count } });

        await AssertRejectedWithoutMutationAsync(
            spectator, game.RuntimeGameId, before,
            expectedAction: "pickup.take",
            expectedReason: "spectator-owns-no-seat",
            expectedRequestedSeat: 0,
            expectedOwnedSeat: null);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task WrongOwner_ManualPickup_IsRejected()
    {
        await using var game = await CreateManualPickupGameAsync();
        var before = await SnapshotAsync(game.RuntimeGameId);
        var count = ChangshaGameStateMachine.ExpectedPickupCount(before.Phase);

        await game.Owner.SendUpdateAsync(
            new object[] { "pickup", "take", new { seatIndex = 1, count } });

        await AssertRejectedWithoutMutationAsync(
            game.Owner, game.RuntimeGameId, before,
            expectedAction: "pickup.take",
            expectedReason: "seat-not-owned-by-connection",
            expectedRequestedSeat: 1,
            expectedOwnedSeat: 0);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task ValidOwner_RollDiceAndManualPickup_AreAccepted()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Manual);
        var beforeRoll = await SnapshotAsync(game.RuntimeGameId);

        await game.Owner.SendUpdateAsync(
            new object[] { "pickup", "rollDice", new { seatIndex = 0 } });

        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(game.RuntimeGameId, out var state)
            && state?.LastDiceRoll is not null
            && state.PickupSeatIndex == 0));
        var afterRoll = await SnapshotAsync(game.RuntimeGameId);
        Assert.True(afterRoll.StateVersion > beforeRoll.StateVersion);
        await game.Owner.DrainAsync();

        var count = ChangshaGameStateMachine.ExpectedPickupCount(afterRoll.Phase);
        var handCount = afterRoll.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles.Count;
        var wallCount = afterRoll.Wall.Count;
        await game.Owner.SendUpdateAsync(
            new object[] { "pickup", "take", new { seatIndex = 0, count } });

        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(game.RuntimeGameId, out var state)
            && state is not null
            && state.StateVersion > afterRoll.StateVersion));
        var afterTake = await SnapshotAsync(game.RuntimeGameId);
        Assert.Equal(handCount + count, afterTake.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles.Count);
        Assert.Equal(wallCount - count, afterTake.Wall.Count);
        Assert.False(await game.Owner.ContainsActionRejectedAsync(250));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task TakeSeat_IsAcquisitionAndDoesNotRequirePriorOwnership()
    {
        var relayGameId = NewGameId("take-seat");
        var playerId = NewPlayerId("seat-acquirer");
        await using var connection = await OpenJoinedAsync(
            relayGameId,
            "seat=-1&bots=false&botCount=0&dealMode=auto",
            playerId);

        await connection.SendUpdateAsync(
            new object[] { "seats", "untrusted-key", new { seat = 0 } });

        string? runtimeGameId = null;
        Assert.True(await WaitForAsync(() =>
        {
            runtimeGameId = Manager.GetRuntimeGameIdBoundTo(relayGameId);
            return runtimeGameId is not null
                && Runtime.TryGetSeatForPlayer(runtimeGameId, playerId) == 0;
        }));
        Assert.Equal(ChangshaPhase.Seating, (await SnapshotAsync(runtimeGameId!)).Phase);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task OccupiedSeatTake_DoesNotPersistSpoofedOwnershipOrLeakOnReconnect()
    {
        await using var game = await CreateBoundGameAsync(DealMode.Auto);
        var before = await SnapshotAsync(game.RuntimeGameId);
        var victimTileId = before.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];
        var attackerPlayerId = NewPlayerId("occupied-seat-attacker");

        await using var attacker = await OpenJoinedAsync(
            game.RelayGameId,
            "seat=0&bots=false&botCount=0&dealMode=auto",
            attackerPlayerId);

        var processingBarrierKey = NewPlayerId("occupied-seat-processing-barrier");
        await attacker.SendUpdateAsync(
            new object[] { "seats", attackerPlayerId, new { seat = 0 } },
            new object[] { "mouse", processingBarrierKey, new { x = 1, y = 2, z = 3 } });
        var processingBarrier = await game.Owner.ReadNonFullUpdateAsync();
        var processingBarrierEntry = Assert.Single(
            processingBarrier.GetProperty("entries").EnumerateArray(),
            entry => entry[0].GetString() == "mouse"
                && entry[1].GetString() == processingBarrierKey);
        Assert.Equal(1, processingBarrierEntry[2].GetProperty("x").GetInt32());

        var afterTake = await SnapshotAsync(game.RuntimeGameId);
        Assert.Equal(before.StateVersion, afterTake.StateVersion);

        await using var reconnect = await OpenSessionAsync(
            game.RelayGameId,
            "seat=0&bots=false&botCount=0&dealMode=auto",
            attackerPlayerId);
        var reconnectFull = await reconnect.JoinAndReadLatestAsync(game.RelayGameId);
        Assert.False(HasSeatOwnershipEntry(reconnectFull, attackerPlayerId, 0));
        Assert.True(HandProjectionIsOpaque(reconnectFull, 0));

        await reconnect.SendUpdateAsync(
            new object[] { "discard", 0, new { tileId = victimTileId } });
        await AssertRejectedWithoutMutationAsync(
            reconnect, game.RuntimeGameId, afterTake,
            expectedAction: "discard",
            expectedReason: "connection-owns-no-seat",
            expectedRequestedSeat: 0,
            expectedOwnedSeat: null);

        var ownerTileId = afterTake.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];
        await game.Owner.SendUpdateAsync(
            new object[] { "discard", 0, new { tileId = ownerTileId } });
        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(game.RuntimeGameId, out var state)
            && state is not null
            && state.StateVersion > afterTake.StateVersion
            && state.DiscardPile.Any(discard => discard.SeatIndex == 0 && discard.TileId == ownerTileId)));
    }

    [Theory, Trait("Category", "Authorization")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoomViewer_CrossRoomJoin_DoesNotInheritSourceOwnersPrivateHand(bool visitUnboundRoom)
    {
        var attackerPlayerId = NewPlayerId("room-viewer");
        await using var source = await CreateBoundGameAsync(DealMode.Auto, attackerPlayerId);
        await using var destination = await CreateBoundGameAsync(DealMode.Auto);
        var sourceBefore = await SnapshotAsync(source.RuntimeGameId);
        var destinationBefore = await SnapshotAsync(destination.RuntimeGameId);

        var sourceUpdates = await JoinWithRejectedDiscardBarrierAsync(source.Owner, source, ownedSeat: 0);
        foreach (var update in sourceUpdates)
            AssertOwnedHandProjection(update, sourceBefore, 0);

        await using var freshSocket = await OpenSessionAsync(
            destination.RelayGameId, "seat=0&bots=false&botCount=0&dealMode=auto", attackerPlayerId);
        var freshUpdates = await JoinWithRejectedDiscardBarrierAsync(freshSocket, destination);
        foreach (var update in freshUpdates)
            AssertOpaqueHandProjection(update, 0, 14);

        if (visitUnboundRoom)
        {
            var unboundRoom = NewGameId("room-viewer-unbound");
            await source.Owner.JoinAsync(unboundRoom);
            Assert.Null(Manager.GetRuntimeGameIdBoundTo(unboundRoom));
        }

        var switchedUpdates = await JoinWithRejectedDiscardBarrierAsync(source.Owner, destination);
        foreach (var update in switchedUpdates)
        {
            AssertOpaqueHandProjection(update, 0, 14);
            Assert.False(HasSeatOwnershipEntry(update, attackerPlayerId, 0));
        }

        var victimTileId = destinationBefore.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];
        await destination.Owner.SendUpdateAsync(new object[] { "discard", 0, new { tileId = victimTileId } });
        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(destination.RuntimeGameId, out var state)
            && state is not null
            && state.StateVersion > destinationBefore.StateVersion
            && state.DiscardPile.Any(discard => discard.SeatIndex == 0 && discard.TileId == victimTileId)));
        var broadcast = await source.Owner.ReadFullUpdateAsync();
        AssertOpaqueHandProjection(broadcast, 0, 13);
        Assert.False(HasSeatOwnershipEntry(broadcast, attackerPlayerId, 0));

        Assert.True(StateFingerprint(sourceBefore) == StateFingerprint(await SnapshotAsync(source.RuntimeGameId)));
        var returnedUpdates = await JoinWithRejectedDiscardBarrierAsync(source.Owner, source, ownedSeat: 0);
        foreach (var update in returnedUpdates)
            AssertOwnedHandProjection(update, sourceBefore, 0);
        var repeatedUpdates = await JoinWithRejectedDiscardBarrierAsync(source.Owner, source, ownedSeat: 0);
        foreach (var update in repeatedUpdates)
            AssertOwnedHandProjection(update, sourceBefore, 0);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task RoomViewer_CrossRoomOccupiedSeatTake_DoesNotPersistForgedSeatOnReconnect()
    {
        var attackerPlayerId = NewPlayerId("room-seat-attacker");
        await using var source = await CreateBoundGameAsync(DealMode.Auto, attackerPlayerId);
        await using var destination = await CreateBoundGameAsync(DealMode.Auto);
        var before = await SnapshotAsync(destination.RuntimeGameId);

        await source.Owner.JoinAsync(destination.RelayGameId);
        var barrierKey = NewPlayerId("room-seat-barrier");
        await source.Owner.SendUpdateAsync(
            new object[] { "seats", attackerPlayerId, new { seat = 0 } },
            new object[] { "mouse", barrierKey, new { x = 1, y = 2, z = 3 } });
        var barrier = await destination.Owner.ReadNonFullUpdateAsync();
        Assert.Single(barrier.GetProperty("entries").EnumerateArray(),
            entry => entry[0].GetString() == "mouse" && entry[1].GetString() == barrierKey);
        Assert.Null(Runtime.TryGetSeatForPlayer(destination.RuntimeGameId, attackerPlayerId));
        Assert.True(StateFingerprint(before) == StateFingerprint(await SnapshotAsync(destination.RuntimeGameId)));

        await using var reconnect = await OpenSessionAsync(
            destination.RelayGameId, "seat=0&bots=false&botCount=0&dealMode=auto", attackerPlayerId);
        var updates = await JoinWithRejectedDiscardBarrierAsync(reconnect, destination);
        foreach (var update in updates)
        {
            Assert.False(HasSeatOwnershipEntry(update, attackerPlayerId, 0));
            AssertOpaqueHandProjection(update, 0, 14);
        }
    }

    [Theory, Trait("Category", "Authorization")]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RoomViewer_CrossRoomJoin_UsesActualDestinationOwnership(int destinationSeat)
    {
        var playerId = NewPlayerId("two-room-owner");
        await using var source = await CreateBoundGameAsync(DealMode.Auto, playerId);
        await using var destination = await CreateBoundGameAsync(DealMode.Auto, playerId, destinationSeat);
        var before = await SnapshotAsync(destination.RuntimeGameId);
        var sourceBefore = await SnapshotAsync(source.RuntimeGameId);
        var destinationConnectionId = ViewerAuthorityAssertions.GrantedConnection(
            Manager, Runtime, destination.RuntimeGameId, destinationSeat).Id.ToString("N");
        var requestedSeat = (destinationSeat + 1) % 4;

        var updates = await JoinWithRejectedDiscardBarrierAsync(
            source.Owner, destination, ownedSeat: null, requestedSeat: requestedSeat);
        foreach (var update in updates)
        {
            Assert.True(HasSeatOwnershipEntry(update, playerId, destinationSeat));
            foreach (var hand in before.Hands)
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }

        await using var duplicate = await OpenSessionAsync(
            destination.RelayGameId, "seat=3&bots=false&botCount=0&dealMode=auto", playerId);
        var duplicateUpdates = await JoinWithRejectedDiscardBarrierAsync(
            duplicate, destination, ownedSeat: null, requestedSeat: requestedSeat);
        foreach (var update in duplicateUpdates)
        {
            Assert.True(HasSeatOwnershipEntry(update, playerId, destinationSeat));
            foreach (var hand in before.Hands)
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }
        foreach (var update in await JoinWithRejectedDiscardBarrierAsync(destination.Owner, destination, destinationSeat))
        {
            AssertOwnedHandProjection(update, before, destinationSeat);
            foreach (var hand in before.Hands.Where(hand => hand.SeatIndex != destinationSeat))
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }

        foreach (var update in await JoinWithRejectedDiscardBarrierAsync(source.Owner, source, ownedSeat: 0))
            AssertOwnedHandProjection(update, sourceBefore, 0);
        await destination.Owner.DisposeAsync();
        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSeatForConnection(destination.RuntimeGameId, destinationConnectionId) is null));
        foreach (var update in await JoinWithRejectedDiscardBarrierAsync(source.Owner, destination, destinationSeat))
        {
            AssertOwnedHandProjection(update, before, destinationSeat);
            Assert.True(HasSeatOwnershipEntry(update, playerId, destinationSeat));
            foreach (var hand in before.Hands.Where(hand => hand.SeatIndex != destinationSeat))
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }
        foreach (var update in await JoinWithRejectedDiscardBarrierAsync(
            duplicate, destination, ownedSeat: null, requestedSeat: requestedSeat))
        {
            Assert.True(HasSeatOwnershipEntry(update, playerId, destinationSeat));
            foreach (var hand in before.Hands)
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }

        var resumedConnectionId = ViewerAuthorityAssertions.GrantedConnection(
            Manager, Runtime, destination.RuntimeGameId, destinationSeat).Id.ToString("N");
        await source.Owner.DisposeAsync();
        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSeatForConnection(destination.RuntimeGameId, resumedConnectionId) is null));
        await using var reconnect = await OpenSessionAsync(
            destination.RelayGameId, "seat=3&bots=false&botCount=0&dealMode=auto", playerId);
        foreach (var update in await JoinWithRejectedDiscardBarrierAsync(reconnect, destination, destinationSeat))
        {
            AssertOwnedHandProjection(update, before, destinationSeat);
            Assert.True(HasSeatOwnershipEntry(update, playerId, destinationSeat));
            foreach (var hand in before.Hands.Where(hand => hand.SeatIndex != destinationSeat))
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }
        Assert.Equal(destination.RuntimeGameId, Manager.GetRuntimeGameIdBoundTo(destination.RelayGameId));
        Assert.Single((await SnapshotAsync(destination.RuntimeGameId)).Seats,
            seat => !seat.IsBot && seat.PlayerId == playerId);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task RoomViewer_ExplicitSpectator_DoesNotInferOwnershipAcrossRooms()
    {
        var playerId = NewPlayerId("spectating-owner");
        await using var source = await CreateBoundGameAsync(DealMode.Auto, playerId);
        await using var destination = await CreateBoundGameAsync(DealMode.Auto, playerId);
        await using var spectator = await OpenSessionAsync(
            source.RelayGameId, "seat=-1&bots=false&botCount=0&dealMode=auto", playerId);

        foreach (var game in new[] { source, destination, destination, source })
        {
            var before = await SnapshotAsync(game.RuntimeGameId);
            var updates = await JoinWithRejectedDiscardBarrierAsync(spectator, game, ownedSeat: null,
                expectedReason: "spectator-owns-no-seat", requestedSeat: 1);
            foreach (var update in updates)
            {
                AssertOpaqueHandProjection(update, 0, 14);
                foreach (var hand in before.Hands.Where(hand => hand.SeatIndex != 0))
                    AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
            }
            foreach (var update in await JoinWithRejectedDiscardBarrierAsync(game.Owner, game, ownedSeat: 0))
            {
                AssertOwnedHandProjection(update, before, 0);
                foreach (var hand in before.Hands.Where(hand => hand.SeatIndex != 0))
                    AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
            }
        }
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task RoomViewer_SameRoomJoin_AfterLeaveDoesNotRestoreReleasedSeat()
    {
        var gameId = NewGameId("room-viewer-leave");
        var formerPlayerId = NewPlayerId("former-owner");
        var replacementPlayerId = NewPlayerId("replacement-owner");
        await using var former = await OpenJoinedAsync(
            gameId, "seat=0&bots=false&botCount=0&dealMode=auto", formerPlayerId);
        await using var replacement = await OpenJoinedAsync(
            gameId, "seat=0&botCount=3&dealMode=auto&seed=73", replacementPlayerId);

        await former.SendUpdateAsync(
            new object[] { "perPlayer", "seats", true },
            new object[] { "perPlayer", "nicks", true },
            new object[] { "seats", formerPlayerId, new { seat = 0 } });
        _ = await replacement.ReadNonFullUpdateAsync();
        string? runtimeGameId = null;
        Assert.True(await WaitForAsync(() =>
        {
            runtimeGameId = Manager.GetRuntimeGameIdBoundTo(gameId);
            return runtimeGameId is not null && Runtime.TryGetSeatForPlayer(runtimeGameId, formerPlayerId) == 0;
        }));

        var barrierKey = NewPlayerId("leave-processing-barrier");
        await former.SendUpdateAsync(
            new object[] { "seats", formerPlayerId, new { seat = (int?)null } },
            new object[] { "mouse", barrierKey, new { x = 1, y = 2, z = 3 } });
        var tombstones = await replacement.ReadNonFullUpdateAsync();
        Assert.Contains(tombstones.GetProperty("entries").EnumerateArray(),
            entry => entry[0].GetString() == "seats" && entry[1].GetString() == formerPlayerId
                && entry[2].ValueKind == JsonValueKind.Null);
        var barrier = await replacement.ReadNonFullUpdateAsync();
        Assert.Single(barrier.GetProperty("entries").EnumerateArray(),
            entry => entry[0].GetString() == "mouse" && entry[1].GetString() == barrierKey);
        Assert.Null(Runtime.TryGetSeatForPlayer(runtimeGameId!, formerPlayerId));

        await replacement.SendUpdateAsync(new object[] { "seats", replacementPlayerId, new { seat = 0 } });
        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSeatForPlayer(runtimeGameId!, replacementPlayerId) == 0));
        var replacementSeated = await SnapshotAsync(runtimeGameId!);
        Assert.Equal(ChangshaPhase.Seating, replacementSeated.Phase);
        Assert.Empty(replacementSeated.Seats.Where(seat => seat.IsBot));
        var humanIds = new[] { replacementPlayerId, NewPlayerId("second-human"),
            NewPlayerId("third-human"), NewPlayerId("fourth-human") };
        Assert.Equal(4, humanIds.Distinct(StringComparer.Ordinal).Count());
        await using var secondHuman = await OpenJoinedAsync(gameId, "join=1", humanIds[1]);
        Assert.Equal(1, Runtime.TryGetSeatForPlayer(runtimeGameId!, humanIds[1]));
        Assert.Equal(ChangshaPhase.Seating, (await SnapshotAsync(runtimeGameId!)).Phase);
        await using var thirdHuman = await OpenJoinedAsync(gameId, "join=1", humanIds[2]);
        Assert.Equal(2, Runtime.TryGetSeatForPlayer(runtimeGameId!, humanIds[2]));
        var waitingForFourth = await SnapshotAsync(runtimeGameId!);
        Assert.Equal(ChangshaPhase.Seating, waitingForFourth.Phase);
        Assert.Empty(waitingForFourth.Seats.Where(seat => seat.IsBot));
        Assert.Equal(new[] { 0, 1, 2 }, humanIds.Take(3)
            .Select(id => Runtime.TryGetSeatForPlayer(runtimeGameId!, id)!.Value));
        await using var fourthHuman = await OpenJoinedAsync(gameId, "join=1", humanIds[3]);
        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(runtimeGameId!, out var state)
            && state?.Phase == ChangshaPhase.AwaitingDiscard
            && Runtime.TryGetSeatForPlayer(runtimeGameId!, replacementPlayerId) == 0));
        var dealt = await SnapshotAsync(runtimeGameId!);
        Assert.Equal(runtimeGameId, Manager.GetRuntimeGameIdBoundTo(gameId));
        Assert.Empty(dealt.Seats.Where(seat => seat.IsBot));
        Assert.Equal(humanIds, dealt.Seats.OrderBy(seat => seat.SeatIndex).Select(seat => seat.PlayerId));
        Assert.Equal(new[] { 14, 13, 13, 13 }, dealt.Hands.OrderBy(hand => hand.SeatIndex)
            .Select(hand => hand.ConcealedTiles.Count));
        await using var game = new BoundGame(gameId, runtimeGameId!, replacement);
        var updates = await JoinWithRejectedDiscardBarrierAsync(former, game);
        foreach (var update in updates)
        {
            AssertOpaqueHandProjection(update, 0, 14);
            Assert.False(HasSeatOwnershipEntry(update, formerPlayerId, 0));
            foreach (var hand in dealt.Hands.Where(hand => hand.SeatIndex != 0))
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }
        foreach (var update in await JoinWithRejectedDiscardBarrierAsync(replacement, game, ownedSeat: 0))
        {
            AssertOwnedHandProjection(update, dealt, 0);
            foreach (var hand in dealt.Hands.Where(hand => hand.SeatIndex != 0))
                AssertOpaqueHandProjection(update, hand.SeatIndex, hand.ConcealedTiles.Count);
        }
        await former.DrainAsync();
        var tileId = dealt.Hands.Single(hand => hand.SeatIndex == 0).ConcealedTiles[0];
        await replacement.SendUpdateAsync(new object[] { "discard", 0, new { tileId } });
        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(runtimeGameId!, out var state) && state is not null
            && state.StateVersion > dealt.StateVersion
            && state.DiscardPile.Any(discard => discard.SeatIndex == 0 && discard.TileId == tileId)));
        var broadcast = await former.ReadFullUpdateAsync();
        AssertOpaqueHandProjection(broadcast, 0, 13);
        Assert.False(HasSeatOwnershipEntry(broadcast, formerPlayerId, 0));
        ViewerAuthorityAssertions.Expect(broadcast, gameId, null);
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task RoomViewer_RelayCrossRoomJoin_PreservesRoomScopedPassthrough()
    {
        var sourceId = NewGameId("relay-source");
        var destinationId = NewGameId("relay-destination");
        const string query = "variant=four_player&seat=-1&bots=false";
        await using var sender = await OpenJoinedAsync(sourceId, query, NewPlayerId("relay-sender"));
        await using var sourcePeer = await OpenJoinedAsync(sourceId, query, NewPlayerId("relay-source-peer"));
        await using var destinationPeer = await OpenJoinedAsync(destinationId, query, NewPlayerId("relay-destination-peer"));

        await sender.SendUpdateAsync(new object[] { "seats", "source-seat", new { seat = 0 } });
        Assert.True(HasSeatOwnershipEntry(await sourcePeer.ReadNonFullUpdateAsync(), "source-seat", 0));
        await sender.JoinAsync(destinationId);
        await sender.SendUpdateAsync(new object[] { "seats", "destination-seat", new { seat = 2 } });
        Assert.True(HasSeatOwnershipEntry(await destinationPeer.ReadNonFullUpdateAsync(), "destination-seat", 2));

        var sourceSnapshot = await sourcePeer.JoinAndReadLatestAsync(sourceId);
        Assert.True(HasSeatOwnershipEntry(sourceSnapshot, "source-seat", 0));
        Assert.False(HasSeatOwnershipEntry(sourceSnapshot, "destination-seat", 2));
        var destinationSnapshot = await sender.JoinAndReadLatestAsync(destinationId);
        Assert.True(HasSeatOwnershipEntry(destinationSnapshot, "destination-seat", 2));
        Assert.False(HasSeatOwnershipEntry(destinationSnapshot, "source-seat", 0));
        Assert.Null(Manager.GetRuntimeGameIdBoundTo(sourceId));
        Assert.Null(Manager.GetRuntimeGameIdBoundTo(destinationId));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task SpectatorLeaveSeat_CannotReleaseAnotherConnectionsSeat()
    {
        var relayGameId = NewGameId("leave-seat");
        var ownerPlayerId = NewPlayerId("leave-owner");
        await using var owner = await OpenJoinedAsync(
            relayGameId,
            "seat=0&bots=false&botCount=0&dealMode=auto",
            ownerPlayerId);
        await owner.SendUpdateAsync(
            new object[] { "seats", "owner", new { seat = 0 } });

        string? runtimeGameId = null;
        Assert.True(await WaitForAsync(() =>
        {
            runtimeGameId = Manager.GetRuntimeGameIdBoundTo(relayGameId);
            return runtimeGameId is not null
                && Runtime.TryGetSeatForPlayer(runtimeGameId, ownerPlayerId) == 0;
        }));
        await owner.DrainAsync();
        var before = await SnapshotAsync(runtimeGameId!);

        await using var spectator = await OpenJoinedAsync(
            relayGameId,
            "seat=-1&bots=false&botCount=0&dealMode=auto",
            NewPlayerId("leave-spectator"));
        await owner.DrainAsync();
        await spectator.SendUpdateAsync(
            new object[] { "seats", "spectator", new { seat = (int?)null } },
            new object[] { "mouse", "spectator", new { x = 1, y = 2, z = 3 } });
        _ = await owner.ReadNonFullUpdateAsync();

        var after = await SnapshotAsync(runtimeGameId!);
        Assert.Equal(before.StateVersion, after.StateVersion);
        Assert.Equal(0, Runtime.TryGetSeatForPlayer(runtimeGameId!, ownerPlayerId));
        Assert.Equal(ownerPlayerId, after.Seats[0].PlayerId);
        Assert.False(await spectator.ContainsActionRejectedAsync(250));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task Spectator_MatchStart_RemainsLifecycleAction()
    {
        var relayGameId = NewGameId("match-start");
        var runtimeGameId = await Runtime.CreateGameAsync(
            seed: 193,
            botSeatIndexes: Array.Empty<int>(),
            hostPlayerId: null,
            hostConnectionId: null);
        Manager.BindRuntimeGameForTest(relayGameId, runtimeGameId);
        var before = await SnapshotAsync(runtimeGameId);

        await using var spectator = await OpenJoinedAsync(
            relayGameId,
            "seat=-1&bots=true&botCount=0&dealMode=auto",
            NewPlayerId("match-spectator"));
        await spectator.SendUpdateAsync(
            new object[] { "match", 0, new { dealCommand = "start" } });

        Assert.True(await WaitForAsync(() =>
            Runtime.TryGetSnapshot(runtimeGameId, out var state)
            && state is not null
            && state.Phase != ChangshaPhase.Seating
            && state.StateVersion > before.StateVersion));
        Assert.False(await spectator.ContainsActionRejectedAsync(300));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task UnboundChangshaMutation_GetsExplicitNoGameRejection()
    {
        var relayGameId = NewGameId("unbound");
        await using var connection = await OpenJoinedAsync(
            relayGameId,
            "seat=-1&bots=false&botCount=0&dealMode=auto",
            NewPlayerId("unbound-spectator"));

        await connection.SendUpdateAsync(
            new object[] { "discard", 0, new { tileId = 8 } });

        var rejection = await connection.ReadActionRejectedAsync();
        AssertRejection(
            rejection,
            expectedAction: "discard",
            expectedReason: "no-game",
            expectedRequestedSeat: 0,
            expectedOwnedSeat: null);
        Assert.False(await connection.ContainsFullUpdateAsync(250));
        Assert.Null(Manager.GetRuntimeGameIdBoundTo(relayGameId));
    }

    [Fact, Trait("Category", "Authorization")]
    public async Task RelayVariant_ForwardsMutationEntriesWithoutAuthorizationInterception()
    {
        var relayGameId = NewGameId("relay");
        await using var sender = await OpenJoinedAsync(
            relayGameId,
            "variant=four_player&seat=-1&bots=false",
            NewPlayerId("relay-sender"));
        await using var peer = await OpenJoinedAsync(
            relayGameId,
            "variant=four_player&seat=2&bots=false",
            NewPlayerId("relay-peer"));
        await sender.DrainAsync();
        await peer.DrainAsync();

        await sender.SendRawAsync(
            """{"type":"UPDATE","entries":[["discard",0,{"tileId":8}],["claim","1",{"action":"pass","type":null}],["pickup","rollDice",{"seatIndex":2}],["seats","relay-key",{"seat":3}],["mouse","relay-key",{"x":1,"y":2,"z":3}]],"full":false}""");

        var forwarded = await peer.ReadNonFullUpdateAsync();
        var entries = forwarded.GetProperty("entries");
        Assert.Equal(5, entries.GetArrayLength());
        Assert.Equal(new[] { "discard", "claim", "pickup", "seats", "mouse" },
            entries.EnumerateArray().Select(entry => entry[0].GetString()).ToArray());
        Assert.Equal(8, entries[0][2].GetProperty("tileId").GetInt32());
        Assert.Equal("pass", entries[1][2].GetProperty("action").GetString());
        Assert.Equal("rollDice", entries[2][1].GetString());
        Assert.Equal(3, entries[3][2].GetProperty("seat").GetInt32());
        Assert.False(await sender.ContainsActionRejectedAsync(250));
        Assert.Null(Manager.GetRuntimeGameIdBoundTo(relayGameId));
    }

    private static readonly ClaimAttempt[] ClaimAttempts =
    [
        new("claim", new { action = "claim", type = "Pung" }),
        new("claim", new { action = "claim", type = "Chow" }),
        new("claim", new { action = "claim", type = "Kong" }),
        new("claim", new { action = "claim", type = "Hu" }),
        new("pass", new { action = "pass", type = (string?)null })
    ];

    private IChangshaGameRuntime Runtime =>
        _factory!.Services.GetRequiredService<IChangshaGameRuntime>();

    private AutotableConnectionManager Manager =>
        _factory!.Services.GetRequiredService<AutotableConnectionManager>();

    private async Task<BoundGame> CreateBoundGameAsync(
        DealMode dealMode,
        string? ownerPlayerId = null,
        int ownerSeatIndex = 0)
    {
        var relayGameId = NewGameId(dealMode == DealMode.Manual ? "manual" : "auto");
        ownerPlayerId ??= NewPlayerId("owner");
        var owner = await OpenJoinedAsync(
            relayGameId,
            $"seat={ownerSeatIndex}&botCount=3&dealMode={dealMode.ToString().ToLowerInvariant()}&seed=73",
            ownerPlayerId);

        try
        {
            await owner.SendUpdateAsync(
                new object[] { "seats", "owner", new { seat = ownerSeatIndex } });

            string? runtimeGameId = null;
            Assert.True(await WaitForAsync(() =>
            {
                runtimeGameId = Manager.GetRuntimeGameIdBoundTo(relayGameId);
                if (runtimeGameId is null
                    || !Runtime.TryGetSnapshot(runtimeGameId, out var state)
                    || state is null)
                {
                    return false;
                }

                return dealMode == DealMode.Manual
                    ? state.Phase == ChangshaPhase.RollingDice
                    : state.Phase == ChangshaPhase.AwaitingDiscard
                      && state.ActiveSeatIndex == 0
                      && state.Hands.Sum(hand => hand.ConcealedTiles.Count) == 53;
            }));
            await owner.DrainAsync();
            return new BoundGame(relayGameId, runtimeGameId!, owner);
        }
        catch
        {
            await owner.DisposeAsync();
            throw;
        }
    }

    private async Task<BoundGame> CreateManualPickupGameAsync()
    {
        var game = await CreateBoundGameAsync(DealMode.Manual);
        try
        {
            await game.Owner.SendUpdateAsync(
                new object[] { "pickup", "rollDice", new { seatIndex = 0 } });
            Assert.True(await WaitForAsync(() =>
                Runtime.TryGetSnapshot(game.RuntimeGameId, out var state)
                && state?.LastDiceRoll is not null
                && state.PickupSeatIndex == 0));
            await game.Owner.DrainAsync();
            return game;
        }
        catch
        {
            await game.DisposeAsync();
            throw;
        }
    }

    private async Task<WsSession> OpenJoinedAsync(
        string relayGameId,
        string query,
        string playerId)
    {
        var client = _factory!.Server.CreateWebSocketClient();
        var cookie = _factory.Services.GetRequiredService<PlayerIdentityService>().Protect(playerId);
        client.ConfigureRequest = request =>
            request.Headers["Cookie"] = $"{PlayerIdentityService.CookieName}={cookie}";
        var uri = new Uri(
            _factory.Server.BaseAddress,
            $"autotable/ws?gameId={Uri.EscapeDataString(relayGameId)}&{query}");
        var session = new WsSession(await client.ConnectAsync(uri, CancellationToken.None));
        try
        {
            await session.JoinAsync(relayGameId);
            await session.DrainAsync();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task<WsSession> OpenSessionAsync(
        string relayGameId,
        string query,
        string playerId)
    {
        var client = _factory!.Server.CreateWebSocketClient();
        var cookie = _factory.Services.GetRequiredService<PlayerIdentityService>().Protect(playerId);
        client.ConfigureRequest = request =>
            request.Headers["Cookie"] = $"{PlayerIdentityService.CookieName}={cookie}";
        var uri = new Uri(
            _factory.Server.BaseAddress,
            $"autotable/ws?gameId={Uri.EscapeDataString(relayGameId)}&{query}");
        return new WsSession(await client.ConnectAsync(uri, CancellationToken.None));
    }

    private async Task AssertRejectedWithoutMutationAsync(
        WsSession connection,
        string runtimeGameId,
        ChangshaGameState before,
        string expectedAction,
        string expectedReason,
        int? expectedRequestedSeat,
        int? expectedOwnedSeat)
    {
        var (rejection, resync) = await connection.ReadRejectionAndResyncAsync();
        AssertRejection(
            rejection,
            expectedAction,
            expectedReason,
            expectedRequestedSeat,
            expectedOwnedSeat);
        Assert.Equal("UPDATE", resync.GetProperty("type").GetString());
        Assert.True(resync.GetProperty("full").GetBoolean());

        var after = await SnapshotAsync(runtimeGameId);
        Assert.Equal(before.StateVersion, after.StateVersion);
        Assert.Equal(StateFingerprint(before), StateFingerprint(after));
    }

    private async Task<IReadOnlyList<JsonElement>> JoinWithRejectedDiscardBarrierAsync(
        WsSession connection,
        BoundGame game,
        int? ownedSeat = null,
        string? expectedReason = null,
        int? requestedSeat = null)
    {
        var before = await SnapshotAsync(game.RuntimeGameId);
        var requested = requestedSeat ?? (ownedSeat.HasValue ? (ownedSeat.Value + 1) % 4 : 0);
        var tileId = before.Hands.Single(hand => hand.SeatIndex == requested).ConcealedTiles[0];
        var updates = new List<JsonElement>();
        var joined = false;
        long revision = 0;

        await connection.SendRawAsync(JsonSerializer.Serialize(new { type = "JOIN", gameId = game.RelayGameId }));
        // The rejected action fences JOIN processing, including every initial/re-inferred
        // snapshot. Inspect the whole stream, not only a last snapshot after a quiet period.
        await connection.SendUpdateAsync(new object[] { "discard", requested, new { tileId } });
        var (rejection, _) = await connection.ReadRejectionAndResyncAsync(inspectEnvelope: envelope =>
        {
            if (envelope.GetProperty("type").GetString() == "JOINED")
            {
                Assert.True(envelope.GetProperty("gameId").GetString() == game.RelayGameId);
                revision = ViewerAuthorityAssertions.Expect(envelope, game.RelayGameId, ownedSeat, revision);
                joined = true;
            }
            else if (joined)
            {
                revision = ViewerAuthorityAssertions.Expect(envelope, game.RelayGameId, ownedSeat, revision);
                if (envelope.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True)
                    updates.Add(envelope);
            }
        });
        Assert.True(joined);
        Assert.True(updates.Count >= 2, "Expected a JOIN snapshot and the rejection resync.");
        AssertRejection(rejection, "discard",
            expectedReason ?? (ownedSeat.HasValue ? "seat-not-owned-by-connection" : "connection-owns-no-seat"),
            requested, ownedSeat);
        var after = await SnapshotAsync(game.RuntimeGameId);
        Assert.Equal(before.StateVersion, after.StateVersion);
        Assert.True(StateFingerprint(before) == StateFingerprint(after));
        return updates;
    }

    private static JsonElement[] HandProjectionEntries(JsonElement update, int seatIndex) =>
        update.GetProperty("entries").EnumerateArray()
            .Where(entry => entry[0].GetString() == "things"
                && entry[2].ValueKind == JsonValueKind.Object
                && entry[2].TryGetProperty("slotName", out var slot)
                && slot.ValueKind == JsonValueKind.String
                && slot.GetString()!.StartsWith("hand.", StringComparison.Ordinal)
                && slot.GetString()!.EndsWith($"@{seatIndex}", StringComparison.Ordinal))
            .ToArray();

    private static void AssertOpaqueHandProjection(JsonElement update, int seatIndex, int count)
    {
        var entries = HandProjectionEntries(update, seatIndex);
        Assert.Equal(count, entries.Length);
        Assert.True(HandProjectionIsOpaque(update, seatIndex), "Foreign hand must remain opaque in every snapshot.");
        foreach (var entry in entries)
            Assert.True(entry[1].ValueKind == JsonValueKind.String
                && entry[1].GetString()!.StartsWith("h_", StringComparison.Ordinal));
    }

    private static void AssertOwnedHandProjection(JsonElement update, ChangshaGameState state, int seatIndex)
    {
        var expected = state.Hands.Single(hand => hand.SeatIndex == seatIndex).ConcealedTiles;
        var entries = HandProjectionEntries(update, seatIndex);
        Assert.Equal(expected.Count, entries.Length);
        foreach (var entry in entries)
        {
            Assert.Equal(JsonValueKind.Number, entry[1].ValueKind);
            Assert.Equal(1, entry[2].GetProperty("rotationIndex").GetInt32());
        }
        Assert.True(expected.Order().SequenceEqual(entries.Select(entry => entry[1].GetInt32()).Order()),
            "The destination owner's projection must contain exactly its own hand.");
    }

    private static void AssertRejection(
        JsonElement envelope,
        string expectedAction,
        string expectedReason,
        int? expectedRequestedSeat,
        int? expectedOwnedSeat)
    {
        Assert.Equal("UPDATE", envelope.GetProperty("type").GetString());
        Assert.False(envelope.GetProperty("full").GetBoolean());
        var entries = envelope.GetProperty("entries");
        var entry = Assert.Single(
            entries.EnumerateArray(),
            candidate => candidate[0].GetString() == AutotableConnectionManager.ActionRejectedKind);
        Assert.Equal("current", entry[1].GetString());
        var value = entry[2];
        Assert.Equal(expectedAction, value.GetProperty("action").GetString());
        Assert.Equal(expectedReason, value.GetProperty("reason").GetString());
        AssertNullableInt(value.GetProperty("requestedSeat"), expectedRequestedSeat);
        AssertNullableInt(value.GetProperty("ownedSeat"), expectedOwnedSeat);
    }

    private static void AssertNullableInt(JsonElement actual, int? expected)
    {
        if (expected.HasValue)
            Assert.Equal(expected.Value, actual.GetInt32());
        else
            Assert.Equal(JsonValueKind.Null, actual.ValueKind);
    }

    private static bool HasSeatOwnershipEntry(JsonElement update, string playerId, int seatIndex)
    {
        var entries = update.GetProperty("entries");
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry[0].GetString() != "seats")
                continue;
            if (entry[1].GetString() != playerId)
                continue;
            if (entry[2].ValueKind != JsonValueKind.Object)
                continue;
            if (!entry[2].TryGetProperty("seat", out var seatEl) || seatEl.ValueKind != JsonValueKind.Number)
                continue;
            if (seatEl.GetInt32() == seatIndex)
                return true;
        }

        return false;
    }

    private static bool HandProjectionIsOpaque(JsonElement update, int seatIndex)
    {
        var entries = update.GetProperty("entries");
        var seen = false;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry[0].GetString() != "things")
                continue;
            if (entry[2].ValueKind != JsonValueKind.Object)
                continue;
            if (!entry[2].TryGetProperty("slotName", out var slotNameEl) || slotNameEl.ValueKind != JsonValueKind.String)
                continue;
            var slotName = slotNameEl.GetString() ?? string.Empty;
            if (!slotName.StartsWith("hand.", StringComparison.Ordinal) || !slotName.EndsWith($"@{seatIndex}", StringComparison.Ordinal))
                continue;

            seen = true;
            if (entry[1].ValueKind == JsonValueKind.Number && entry[1].TryGetInt64(out _))
                return false;
            if (entry[1].ValueKind == JsonValueKind.String
                && long.TryParse(entry[1].GetString(), out _))
                return false;
            if (entry[2].TryGetProperty("face", out var faceEl) && faceEl.ValueKind != JsonValueKind.Null)
                return false;
            if (entry[2].TryGetProperty("rotationIndex", out var rotationEl) && rotationEl.GetInt32() != 2)
                return false;
        }

        return seen;
    }

    private async Task<ChangshaGameState> SnapshotAsync(string runtimeGameId) =>
        await Runtime.TryGetSnapshotCopyAsync(runtimeGameId)
        ?? throw new InvalidOperationException($"Runtime game {runtimeGameId} disappeared.");

    private static string StateFingerprint(ChangshaGameState state) =>
        JsonSerializer.Serialize(state);

    private static void EnsureTileInSeatHand(ChangshaGameState state, int seat, int tileId)
    {
        var target = state.Hands.Single(hand => hand.SeatIndex == seat).ConcealedTiles;
        if (target.Contains(tileId))
            return;

        var displaced = target[0];
        var wallIndex = state.Wall.IndexOf(tileId);
        if (wallIndex >= 0)
        {
            state.Wall[wallIndex] = displaced;
            target[0] = tileId;
            return;
        }

        foreach (var hand in state.Hands.Where(hand => hand.SeatIndex != seat))
        {
            var index = hand.ConcealedTiles.IndexOf(tileId);
            if (index < 0) continue;
            hand.ConcealedTiles[index] = displaced;
            target[0] = tileId;
            return;
        }

        throw new InvalidOperationException($"Tile {tileId} was not present in the dealt game.");
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(20);
        }
        return predicate();
    }

    private static string NewGameId(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}";

    private static string NewPlayerId(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}";

    private sealed record ClaimAttempt(string ExpectedAction, object Payload);

    private sealed class BoundGame : IAsyncDisposable
    {
        public BoundGame(string relayGameId, string runtimeGameId, WsSession owner)
        {
            RelayGameId = relayGameId;
            RuntimeGameId = runtimeGameId;
            Owner = owner;
        }

        public string RelayGameId { get; }
        public string RuntimeGameId { get; }
        public WsSession Owner { get; }

        public ValueTask DisposeAsync() => Owner.DisposeAsync();
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private bool _disposed;

        public WsSession(WebSocket socket) => _socket = socket;

        public Task SendUpdateAsync(params object[][] entries) =>
            SendRawAsync(JsonSerializer.Serialize(new
            {
                type = "UPDATE",
                entries,
                full = false
            }));

        public async Task SendRawAsync(string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task JoinAsync(string gameId)
        {
            await SendRawAsync(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            var joined = false;
            var fullUpdate = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && (!joined || !fullUpdate))
            {
                var envelope = await ReadEnvelopeAsync(
                    Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                var type = envelope.GetProperty("type").GetString();
                joined |= type == "JOINED";
                fullUpdate |= type == "UPDATE"
                    && envelope.TryGetProperty("full", out var full)
                    && full.ValueKind == JsonValueKind.True;
            }
            if (!joined || !fullUpdate)
                throw new TimeoutException("JOIN did not yield JOINED and a full UPDATE.");
        }

        public async Task<JsonElement> JoinAndReadLatestAsync(string gameId, int quietMs = 400, int hardTimeoutMs = 5_000)
        {
            await SendRawAsync(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            var joined = false;
            JsonElement? lastFull = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(hardTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                JsonElement envelope;
                try
                {
                    envelope = await ReadEnvelopeAsync(
                        Math.Max(1, Math.Min(quietMs, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)));
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var type = envelope.GetProperty("type").GetString();
                joined |= type == "JOINED";
                if (type == "UPDATE"
                    && envelope.TryGetProperty("full", out var full)
                    && full.ValueKind == JsonValueKind.True)
                {
                    lastFull = envelope;
                }
            }

            if (!joined || lastFull is null)
                throw new TimeoutException("JOIN did not yield JOINED and a full UPDATE.");

            return lastFull.Value;
        }

        public async Task<(JsonElement Rejection, JsonElement Resync)> ReadRejectionAndResyncAsync(
            int timeoutMs = 5_000,
            Action<JsonElement>? inspectEnvelope = null)
        {
            JsonElement? rejection = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var envelope = await ReadEnvelopeAsync(
                    Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                inspectEnvelope?.Invoke(envelope);
                if (TryFindActionRejected(envelope, out _) && rejection is null)
                {
                    rejection = envelope;
                    continue;
                }

                if (rejection.HasValue
                    && envelope.TryGetProperty("type", out var type)
                    && type.GetString() == "UPDATE"
                    && envelope.TryGetProperty("full", out var full)
                    && full.ValueKind == JsonValueKind.True)
                {
                    return (rejection.Value, envelope);
                }
            }
            throw new TimeoutException("Did not receive actionRejected followed by a full resync.");
        }

        public async Task<JsonElement> ReadActionRejectedAsync(int timeoutMs = 5_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var envelope = await ReadEnvelopeAsync(
                    Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                if (TryFindActionRejected(envelope, out _))
                    return envelope;
            }
            throw new TimeoutException("Did not receive actionRejected.");
        }

        public Task<JsonElement> ReadFullUpdateAsync(int timeoutMs = 5_000) =>
            ReadUpdateAsync(isFull: true, timeoutMs);

        public Task<JsonElement> ReadNonFullUpdateAsync(int timeoutMs = 5_000) =>
            ReadUpdateAsync(isFull: false, timeoutMs);

        private async Task<JsonElement> ReadUpdateAsync(bool isFull, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var envelope = await ReadEnvelopeAsync(
                    Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                if (envelope.TryGetProperty("type", out var type)
                    && type.GetString() == "UPDATE"
                    && envelope.TryGetProperty("full", out var full)
                    && full.ValueKind == (isFull ? JsonValueKind.True : JsonValueKind.False))
                {
                    return envelope;
                }
            }
            throw new TimeoutException("Did not receive the requested UPDATE.");
        }

        public async Task<bool> ContainsActionRejectedAsync(int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var envelope = await ReadEnvelopeAsync(
                        Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                    if (TryFindActionRejected(envelope, out _))
                        return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            return false;
        }

        public async Task<bool> ContainsFullUpdateAsync(int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var envelope = await ReadEnvelopeAsync(
                        Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                    if (envelope.TryGetProperty("type", out var type)
                        && type.GetString() == "UPDATE"
                        && envelope.TryGetProperty("full", out var full)
                        && full.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            return false;
        }

        public async Task DrainAsync(int quietMs = 100)
        {
            while (true)
            {
                try
                {
                    _ = await ReadEnvelopeAsync(quietMs);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task<JsonElement> ReadEnvelopeAsync(int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cts.Token);
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(text.ToString()).RootElement.Clone();
        }

        private static bool TryFindActionRejected(JsonElement envelope, out JsonElement entry)
        {
            entry = default;
            if (!envelope.TryGetProperty("type", out var type)
                || type.GetString() != "UPDATE"
                || !envelope.TryGetProperty("entries", out var entries))
            {
                return false;
            }

            foreach (var candidate in entries.EnumerateArray())
            {
                if (candidate[0].GetString() != AutotableConnectionManager.ActionRejectedKind)
                    continue;
                entry = candidate;
                return true;
            }
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "test complete",
                        CancellationToken.None);
                }
                catch
                {
                    // The server may already have completed the close handshake.
                }
            }
            _socket.Dispose();
        }
    }
}
