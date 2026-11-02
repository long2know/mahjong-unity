using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayV3CompletionBoundaryTests
{
    [Fact]
    public async Task InstanceDisposal_CancelsAndDrainsCompletionOnce()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(1);
        var instance = new ChangshaGameInstance(state.GameId, state);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = instance.QueueCompletionEffects(async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled.TrySetResult(); }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = instance.DisposeAsync().AsTask();
        var second = instance.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        Assert.True(completion.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => instance.QueueCompletionEffects(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task CompletionFault_IsObservedByIdempotentDisposal()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(2);
        var instance = new ChangshaGameInstance(state.GameId, state);
        var completion = instance.QueueCompletionEffects(_ => Task.FromException(new InvalidOperationException("completion-fault")));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => instance.DisposeAsync().AsTask());
        Assert.Equal("completion-fault", failure.Message);
        Assert.True(completion.IsFaulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => instance.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalOrRuntimeDisposal_CancelsRealPendingProfileWork(bool disposeRuntime)
    {
        var gate = new ProfileCompletionGate(false);
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate, profileCompletion: true);
        try
        {
            var branch = ReplayReachableBranches.Get("discard-hu");
            var (id, _) = await ReplayV3RecoveryIntegrationTests.DriveRuntimePrefix(
                fixture, branch, stopBeforeLast: true, cap: 1);
            await fixture.Runtime.ClaimAsync(id, branch.Steps[^1].Inputs.SeatIndex!.Value, "Hu", null);
            if (fixture.State(id).ClaimWindow is not null)
                await fixture.PassRemaining(id, branch.Steps[^1].Inputs.SeatIndex);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var instance = fixture.Instances()[id];
            var completion = instance.CompletionEffectsTask!;
            Assert.False(completion.IsCompleted);
            if (disposeRuntime)
                await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            else
                await fixture.Runtime.RemoveGameAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(gate.Cancelled.Task.IsCompletedSuccessfully);
            Assert.True(completion.IsCompletedSuccessfully);
            Assert.False(fixture.Runtime.TryGetSnapshot(id, out _));
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task DeferredHistory_UsesTheImmutableTerminalOwners()
    {
        var gate = new ProfileCompletionGate(false);
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate, profileCompletion: true);
        try
        {
            var branch = ReplayReachableBranches.Get("discard-hu");
            var (id, _) = await ReplayV3RecoveryIntegrationTests.DriveRuntimePrefix(
                fixture, branch, stopBeforeLast: true, cap: 1, aliasedRoom: false);
            var seat = branch.Steps[^1].Inputs.SeatIndex!.Value;
            await fixture.Runtime.ClaimAsync(id, seat, "Hu", null);
            if (fixture.State(id).ClaimWindow is not null) await fixture.PassRemaining(id, seat);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var terminalOwners = fixture.State(id).Seats.Select(s => s.PlayerId).Order().ToArray();
            var instance = fixture.Instances()[id];
            var original = fixture.State(id).Seats[seat].PlayerId;
            await fixture.Runtime.HandleDisconnectAsync(original, $"{id}-connection-{seat}");
            Assert.Equal(seat, await fixture.Runtime.TakeSeatAsync(id, "late-completion-owner", $"{id}-late", seat));
            Assert.Equal("late-completion-owner", instance.State.Seats[seat].PlayerId);
            Assert.False(instance.CompletionEffectsTask!.IsCompleted);
            gate.Release.TrySetResult();
            await instance.CompletionEffectsTask!.WaitAsync(TimeSpan.FromSeconds(5));
            using var scope = fixture.Services.CreateScope();
            var gid = Guid.Parse(id);
            var history = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PlayerGameHistory
                .AsNoTracking().Where(row => row.GameId == gid).ToArrayAsync();
            Assert.Equal(terminalOwners, history.Select(row => row.PlayerId).Order());
            Assert.DoesNotContain(history, row => row.PlayerId == "late-completion-owner");
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalProfileWrite_CannotHoldCompletionOrTheGameLock(bool failWrite)
    {
        var gate = new ProfileCompletionGate(failWrite);
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate, profileCompletion: true);
        var branch = ReplayReachableBranches.Get("discard-hu");
        try
        {
            var (id, _) = await ReplayV3RecoveryIntegrationTests.DriveRuntimePrefix(
                fixture, branch, stopBeforeLast: true, cap: 1);
            var winner = branch.Steps[^1].Inputs.SeatIndex!.Value;
            var completion = fixture.Runtime.ClaimAsync(id, winner, "Hu", null);
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (fixture.State(id).ClaimWindow is not null) await fixture.PassRemaining(id, winner);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var instance = fixture.Instances()[id];
            Assert.NotNull(instance.CompletionEffectsTask);
            Assert.False(instance.CompletionEffectsTask.IsCompleted);
            Assert.Equal(1, instance.Lock.CurrentCount);
            var state = await fixture.Runtime.TryGetSnapshotCopyAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(state!.IsGameComplete);
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gid = Guid.Parse(id);
            var row = await db.ChangshaGameReplays.AsNoTracking().SingleAsync(r => r.GameId == gid);
            var verified = ChangshaFullStateReplayVerifier.VerifyJson(row.EventsJson);
            Assert.True(verified.Success, verified.Detail);
            Assert.Equal(ChangshaReplayStateCodec.Serialize(state),
                ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
            Assert.Contains("GameCompleted", fixture.Hub.Methods);
            Assert.Same(instance.CompletionEffectsTask, instance.QueueCompletionEffects(
                _ => throw new InvalidOperationException("Completion work must be scheduled once.")));
        }
        finally
        {
            gate.Release.TrySetResult();
            foreach (var instance in fixture.Instances().Values)
                if (instance.CompletionEffectsTask is { } effects)
                    await effects.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var finalScope = fixture.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorded = await finalDb.PlayerStats.AsNoTracking().ToArrayAsync();
        if (failWrite) Assert.Empty(recorded);
        else
        {
            Assert.Equal(4, recorded.Length);
            Assert.All(recorded, stats => Assert.Equal(1, stats.GamesPlayed));
        }
    }
}

internal sealed class ProfileCompletionGate(bool failWrite) : SaveChangesInterceptor
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<PlayerStats>().Any(entry => entry.Entity.GamesPlayed > 0))
        {
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            if (failWrite) throw new DbUpdateException("Controlled best-effort profile-write failure.");
        }
        return result;
    }
}
