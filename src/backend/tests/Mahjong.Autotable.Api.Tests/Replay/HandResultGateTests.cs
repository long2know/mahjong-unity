using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Replay;

public sealed class HandResultGateTests
{
    [Theory]
    [InlineData("self-draw")]
    [InlineData("discard-hu")]
    [InlineData("kong-replacement-hu")]
    public async Task SettledHumanWin_HoldsUntilEveryHumanAcknowledges(string branchName)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, _) = await ReplayV3RecoveryIntegrationTests.DriveRuntimePrefix(
            fixture, ReplayReachableBranches.Get(branchName), cap: 8);
        var state = fixture.State(id);
        Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        Assert.NotNull(state.CurrentScore);
        Assert.NotNull(state.CurrentWin);
        var result = Assert.IsType<ChangshaHandResultContinuation>(state.HandResultContinuation);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.WaitingSeats(state));
        var stable = Settlement(state);
        var heldHand = state.HandNumber;
        Assert.True(heldHand < state.MaxHands);
        var winner = state.CurrentWin.WinningSeatIndex;
        var beforeRotations = state.EventLog.Count(entry => entry.EventType == "banker-rotated");

        await fixture.Runtime.EnableHandResultAcknowledgementsAsync(id);
        await fixture.Runtime.AcknowledgeDealAsync(id, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.StartGameAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.RollDiceAsync(id, state.DealerSeatIndex));
        await InvokeContinuation(fixture, id);
        Assert.Equal(stable, Settlement(state));
        for (var seat = 0; seat < 3; seat++)
        {
            await HandResultTestActions.Acknowledge(fixture, id, seat);
            Assert.Equal(stable, Settlement(state));
            Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        }

        var duplicate = await Assert.ThrowsAsync<HandResultAcknowledgementException>(
            () => HandResultTestActions.Acknowledge(fixture, id, 0));
        Assert.Equal("hand-result-already-acknowledged", duplicate.Reason);
        await AssertPrefix(fixture, id);
        await HandResultTestActions.Acknowledge(fixture, id, 3);

        Assert.Equal(heldHand + 1, state.HandNumber);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(winner, state.DealerSeatIndex);
        Assert.Null(state.HandResultContinuation);
        Assert.Null(state.CurrentWin);
        Assert.Null(state.CurrentScore);
        Assert.Equal(beforeRotations + 1, state.EventLog.Count(entry => entry.EventType == "banker-rotated"));
        Assert.Equal(new[] { 13, 13, 13, 14 }, state.Hands.Select(hand => hand.ConcealedTiles.Count).Order());
        await AssertPrefix(fixture, id);
        var stale = await Assert.ThrowsAsync<HandResultAcknowledgementException>(() =>
            fixture.Runtime.AcknowledgeHandResultAsync(id, "replay-human-3", $"{id}-connection-3",
                result.HandNumber, result.ResultToken));
        Assert.Equal("stale-hand-result", stale.Reason);
        Assert.Equal(heldHand + 1, state.HandNumber);
    }

    [Theory]
    [InlineData(DealMode.Auto)]
    [InlineData(DealMode.Manual)]
    public async Task Washout_TwoHumansGateAndConcurrentContinueAdvancesExactlyOnce(DealMode mode)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, _) = await CreateTwoHumanTable(fixture, mode);
        await DrainHand(fixture, id);
        var state = fixture.State(id);
        var gate = Assert.IsType<ChangshaHandResultContinuation>(state.HandResultContinuation);
        Assert.Equal(new[] { 0, 1 }, gate.RequiredSeats(state));
        Assert.Equal("Draw", ChangshaToAutotableTranslator.BuildHandResult(state).Type);
        Assert.Empty(state.Wall);
        Assert.Null(state.CurrentScore);
        var stable = Settlement(state);
        await fixture.Runtime.AcknowledgeDealAsync(id, 0);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        await InvokeContinuation(fixture, id);
        await InvokeBotSchedule(fixture, id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Runtime.TakeTilesFromWallAsync(id, state.DealerSeatIndex, 4));
        Assert.Equal(stable, Settlement(state));
        var wrongConnection = await Assert.ThrowsAsync<HandResultAcknowledgementException>(() =>
            fixture.Runtime.AcknowledgeHandResultAsync(id, "replay-human-0", "same-player-other-transport",
                gate.HandNumber, gate.ResultToken));
        Assert.Equal("connection-owns-no-seat", wrongConnection.Reason);
        Assert.Empty(gate.AcknowledgedSeats);

        await Task.WhenAll(HandResultTestActions.Acknowledge(fixture, id, 0),
            HandResultTestActions.Acknowledge(fixture, id, 1));
        Assert.Equal(2, state.HandNumber);
        Assert.Single(state.EventLog, entry => entry.EventType == "banker-rotated");
        Assert.Equal(mode == DealMode.Manual ? ChangshaPhase.RollingDice : ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(mode == DealMode.Manual ? 1 : 2, state.EventLog.Count(entry => entry.EventType == "tiles-dealt"));
        Assert.Equal(mode == DealMode.Manual ? 1 : 2, state.EventLog.Count(entry => entry.EventType == "dice-rolled"));
        Assert.Null(state.HandResultContinuation);
        await AssertPrefix(fixture, id);
    }

    [Fact]
    public async Task ActualScheduledBotWin_HoldsForHumansInsteadOfSchedulingTheNextDeal()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var branch = ReplayReachableBranches.Get("self-draw");
        var (id, _) = await ReplayV3RecoveryIntegrationTests.DriveRuntimePrefix(fixture, branch, stopBeforeLast: true);
        var instance = fixture.Instances()[id];
        var winner = branch.Steps[^1].Inputs.SeatIndex!.Value;
        var heldHand = instance.State.HandNumber;
        var rotations = instance.State.EventLog.Count(entry => entry.EventType == "banker-rotated");
        Assert.True(ChangshaOwnTurnActions.Available(instance.State, winner)!.Hu);
        await instance.Lock.WaitAsync();
        try
        {
            instance.ReplayJournal!.Apply(instance.State, ReplayOperation.BindBotSeat, new() { SeatIndex = winner });
            instance.SeatConnections.TryRemove(winner, out _);
        }
        finally { instance.Lock.Release(); }
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Capture(string gameId, ChangshaGameState snapshot)
        {
            if (gameId == id && snapshot.Phase == ChangshaPhase.EndHand) settled.TrySetResult();
        }
        fixture.Runtime.StateChanged += Capture;
        try
        {
            await InvokeBotSchedule(fixture, id);
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A snapshot copy takes the runtime lock after the settlement mutation.
            var state = (await fixture.Runtime.TryGetSnapshotCopyAsync(id))!;
            Assert.Equal(winner, state.CurrentWin!.WinningSeatIndex);
            Assert.DoesNotContain(winner, state.HandResultContinuation!.RequiredSeats(state));
            Assert.Equal(3, state.HandResultContinuation.WaitingSeats(state).Length);
            Assert.Equal(heldHand, state.HandNumber);
            await InvokeContinuation(fixture, id);
            await InvokeBotSchedule(fixture, id);
            Assert.Equal(heldHand, fixture.State(id).HandNumber);
            Assert.Equal(rotations, fixture.State(id).EventLog.Count(entry => entry.EventType == "banker-rotated"));
            await AssertPrefix(fixture, id);
        }
        finally { fixture.Runtime.StateChanged -= Capture; }
    }

    [Theory]
    [InlineData(false, DealMode.Auto)]
    [InlineData(true, DealMode.Manual)]
    public async Task DisconnectedHumanAndRestart_RetainAcknowledgementsAndResult(bool eager, DealMode mode)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await CreateTwoHumanTable(fixture, mode);
        await DrainHand(fixture, id);
        await HandResultTestActions.Acknowledge(fixture, id, 0);
        var before = fixture.State(id);
        var token = before.HandResultContinuation!.ResultToken;
        var stable = Settlement(before);
        await fixture.Runtime.HandleDisconnectAsync("replay-human-1", $"{id}-connection-1");
        Assert.Equal(new[] { 1 }, before.HandResultContinuation.WaitingSeats(before));
        Assert.False(await fixture.Runtime.ReconnectAsync(id, 1, "forged-owner", "observer"));
        await fixture.Restart(eager);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        await fixture.Runtime.EnableHandResultAcknowledgementsAsync(id);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        var restored = fixture.State(id);
        Assert.Equal(stable, Settlement(restored));
        Assert.Equal(token, restored.HandResultContinuation!.ResultToken);
        Assert.Equal(new[] { 0 }, restored.HandResultContinuation.AcknowledgedSeats);
        Assert.Equal(new[] { 1 }, restored.HandResultContinuation.WaitingSeats(restored));
        Assert.Null(fixture.Runtime.TryGetSeatForConnection(id, $"{id}-connection-1"));
        Assert.True(await fixture.Runtime.ReconnectAsync(id, 1, "replay-human-1", "returning-owner"));
        Assert.False(await fixture.Runtime.ReconnectAsync(id, 1, "replay-human-1", "duplicate-tab"));
        await AssertPrefix(fixture, id);
        await fixture.Runtime.AcknowledgeHandResultAsync(id, "replay-human-1", "returning-owner", 1, token);
        Assert.Equal(2, restored.HandNumber);
        Assert.Single(restored.EventLog, entry => entry.EventType == "banker-rotated");
        await AssertPrefix(fixture, id);
    }

    [Theory]
    [InlineData(DealMode.Auto)]
    [InlineData(DealMode.Manual)]
    public async Task FinalWashout_EmitsCompletionWithoutAnyResultAcknowledgement(DealMode mode)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, _) = await CreateTwoHumanTable(fixture, mode, cap: 1);
        await DrainHand(fixture, id);
        var state = fixture.State(id);
        Assert.True(state.IsGameComplete);
        Assert.Equal(ChangshaPhase.GameComplete, state.Phase);
        Assert.Null(state.HandResultContinuation);
        Assert.Contains("GameCompleted", fixture.Hub.Methods);
        Assert.Equal(1, fixture.Hub.Methods.Count(method => method == "GameCompleted"));
        Assert.Single(state.EventLog, entry => entry.EventType == "tiles-dealt");
        await InvokeContinuation(fixture, id);
        Assert.Equal(2, state.HandNumber); // Existing terminal counter points past the played hand.
        await AssertPrefix(fixture, id, ReplayCutKind.NaturalCompletion);
    }

    [Fact]
    public async Task OldBrowserSnapshot_IsEnabledBeforeResume_WithoutInventingHistoricalReplay()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await CreateTwoHumanTable(fixture, DealMode.Auto);
        await DrainHand(fixture, id);
        var original = Settlement(fixture.State(id));
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gameId = Guid.Parse(id);
            var row = await db.ChangshaGames.SingleAsync(game => game.Id == gameId);
            var old = JsonNode.Parse(row.StateJson)!.AsObject();
            old.Remove("requireHandResultAcknowledgements");
            old.Remove("handResultContinuation");
            row.StateJson = old.ToJsonString(ChangshaReplayStateCodec.SnapshotJson);
            // A pre-journal snapshot, not a damaged purportedly verified prefix.
            db.ChangshaGameEvents.RemoveRange(await db.ChangshaGameEvents.Where(entry => entry.GameId == gameId).ToListAsync());
            await db.SaveChangesAsync();
        }
        await fixture.Restart(eager: false);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.False(fixture.State(id).RequireHandResultAcknowledgements);
        Assert.Null(fixture.Instances()[id].ReplayJournal);
        await fixture.Runtime.EnableHandResultAcknowledgementsAsync(id);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        Assert.True(fixture.State(id).RequireHandResultAcknowledgements);
        Assert.Equal(new[] { 0, 1 }, fixture.State(id).HandResultContinuation!.WaitingSeats(fixture.State(id)));
        Assert.Equal(original, Settlement(fixture.State(id)));
        Assert.Null(fixture.Instances()[id].ReplayJournal);
    }

    [Fact]
    public async Task NativeUnaliasedLegacyGame_KeepsAutomaticContinuationUnlessEnabled()
    {
        await using var fixture = await ReplayRuntimeFixture.Create(persistSnapshots: false);
        var id = await fixture.CreateHumans(23, manual: false);
        Assert.False(fixture.State(id).RequireHandResultAcknowledgements);
        await DrainHand(fixture, id);
        Assert.Equal(2, fixture.State(id).HandNumber);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, fixture.State(id).Phase);
        Assert.Null(fixture.State(id).HandResultContinuation);
        await fixture.Runtime.EnableHandResultAcknowledgementsAsync(id);
        await DrainHand(fixture, id);
        Assert.Equal(2, fixture.State(id).HandNumber);
        Assert.Equal(ChangshaPhase.EndHand, fixture.State(id).Phase);
    }

    [Fact]
    public async Task CompletedLegacyGame_IsNotReopenedOrAppendedToByBrowserEnablement()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await fixture.CreateHumans(23, manual: false, cap: 1);
        await DrainHand(fixture, id);
        Assert.True(fixture.State(id).IsGameComplete);
        var original = ChangshaReplayStateCodec.Serialize(fixture.State(id));
        var count = fixture.Instances()[id].ReplayJournal!.Count;
        await fixture.Runtime.EnableHandResultAcknowledgementsAsync(id);
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        Assert.Equal(count, fixture.Instances()[id].ReplayJournal!.Count);
        await AssertPrefix(fixture, id, ReplayCutKind.NaturalCompletion);
    }

    [Fact]
    public async Task CommittedLastAcknowledgement_ResumesOneRotationWithoutAcknowledgingAgain()
    {
        var fault = new ReplayBoundaryWriteFault();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: fault);
        var (id, room) = await CreateTwoHumanTable(fixture, DealMode.Auto);
        await DrainHand(fixture, id);
        fault.BlockAfter(state => state.Phase == ChangshaPhase.EndHand
            && state.HandResultContinuation?.AcknowledgedSeats.Count == 2);
        await HandResultTestActions.AcknowledgeAll(fixture, id);
        Assert.Equal(2, fixture.State(id).HandNumber);
        var (_, savedJson, _) = await fixture.ReadPrefix(id);
        var saved = JsonSerializer.Deserialize<ChangshaGameState>(savedJson, ChangshaReplayStateCodec.SnapshotJson)!;
        Assert.Equal(ChangshaPhase.EndHand, saved.Phase);
        Assert.Empty(saved.HandResultContinuation!.WaitingSeats(saved));
        fault.Clear();
        await fixture.Restart(eager: false);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        await fixture.Runtime.EnableHandResultAcknowledgementsAsync(id);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        Assert.Equal(2, fixture.State(id).HandNumber);
        Assert.Single(fixture.State(id).EventLog, entry => entry.EventType == "banker-rotated");
        Assert.Null(fixture.State(id).HandResultContinuation);
        await AssertPrefix(fixture, id);
    }

    [Fact]
    public async Task RetiringAHeldTable_DrainsWithoutWaitingForHumanAcknowledgements()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, _) = await CreateTwoHumanTable(fixture, DealMode.Manual);
        await DrainHand(fixture, id);
        var state = fixture.State(id);
        Assert.NotNull(state.HandResultContinuation);
        await fixture.Runtime.RemoveGameAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Runtime.TryGetSnapshot(id, out _));
        Assert.Null(state.HandResultContinuation);
        Assert.Equal(1, state.HandNumber);
        Assert.DoesNotContain(state.EventLog, entry => entry.EventType == "banker-rotated");
        await AssertPrefix(fixture, id, ReplayCutKind.Removed);
    }

    private static async Task<(string Id, string Room)> CreateTwoHumanTable(
        ReplayRuntimeFixture fixture, DealMode mode, int cap = 4)
    {
        // Drive a conserved legal game deterministically; unrelated bot/claim clocks do
        // not choose moves for this test. The scheduled-bot test exercises the real clock.
        var options = fixture.Services.GetRequiredService<IOptions<ChangshaRuntimeOptions>>().Value;
        options.BotTurnDelayMs = options.BotClaimDelayMs = options.BotPickupDelayMs = 30_000;
        options.ClaimWindowTimeoutMs = 30_000;
        var room = $"result-hold-{Guid.NewGuid():N}";
        var id = await fixture.Runtime.CreateGameAsync(23, [2, 3], "replay-human-0", null,
            maxHands: cap, publicRoom: new(room, mode, "medium"));
        fixture.Track(id);
        for (var seat = 0; seat < 2; seat++)
            await fixture.Runtime.TakeSeatAsync(id, $"replay-human-{seat}", $"{id}-connection-{seat}", seat);
        await fixture.Runtime.StartGameAsync(id);
        await fixture.CompleteDeal(id);
        return (id, room);
    }

    private static async Task DrainHand(ReplayRuntimeFixture fixture, string id)
    {
        var hand = fixture.State(id).HandNumber;
        for (var step = 0; step < 512 && fixture.State(id).HandNumber == hand
            && fixture.State(id).Phase != ChangshaPhase.EndHand && !fixture.State(id).IsGameComplete; step++)
        {
            var state = fixture.State(id);
            if (state.Phase == ChangshaPhase.AwaitingClaim) await fixture.PassRemaining(id);
            else
            {
                Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
                await fixture.Runtime.DiscardAsync(id, state.ActiveSeatIndex, state.Hands[state.ActiveSeatIndex].ConcealedTiles[0]);
            }
        }
        Assert.True(fixture.State(id).HandNumber != hand || fixture.State(id).Phase == ChangshaPhase.EndHand
            || fixture.State(id).IsGameComplete);
    }

    private static string Settlement(ChangshaGameState state) => JsonSerializer.Serialize(new
    {
        state.HandNumber, state.HandInRound, state.RoundNumber, state.DealerSeatIndex,
        state.Wall, state.WallDrawIndex, state.WallBackIndex, state.WallBackDrawn,
        state.Hands, state.CurrentWin, state.CurrentScore, state.CumulativeScores,
        state.EventSequence, state.LastDiceRoll, state.BreakPoint
    });

    private static Task InvokeContinuation(ReplayRuntimeFixture fixture, string id) =>
        Assert.IsAssignableFrom<Task>(typeof(ChangshaGameRuntime)
            .GetMethod("StartNextHandOrEndAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Runtime, [fixture.Instances()[id], CancellationToken.None, null, null]));

    private static Task InvokeBotSchedule(ReplayRuntimeFixture fixture, string id) =>
        Assert.IsAssignableFrom<Task>(typeof(ChangshaGameRuntime)
            .GetMethod("ScheduleBotIfNeededAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Runtime, [fixture.Instances()[id], CancellationToken.None]));

    private static async Task AssertPrefix(ReplayRuntimeFixture fixture, string id,
        ReplayCutKind kind = ReplayCutKind.Checkpoint)
    {
        var (envelope, actual, _) = await fixture.ReadPrefix(id, kind);
        var verified = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(verified.Success, $"{verified.Status}: {verified.Detail}");
        Assert.Equal(actual, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
    }
}

internal static class HandResultTestActions
{
    internal static Task Acknowledge(ReplayRuntimeFixture fixture, string id, int seat)
    {
        var instance = fixture.Instances()[id];
        var gate = Assert.IsType<ChangshaHandResultContinuation>(instance.State.HandResultContinuation);
        return fixture.Runtime.AcknowledgeHandResultAsync(id, instance.State.Seats[seat].PlayerId,
            instance.SeatConnections[seat], gate.HandNumber, gate.ResultToken);
    }

    internal static async Task AcknowledgeAll(ReplayRuntimeFixture fixture, string id)
    {
        var state = fixture.State(id);
        foreach (var seat in state.HandResultContinuation!.WaitingSeats(state))
            await Acknowledge(fixture, id, seat);
    }
}
