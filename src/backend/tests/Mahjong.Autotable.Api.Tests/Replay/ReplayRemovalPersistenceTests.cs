using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayRemovalPersistenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedVersionAdvancingSnapshot_RemovalKeepsCommittedCutAndRetainedAlias(bool afterCommit)
    {
        var fault = new ReplaySaveFault();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: fault);
        var (id, room) = await fixture.CreatePublicSeating(19, DealMode.Manual);
        await fixture.BindHumans(id);
        var before = await ReadSnapshotRow(fixture, id);
        var (_, _, beforeRows) = await fixture.ReadPrefix(id);
        var journal = Assert.IsType<ChangshaReplayJournal>(fixture.Instances()[id].ReplayJournal);

        fault.Arm(afterCommit);
        await fixture.Runtime.StartGameAsync(id, expectedVersion: before.StateVersion);

        var state = fixture.State(id);
        Assert.Equal(ChangshaPhase.RollingDice, state.Phase);
        Assert.True(state.StateVersion > before.StateVersion);
        Assert.Equal(ReplayOperation.StartGame, journal.Records()[^1].Operation);
        Assert.Equal(beforeRows.Length, journal.PersistedRecordSequence);
        Assert.Single(journal.Pending());
        var saved = await ReadSnapshotRow(fixture, id);
        var (_, _, savedRows) = await fixture.ReadPrefix(id);
        Assert.Equal(afterCommit ? ChangshaReplayStateCodec.Serialize(state) : before.StateJson, saved.StateJson);
        Assert.Equal(afterCommit ? state.StateVersion : before.StateVersion, saved.StateVersion);
        Assert.Equal(beforeRows.Length + (afterCommit ? 1 : 0), savedRows.Length);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "failed-start-snapshot",
            afterCommit,
            committedVersion = saved.StateVersion,
            runtimeVersion = state.StateVersion,
            persistedRows = savedRows.Length,
            pendingRecords = journal.Pending().Count
        }));

        await AssertRemovalCutAndRecovery(fixture, id, room, beforeRows);
    }

    [Fact]
    public async Task FailedRoundAdvancingSnapshot_RemovalPersistsHandAndRoundForRetainedAlias()
    {
        var fault = new ReplayBoundaryWriteFault();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: fault);
        var (id, room) = await fixture.CreatePublicSeating(7, DealMode.Manual, cap: 8);
        await fixture.BindHumans(id);
        await fixture.Runtime.StartGameAsync(id);

        // Commit hand four's acknowledged EndHand cut, then reject the real rotation's snapshot before commit.
        fault.BlockAfter(state => state.Phase == ChangshaPhase.EndHand && state.HandNumber == 4
            && state.HandResultContinuation is { } readyResult && readyResult.WaitingSeats(state).Length == 0);
        for (var step = 0; step < 512 && fixture.State(id).HandNumber < 5; step++)
        {
            var state = fixture.State(id);
            if (state.Phase == ChangshaPhase.RollingDice || ChangshaGameStateMachine.IsPickupPhase(state.Phase))
                await fixture.CompleteDeal(id);
            else if (state.Phase == ChangshaPhase.EndHand)
                await HandResultTestActions.AcknowledgeAll(fixture, id);
            else if (state.Phase == ChangshaPhase.AwaitingClaim)
                await fixture.PassRemaining(id);
            else
            {
                Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
                await fixture.Runtime.DiscardAsync(id, state.ActiveSeatIndex, state.Hands[state.ActiveSeatIndex].ConcealedTiles[0]);
            }
        }

        var advanced = fixture.State(id);
        Assert.Equal((5, 2, ChangshaPhase.RollingDice), (advanced.HandNumber, advanced.RoundNumber, advanced.Phase));
        var saved = await ReadSnapshotRow(fixture, id);
        var savedState = JsonSerializer.Deserialize<ChangshaGameState>(saved.StateJson, ChangshaReplayStateCodec.SnapshotJson)!;
        Assert.Equal((4, 1, ChangshaPhase.EndHand), (savedState.HandNumber, savedState.RoundNumber, savedState.Phase));
        Assert.True(savedState.RequireHandResultAcknowledgements);
        var continuation = Assert.IsType<ChangshaHandResultContinuation>(savedState.HandResultContinuation);
        Assert.Equal(new[] { 0, 1, 2, 3 }, continuation.RequiredSeats(savedState));
        Assert.Empty(continuation.WaitingSeats(savedState));
        Assert.Equal((savedState.StateVersion, 4, 1), (saved.StateVersion, saved.CurrentHandNumber, saved.CurrentRoundNumber));
        Assert.True(advanced.StateVersion > saved.StateVersion);
        var journal = Assert.IsType<ChangshaReplayJournal>(fixture.Instances()[id].ReplayJournal);
        Assert.Equal(ReplayOperation.RotateBanker, Assert.Single(journal.Pending()).Operation);
        var (savedEnvelope, _, savedRows) = await fixture.ReadPrefix(id);
        Assert.Equal(ReplayOperation.AcknowledgeHandResult, savedEnvelope.Records[^1].Operation);
        Assert.Equal(continuation.ResultToken, savedEnvelope.Records[^1].Inputs.ResultToken);
        Assert.Equal(ReplayOperation.HandleWallExhausted,
            savedEnvelope.Records.Last(record => record.Observations.Events.Count > 0).Operation);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "failed-round-snapshot",
            committed = new { saved.StateVersion, saved.CurrentHandNumber, saved.CurrentRoundNumber },
            runtime = new { advanced.StateVersion, advanced.HandNumber, advanced.RoundNumber },
            pendingOperation = journal.Pending()[0].Operation.ToString()
        }));

        fault.Clear();
        await AssertRemovalCutAndRecovery(fixture, id, room, savedRows);
    }

    [Fact]
    public async Task MissingSnapshotRow_RemovalDoesNotRecreateSnapshotOrJournal()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await fixture.CreatePublicSeating(23, DealMode.Manual);
        await fixture.BindHumans(id);
        await fixture.Runtime.StartGameAsync(id);
        Assert.True(fixture.State(id).StateVersion > 0);
        var gid = Guid.Parse(id);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangshaGames.Remove(await db.ChangshaGames.SingleAsync(game => game.Id == gid));
            await db.SaveChangesAsync();
            Assert.False(await db.ChangshaGames.AnyAsync());
            Assert.False(await db.ChangshaGameEvents.AnyAsync());
        }

        await fixture.Runtime.RemoveGameAsync(id);

        Assert.False(fixture.Runtime.TryGetSnapshot(id, out _));
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.ChangshaGames.AnyAsync());
            Assert.False(await db.ChangshaGameEvents.AnyAsync());
            Assert.False(await db.ChangshaGameReplays.AnyAsync());
            Assert.Equal(gid, (await db.AutotableRoomBindings.SingleAsync()).RuntimeGameId);
        }
        await fixture.Restart(eager: false);
        var failure = await Assert.ThrowsAsync<PublicRoomRecoveryException>(() => fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal("room-snapshot-unavailable", failure.Reason);
        Assert.Equal(0, fixture.Runtime.GameCount);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "missing-row-removal",
            snapshotRows = 0,
            journalRows = 0,
            aliasRetained = true,
            recoveryReason = failure.Reason
        }));
    }

    private async Task AssertRemovalCutAndRecovery(
        ReplayRuntimeFixture fixture, string id, string room, ChangshaGameEvent[] committedPrefix)
    {
        var state = fixture.State(id);
        var journal = Assert.IsType<ChangshaReplayJournal>(fixture.Instances()[id].ReplayJournal);
        var pendingBeforeRemoval = journal.Pending().Count;
        await fixture.Runtime.RemoveGameAsync(id);
        Assert.False(fixture.Runtime.TryGetSnapshot(id, out _));
        Assert.True(state.IsGameComplete);
        Assert.Equal(ChangshaPhase.GameComplete, state.Phase);

        var row = await ReadSnapshotRow(fixture, id);
        var (removed, json, rows) = await fixture.ReadPrefix(id, ReplayCutKind.Removed);
        var snapshot = JsonSerializer.Deserialize<ChangshaGameState>(json, ChangshaReplayStateCodec.SnapshotJson)!;
        Assert.Equal(ChangshaReplayStateCodec.Serialize(state), json);
        Assert.Equal(ChangshaReplayStateCodec.Checkpoint(snapshot), removed.Final);
        Assert.Equal(committedPrefix.Select(entry => entry.Detail), rows.Take(committedPrefix.Length).Select(entry => entry.Detail));
        Assert.Equal(Enumerable.Range(1, rows.Length).Select(sequence => (long)sequence), rows.Select(entry => entry.Sequence));
        Assert.Equal(ReplayOperation.MarkRemoved, removed.Records[^1].Operation);
        Assert.Single(removed.Records, record => record.Operation == ReplayOperation.MarkRemoved);
        Assert.Single(removed.Records, record => record.Inputs.Initialization is not null);
        Assert.Equal(rows.Length, journal.PersistedRecordSequence);
        Assert.Empty(journal.Pending());
        foreach (var (entry, record) in rows.Zip(removed.Records))
        {
            Assert.Equal(record.After.StateVersion, entry.StateVersion);
            Assert.Equal(record.After.HandNumber, entry.HandNumber);
            Assert.Equal(record.After.TurnNumber, entry.TurnNumber);
        }
        var verified = ChangshaFullStateReplayVerifier.Verify(removed);
        Assert.True(verified.Success, verified.Detail);
        Assert.Equal(json, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));

        await fixture.Restart(eager: false);
        Assert.Equal(0, fixture.Runtime.GameCount);
        string? restoredId = null;
        var recoveryFailure = await Record.ExceptionAsync(async () =>
        {
            restoredId = await fixture.Runtime.RestorePublicRoomAsync(room);
        });
        output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "removed-cut-and-lazy-recovery",
            columnCut = new { row.StateVersion, handNumber = row.CurrentHandNumber, roundNumber = row.CurrentRoundNumber },
            stateCut = new { snapshot.StateVersion, handNumber = snapshot.HandNumber, roundNumber = snapshot.RoundNumber },
            snapshotSha256 = ChangshaReplayStateCodec.Hash(json),
            journalHead = removed.Final,
            journalRows = rows.Length,
            pendingBeforeRemoval,
            replayVerified = verified.Success,
            recoveryReason = (recoveryFailure as PublicRoomRecoveryException)?.Reason,
            recoveryException = recoveryFailure?.GetType().Name,
            retainedRuntimeId = restoredId == id
        }));
        Assert.Equal((snapshot.StateVersion, snapshot.HandNumber, snapshot.RoundNumber),
            (row.StateVersion, row.CurrentHandNumber, row.CurrentRoundNumber));
        Assert.Null(recoveryFailure);
        Assert.Equal(id, restoredId);
        Assert.Equal(1, fixture.Runtime.GameCount);
        Assert.Equal(json, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        Assert.Equal(json, ChangshaReplayStateCodec.Serialize(fixture.State(id)));

        var (recovered, recoveredJson, recoveredRows) = await fixture.ReadPrefix(id);
        Assert.Equal(json, recoveredJson);
        Assert.Equal(rows.Select(entry => entry.Detail), recoveredRows.Take(rows.Length).Select(entry => entry.Detail));
        Assert.Equal(rows.Length + 2, recoveredRows.Length);
        Assert.Equal(ReplayOperation.SnapshotRoundTrip, recovered.Records[^2].Operation);
        Assert.Equal(ReplayOperation.BindAuthoritativeGameId, recovered.Records[^1].Operation);
        var recoveredVerification = ChangshaFullStateReplayVerifier.Verify(recovered);
        Assert.True(recoveredVerification.Success, $"{recoveredVerification.Status}: {recoveredVerification.Detail}");
        Assert.Equal(json, ChangshaReplayStateCodec.Serialize(recoveredVerification.ReconstructedState!));
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(Guid.Parse(id), (await db.AutotableRoomBindings.SingleAsync(binding => binding.RoomId == room)).RuntimeGameId);
        Assert.Equal(1, await db.ChangshaGames.CountAsync());
    }

    private static async Task<ChangshaGame> ReadSnapshotRow(ReplayRuntimeFixture fixture, string id)
    {
        using var scope = fixture.Services.CreateScope();
        var gid = Guid.Parse(id);
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangshaGames
            .AsNoTracking().SingleAsync(game => game.Id == gid);
    }
}
