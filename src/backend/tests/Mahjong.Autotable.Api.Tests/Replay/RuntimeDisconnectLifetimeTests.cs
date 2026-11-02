using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class RuntimeDisconnectLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private readonly ITestOutputHelper _output;

    public RuntimeDisconnectLifetimeTests(ITestOutputHelper output)
    {
        _output = output;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "loaded-assemblies",
            processId = Environment.ProcessId,
            framework = RuntimeInformation.FrameworkDescription,
            api = Loaded(typeof(ChangshaGameRuntime).Assembly),
            tests = Loaded(typeof(RuntimeDisconnectLifetimeTests).Assembly)
        }));
    }

    [Fact]
    public async Task Shutdown_WaitsForHeldDisconnectPersistence_BeforeDisposingItsSemaphore()
    {
        var gate = new SnapshotWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var (id, room) = await CreatePublicGame(fixture);
        var instance = fixture.Instances()[id];
        gate.Arm(id);
        var disconnect = fixture.Runtime.HandleDisconnectAsync("replay-owner", "owner-connection");
        await gate.Entered.Task.WaitAsync(Deadline);
        var shutdown = fixture.Runtime.DisposeAsync().AsTask();
        var returnedWhileHeld = shutdown.IsCompleted;
        gate.Release.TrySetResult();
        var disconnectFailure = await Record.ExceptionAsync(() => disconnect.WaitAsync(Deadline));
        var shutdownFailure = await Record.ExceptionAsync(() => shutdown.WaitAsync(Deadline));
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "held-disconnect-shutdown",
            returnedWhileHeld,
            disconnectFailure = disconnectFailure?.ToString(),
            shutdownFailure = shutdownFailure?.ToString(),
            gate = gate.Trace.ToArray()
        }));

        Assert.False(returnedWhileHeld);
        Assert.Null(disconnectFailure);
        Assert.Null(shutdownFailure);
        Assert.Equal("successor", instance.State.CreatorPlayerId);
        Assert.False(instance.SeatConnections.ContainsKey(0));
        await AssertStoredCut(fixture, instance);
        await fixture.Restart(eager: false);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal("successor", fixture.State(id).CreatorPlayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_WaitsForHeldOperation_EvenWhenItsOwnWaitIsCancelled(bool cancelRemoval)
    {
        var gate = new SnapshotWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var (id, _) = await CreatePublicGame(fixture);
        var instance = fixture.Instances()[id];
        gate.Arm(id);
        var operation = fixture.Runtime.SetGamePublicAsync(id, "replay-owner", true, "held-title");
        await gate.Entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        if (cancelRemoval) cancellation.Cancel();
        var removal = fixture.Runtime.RemoveGameAsync(id, cancellation.Token);
        var returnedWhileHeld = removal.IsCompleted;
        gate.Release.TrySetResult();
        var operationFailure = await Record.ExceptionAsync(() => operation.WaitAsync(Deadline));
        var removalFailure = await Record.ExceptionAsync(() => removal.WaitAsync(Deadline));
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "held-operation-removal", cancelRemoval, returnedWhileHeld,
            operationFailure = operationFailure?.ToString(),
            removalFailure = removalFailure?.ToString()
        }));

        Assert.False(returnedWhileHeld);
        Assert.Null(operationFailure);
        Assert.Null(removalFailure);
        Assert.False(fixture.Runtime.TryGetSnapshot(id, out _));
        Assert.Equal("held-title", instance.State.PublicName);
        Assert.Equal(!cancelRemoval, instance.State.IsGameComplete);
        await AssertStoredCut(fixture, instance,
            cancelRemoval ? ReplayCutKind.Checkpoint : ReplayCutKind.Removed);
    }

    [Fact]
    public async Task QueuedDisconnectsAcrossGames_DrainTheirHostTransferAndRemovalBeforeShutdown()
    {
        var gate = new SnapshotWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var first = await CreatePublicGame(fixture);
        var second = await CreatePublicGame(fixture);
        var instances = fixture.Instances().Values.ToArray();
        gate.Arm(fixture.Instances().First().Key);
        var ownerDisconnect = fixture.Runtime.HandleDisconnectAsync("replay-owner", "owner-connection");
        await gate.Entered.Task.WaitAsync(Deadline);
        var successorDisconnect = fixture.Runtime.HandleDisconnectAsync("successor", "successor-connection");
        Assert.False(successorDisconnect.IsCompleted);
        var shutdown = fixture.Runtime.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        gate.Release.TrySetResult();
        await Task.WhenAll(ownerDisconnect, successorDisconnect, shutdown).WaitAsync(Deadline);

        Assert.Equal(0, fixture.Runtime.GameCount);
        foreach (var instance in instances)
        {
            Assert.True(instance.State.IsGameComplete);
            Assert.Empty(instance.SeatConnections);
            await AssertStoredCut(fixture, instance, ReplayCutKind.Removed);
        }
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.AutotableRoomBindings.CountAsync());
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            stage = "queued-disconnects-drained",
            callbacksCompleted = ownerDisconnect.IsCompletedSuccessfully && successorDisconnect.IsCompletedSuccessfully,
            terminalGames = instances.Length,
            retainedAliases = new[] { first.Room, second.Room }
        }));
    }

    [Fact]
    public async Task HeldStartOperation_FinishesItsPostLockContinuationBeforeShutdown()
    {
        var gate = new SnapshotWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var (id, _) = await CreatePublicGame(fixture, DealMode.Auto);
        for (var seat = 2; seat < 4; seat++)
            await fixture.Runtime.TakeSeatAsync(id, $"start-human-{seat}", $"{id}-connection-{seat}", seat);
        Assert.True(fixture.Runtime.AreAllSeatsOccupied(id));
        var instance = fixture.Instances()[id];
        gate.Arm(id);
        var start = fixture.Runtime.StartGameAsync(id);
        await gate.Entered.Task.WaitAsync(Deadline);
        var shutdown = fixture.Runtime.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        gate.Release.TrySetResult();
        await Task.WhenAll(start, shutdown).WaitAsync(Deadline);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, instance.State.Phase);
        Assert.False(instance.State.IsGameComplete);
        Assert.True(instance.State.StateVersion > 0);
        await AssertStoredCut(fixture, instance);
    }

    [Fact]
    public async Task CancelledQueuedDisconnect_RemainsObservableAndDoesNotLeakShutdownOwnership()
    {
        var gate = new SnapshotWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var (id, _) = await CreatePublicGame(fixture);
        var instance = fixture.Instances()[id];
        gate.Arm(id);
        var operation = fixture.Runtime.SetGamePublicAsync(id, "replay-owner", true, "held-title");
        await gate.Entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var disconnect = fixture.Runtime.HandleDisconnectAsync("replay-owner", "owner-connection", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect.WaitAsync(Deadline));
        var shutdown = fixture.Runtime.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        gate.Release.TrySetResult();
        await Task.WhenAll(operation, shutdown).WaitAsync(Deadline);

        Assert.Equal("replay-owner", instance.State.CreatorPlayerId);
        Assert.Equal("owner-connection", instance.SeatConnections[0]);
        await AssertStoredCut(fixture, instance);
    }

    [Fact]
    public async Task Shutdown_CancelsAndDrainsCompletionAlongsideAnAdmittedDisconnect()
    {
        var gate = new SnapshotWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var (id, _) = await CreatePublicGame(fixture);
        var instance = fixture.Instances()[id];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = instance.QueueCompletionEffects(async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled.TrySetResult(); }
        });
        await entered.Task.WaitAsync(Deadline);
        gate.Arm(id);
        var disconnect = fixture.Runtime.HandleDisconnectAsync("replay-owner", "owner-connection");
        await gate.Entered.Task.WaitAsync(Deadline);
        var shutdown = fixture.Runtime.DisposeAsync().AsTask();
        Assert.Same(shutdown, fixture.Runtime.DisposeAsync().AsTask());
        await cancelled.Task.WaitAsync(Deadline);
        Assert.False(shutdown.IsCompleted);
        gate.Release.TrySetResult();
        await Task.WhenAll(disconnect, shutdown, completion).WaitAsync(Deadline);

        Assert.True(completion.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => instance.QueueCompletionEffects(_ => Task.CompletedTask));
        await AssertStoredCut(fixture, instance);
    }

    [Fact]
    public async Task NormalDisconnect_PreservesReservationsReplayAndFurtherOperations()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await CreatePublicGame(fixture);
        await fixture.Runtime.HandleDisconnectAsync("replay-owner", "owner-connection");
        Assert.Equal("replay-owner", fixture.State(id).Seats[0].PlayerId);
        Assert.Equal("successor", fixture.State(id).CreatorPlayerId);
        Assert.True(await fixture.Runtime.ReconnectAsync(id, 0, "replay-owner", "owner-returned"));
        await fixture.Runtime.SetGamePublicAsync(id, "successor", true, "continued-title");
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal("continued-title", fixture.State(id).PublicName);
        await AssertStoredCut(fixture, fixture.Instances()[id]);
    }

    private static async Task<(string Id, string Room)> CreatePublicGame(
        ReplayRuntimeFixture fixture, DealMode mode = DealMode.Manual)
    {
        var result = await fixture.CreatePublicSeating(19, mode);
        await fixture.Runtime.TakeSeatAsync(result.Id, "replay-owner", "owner-connection", 0);
        await fixture.Runtime.TakeSeatAsync(result.Id, "successor", "successor-connection", 1);
        await fixture.Runtime.SetGamePublicAsync(result.Id, "replay-owner", true, "lifetime-test");
        return result;
    }

    private static async Task AssertStoredCut(
        ReplayRuntimeFixture fixture, ChangshaGameInstance instance, ReplayCutKind kind = ReplayCutKind.Checkpoint)
    {
        var (envelope, json, rows) = await fixture.ReadPrefix(instance.GameId, kind);
        var result = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(result.Success, $"{result.Status}: {result.Detail}");
        Assert.Equal(json, ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
        Assert.Equal(json, ChangshaReplayStateCodec.Serialize(instance.State));
        Assert.Equal(Enumerable.Range(1, rows.Length).Select(value => (long)value), rows.Select(row => row.Sequence));
        using var scope = fixture.Services.CreateScope();
        var id = Guid.Parse(instance.GameId);
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangshaGames
            .AsNoTracking().SingleAsync(game => game.Id == id);
        Assert.Equal((instance.State.StateVersion, instance.State.HandNumber, instance.State.RoundNumber),
            (row.StateVersion, row.CurrentHandNumber, row.CurrentRoundNumber));
    }

    private static object Loaded(Assembly assembly) => new
    {
        path = assembly.Location,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()
    };

    private sealed class SnapshotWriteGate : SaveChangesInterceptor, IDisposable
    {
        private Guid _gameId;
        private int _armed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Trace { get; } = new();

        public void Dispose() => Release.TrySetResult();

        internal void Arm(string id)
        {
            _gameId = Guid.Parse(id);
            Interlocked.Exchange(ref _armed, 1);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && eventData.Context!.ChangeTracker.Entries<ChangshaGame>().Any(entry => entry.Entity.Id == _gameId)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Trace.Enqueue("snapshot-entered-before-commit");
                Entered.TrySetResult();
                await Release.Task;
                Trace.Enqueue("snapshot-released-to-commit");
            }
            return result;
        }
    }
}
