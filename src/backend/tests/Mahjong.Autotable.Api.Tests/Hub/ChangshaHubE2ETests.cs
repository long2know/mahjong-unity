using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Hub;

[Collection("ChangshaHubE2E")]
public sealed class ChangshaHubE2ETests
{
    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Predicate not satisfied within timeout.");
    }

    [Fact]
    [Trait("Category", "ChangshaHubE2E")]
    public async Task E2E1_AllBots_PlaysAtLeastOneHandAndCompletes()
    {
        await using var harness = new ChangshaHubTestHarness();
        var conn = await harness.ConnectAsync();

        // Create with seed (all 4 bots), start.
        var createResult = await conn.InvokeAsync<CreateGameResult>("CreateGame", "changsha-v1", new int[] { 0, 1, 2, 3 }, 12345);
        Assert.False(string.IsNullOrEmpty(createResult.GameId));

        await conn.InvokeAsync("StartGame", createResult.GameId);

        // Wait for either a WinDeclared OR a ScoringComplete with isDraw=true.
        await WaitForAsync(() =>
            harness.EventsOfType("WinDeclared").Any() ||
            harness.EventsOfType("ScoringComplete").Any(),
            TimeSpan.FromSeconds(60));

        // Sanity: the deal happened and at least one turn started.
        Assert.NotEmpty(harness.EventsOfType("GameStarted"));
        Assert.NotEmpty(harness.EventsOfType("DiceRolled"));
        Assert.NotEmpty(harness.EventsOfType("TilesDealt"));
        Assert.NotEmpty(harness.EventsOfType("TurnStarted"));
    }

    [Fact]
    [Trait("Category", "ChangshaHubE2E")]
    public async Task E2E2_HumanSeat_DiscardOpensClaimWindowOrAdvances()
    {
        await using var harness = new ChangshaHubTestHarness();
        var conn = await harness.ConnectAsync();

        // Human at seat 0, bots at 1/2/3
        var createResult = await conn.InvokeAsync<CreateGameResult>("CreateGame", "changsha-v1", new int[] { 1, 2, 3 }, 7777);
        await conn.InvokeAsync<TakeSeatResult>("TakeSeat", createResult.GameId, 0);
        await conn.InvokeAsync("StartGame", createResult.GameId);
        await conn.InvokeAsync("AcknowledgeDeal", createResult.GameId, 0);

        // Wait for our turn (seat 0).
        await WaitForAsync(() =>
            harness.EventsOfType("TurnStarted").Any(), TimeSpan.FromSeconds(10));

        // Inspect runtime state directly to discover dealer's tile to discard.
        var runtime = (Mahjong.Autotable.Api.Changsha.Runtime.IChangshaGameRuntime)
            harness.Factory.Services.GetService(typeof(Mahjong.Autotable.Api.Changsha.Runtime.IChangshaGameRuntime))!;
        Assert.True(runtime.TryGetSnapshot(createResult.GameId, out var state));
        Assert.NotNull(state);
        var hand = state!.Hands.Single(h => h.SeatIndex == 0);
        Assert.NotEmpty(hand.ConcealedTiles);

        var tileId = hand.ConcealedTiles[0];
        await conn.InvokeAsync("Discard", createResult.GameId, 0, tileId);

        // The TileDiscarded event must fire shortly thereafter.
        await WaitForAsync(() => harness.EventsOfType("TileDiscarded").Any(), TimeSpan.FromSeconds(10));
        Assert.NotEmpty(harness.EventsOfType("TileDiscarded"));
    }

    [Fact]
    [Trait("Category", "ChangshaHubE2E")]
    public async Task E2E3_Reconnect_ReceivesFullStateSnapshot()
    {
        await using var harness = new ChangshaHubTestHarness();
        var playerId = $"reconnect-owner-{Guid.NewGuid():N}";
        var conn1 = await harness.ConnectAsync(playerId);

        var createResult = await conn1.InvokeAsync<CreateGameResult>("CreateGame", "changsha-v1", new int[] { 1, 2, 3 }, 9001);
        await conn1.InvokeAsync<TakeSeatResult>("TakeSeat", createResult.GameId, 0);
        await conn1.InvokeAsync("StartGame", createResult.GameId);

        // Wait for the turn to start.
        await WaitForAsync(() => harness.EventsOfType("TurnStarted").Any(), TimeSpan.FromSeconds(10));

        // Disconnect first connection.
        var runtime = harness.Factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var previousConnectionId = Assert.IsType<string>(conn1.ConnectionId);
        await conn1.DisposeAsync();
        await WaitForAsync(() => runtime.TryGetSeatForConnection(createResult.GameId, previousConnectionId) is null);

        // Reconnect on a new connection.
        var conn2 = await harness.ConnectAsync(playerId);
        Assert.NotEqual(previousConnectionId, conn2.ConnectionId);
        var fullStateBefore = harness.EventsOfType("FullState").Count();
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = conn2.On<JsonElement>("FullState", payload => received.TrySetResult(payload.Clone()));

        var ok = await conn2.InvokeAsync<ReconnectResult>("ReconnectGame", createResult.GameId, 0);
        Assert.True(ok.Success);

        // FullState event must arrive on conn2.
        await WaitForAsync(
            () => harness.EventsOfType("FullState").Count() > fullStateBefore,
            TimeSpan.FromSeconds(5));
        var snapshot = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(createResult.GameId, snapshot.GetProperty("gameId").GetString());
        Assert.Equal(0, runtime.TryGetSeatForConnection(createResult.GameId, conn2.ConnectionId!));
        var state = await runtime.TryGetSnapshotCopyAsync(createResult.GameId);
        Assert.Equal(playerId, state!.Seats[0].PlayerId);
        var seats = snapshot.GetProperty("seats").EnumerateArray().ToArray();
        var own = Assert.Single(seats, seat => seat.GetProperty("seatIndex").GetInt32() == 0);
        Assert.Equal(state.Hands[0].ConcealedTiles,
            own.GetProperty("concealedTiles").EnumerateArray().Select(tile => tile.GetInt32()));
        Assert.All(seats.Where(seat => seat.GetProperty("seatIndex").GetInt32() != 0), seat =>
            Assert.True(!seat.TryGetProperty("concealedTiles", out var tiles) || tiles.ValueKind == JsonValueKind.Null));
    }

    private sealed record CreateGameResult(string GameId);
    private sealed record TakeSeatResult(bool Success, int SeatIndex);
    private sealed record ReconnectResult(bool Success);
}
