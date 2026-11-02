using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
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

public sealed class RuntimeAdmissionLifetimeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("public-create")]
    [InlineData("lazy-recovery")]
    [InlineData("eager-hydration")]
    public async Task ShutdownDuringPrepublication_DrainsCandidateAndCannotPublishAfterCompletion(string flow)
    {
        using var gate = new AdmissionWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var room = $"admission-{Guid.NewGuid():N}";
        string? id = null;
        if (flow != "public-create")
        {
            id = await Create(fixture, room);
            await fixture.Restart(eager: false);
        }
        Assert.Empty(fixture.Instances());
        gate.Runtime = fixture.Runtime;
        gate.Arm(id);
        var operation = Start(fixture, flow, room);
        Task? shutdown = null;
        var candidates = new List<ChangshaGameInstance>();
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            candidates.AddRange(gate.Candidates);
            Assert.Empty(fixture.Instances());
            shutdown = fixture.Runtime.DisposeAsync().AsTask();
            var completedWhileHeld = shutdown.IsCompleted;
            Assert.Same(shutdown, fixture.Runtime.DisposeAsync().AsTask());
            gate.Release.TrySetResult();
            var operationFailure = await Observe(operation);
            var shutdownFailure = await Observe(shutdown);
            candidates.AddRange(fixture.Instances().Values);
            var bindings = Bindings(fixture.Runtime);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                flow, stage = "after-persistence-release-before-test-cleanup", completedWhileHeld,
                operationFailure = operationFailure?.GetType().FullName,
                shutdownFailure = shutdownFailure?.ToString(),
                publishedGames = fixture.Runtime.GameCount, publishedBindings = bindings.Count,
                candidateCount = candidates.Distinct().Count(), gameId = gate.GameId,
                api = Loaded(typeof(ChangshaGameRuntime).Assembly),
                tests = Loaded(typeof(RuntimeAdmissionLifetimeTests).Assembly)
            }));
            Assert.False(completedWhileHeld);
            Assert.IsType<ObjectDisposedException>(operationFailure);
            Assert.Null(shutdownFailure);
            Assert.Equal(0, fixture.Runtime.GameCount);
            Assert.Empty(bindings);
            var candidate = Assert.Single(candidates.Distinct());
            await AssertDisposed(candidate);
            await AssertStoredPrefix(fixture, gate.GameId!);
            Assert.Same(shutdown, fixture.Runtime.DisposeAsync().AsTask());
        }
        finally
        {
            gate.Release.TrySetResult();
            await Observe(operation);
            if (shutdown is not null) await Observe(shutdown);
            foreach (var candidate in candidates.Concat(fixture.Instances().Values).Distinct())
                await candidate.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("public-create")]
    [InlineData("private-create")]
    [InlineData("lazy-recovery")]
    [InlineData("eager-hydration")]
    [InlineData("creation-check")]
    public async Task NewAdmissionAfterShutdown_RejectsWithoutPublication(string flow)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var shutdown = fixture.Runtime.DisposeAsync().AsTask();
        await shutdown.WaitAsync(Deadline);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Start(fixture, flow, "stopped-room"));
        Assert.Empty(fixture.Instances());
        Assert.Empty(Bindings(fixture.Runtime));
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.ChangshaGames.CountAsync());
        Assert.Equal(0, await db.AutotableRoomBindings.CountAsync());
        Assert.Equal(0, await db.ChangshaGameEvents.CountAsync());
        Assert.Same(shutdown, fixture.Runtime.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData("public-create")]
    [InlineData("lazy-recovery")]
    public async Task CancelledPrepublication_DisposesCandidateAndReleasesAdmission(string flow)
    {
        using var gate = new AdmissionWriteGate();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var room = $"cancel-admission-{Guid.NewGuid():N}";
        string? id = null;
        if (flow == "lazy-recovery")
        {
            id = await Create(fixture, room);
            await fixture.Restart(eager: false);
        }
        gate.Runtime = fixture.Runtime;
        gate.Arm(id);
        using var cancellation = new CancellationTokenSource();
        var operation = Start(fixture, flow, room, cancellation.Token);
        await gate.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        gate.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Deadline));
        await AssertDisposed(Assert.Single(gate.Candidates));
        Assert.Empty(fixture.Instances());
        Assert.Empty(Bindings(fixture.Runtime));
        await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData("public-create")]
    [InlineData("lazy-recovery")]
    public async Task FailedPrepublication_DisposesCandidateAndAllowsValidRetry(string flow)
    {
        using var gate = new AdmissionWriteGate { FailWrite = true };
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var room = $"failed-admission-{Guid.NewGuid():N}";
        string? id = null;
        if (flow == "lazy-recovery")
        {
            id = await Create(fixture, room);
            await fixture.Restart(eager: false);
        }
        gate.Runtime = fixture.Runtime;
        gate.Arm(id);
        var operation = Start(fixture, flow, room);
        await gate.Entered.Task.WaitAsync(Deadline);
        gate.Release.TrySetResult();
        var failure = await Assert.ThrowsAsync<PublicRoomRecoveryException>(() => operation.WaitAsync(Deadline));
        Assert.Equal(flow == "public-create" ? "room-persistence-failed" : "room-replay-persistence-failed",
            failure.Reason);
        await AssertDisposed(Assert.Single(gate.Candidates));
        Assert.Empty(fixture.Instances());
        Assert.Empty(Bindings(fixture.Runtime));
        var accepted = await Start(fixture, flow, room).WaitAsync(Deadline);
        Assert.NotNull(accepted);
        Assert.Single(fixture.Instances());
        await AssertStoredPrefix(fixture, accepted!);
    }

    [Theory]
    [InlineData("public-create")]
    [InlineData("lazy-recovery")]
    public async Task PersistenceAndCandidateCleanupFailures_AreBothObserved(string flow)
    {
        using var gate = new AdmissionWriteGate { FailWrite = true, FailCandidateCancellation = true };
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        var room = $"double-fault-{Guid.NewGuid():N}";
        string? id = null;
        if (flow == "lazy-recovery")
        {
            id = await Create(fixture, room);
            await fixture.Restart(eager: false);
        }
        gate.Runtime = fixture.Runtime;
        gate.Arm(id);
        var operation = Start(fixture, flow, room);
        await gate.Entered.Task.WaitAsync(Deadline);
        gate.Release.TrySetResult();
        var failure = await Assert.ThrowsAsync<AggregateException>(() => operation.WaitAsync(Deadline));
        var errors = failure.Flatten().InnerExceptions;
        var reason = flow == "public-create" ? "room-persistence-failed" : "room-replay-persistence-failed";
        Assert.Contains(errors, error => error is PublicRoomRecoveryException recovery && recovery.Reason == reason);
        Assert.Contains(errors, error => error is InvalidOperationException && error.Message == "candidate-cleanup-fault");
        var candidate = Assert.Single(gate.Candidates);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => candidate.Lock.WaitAsync());
        Assert.Empty(fixture.Instances());
        Assert.Empty(Bindings(fixture.Runtime));
        await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ReentrantShutdownFromAdmission_FailsExplicitlyWithoutSelfDrain()
    {
        using var gate = new AdmissionWriteGate { ProbeReentrantDisposal = true };
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: gate);
        using var releaseGate = gate;
        gate.Runtime = fixture.Runtime;
        gate.Arm(null);
        var operation = Start(fixture, "public-create", $"reentrant-{Guid.NewGuid():N}");
        await gate.Entered.Task.WaitAsync(Deadline);
        gate.Release.TrySetResult();
        var id = await operation.WaitAsync(Deadline);
        Assert.IsType<InvalidOperationException>(gate.ReentrantFailure);
        Assert.NotNull(id);
        Assert.Single(fixture.Instances());
        await AssertStoredPrefix(fixture, id!);
        await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ReentrantShutdownFromCancellation_FailsExplicitlyAndOuterShutdownCompletes()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await Create(fixture, $"shutdown-callback-{Guid.NewGuid():N}");
        var instance = fixture.Instances()[id];
        Exception? nested = null;
        using var callback = instance.LifecycleCts.Token.Register(() =>
            nested = Record.Exception(() => { _ = fixture.Runtime.DisposeAsync(); }));
        await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.IsType<InvalidOperationException>(nested);
        Assert.Empty(fixture.Instances());
        Assert.Empty(Bindings(fixture.Runtime));
        await AssertDisposed(instance);
        await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task RemovalCancellation_DoesNotInvokeCallbacksUnderRuntimeAdmissionGate()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await Create(fixture, $"removal-callback-{Guid.NewGuid():N}");
        var instance = fixture.Instances()[id];
        var gate = typeof(ChangshaGameRuntime).GetField("_disposeGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Runtime)!;
        bool? gateHeld = null;
        using var callback = instance.LifecycleCts.Token.Register(() => gateHeld = Monitor.IsEntered(gate));
        await fixture.Runtime.RemoveGameAsync(id).WaitAsync(Deadline);
        Assert.NotNull(gateHeld);
        Assert.False(gateHeld.Value);
        Assert.Empty(fixture.Instances());
        await AssertDisposed(instance);
    }

    [Fact]
    public async Task NormalAdmissions_PreserveAtomicRowsReplayAndIdempotentRecovery()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var room = $"normal-admission-{Guid.NewGuid():N}";
        var id = await Create(fixture, room);
        await fixture.Runtime.TakeSeatAsync(id, "admission-owner", "original-connection", 0);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        await AssertStoredPrefix(fixture, id);
        await fixture.Restart(eager: true);
        var first = fixture.Instances()[id];
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Same(first, fixture.Instances()[id]);
        Assert.Equal("admission-owner", first.RecoveredSeatOwners[0]);
        Assert.Equal(7, first.State.BaseUnit);
        Assert.Equal(DealMode.Manual, first.State.DealMode);
        Assert.Equal("hard", first.State.BotDifficulty);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.ChangshaGames.CountAsync());
        Assert.Equal(1, await db.AutotableRoomBindings.CountAsync());
        await AssertStoredPrefix(fixture, id);
    }

    private static Task<string> Create(ReplayRuntimeFixture fixture, string room, CancellationToken ct = default) =>
        fixture.Runtime.CreateGameAsync(19, [], "admission-owner", null, ct, maxHands: 4, baseUnit: 7,
            publicRoom: new(room, DealMode.Manual, "hard"));

    private static async Task<string?> Start(
        ReplayRuntimeFixture fixture, string flow, string room, CancellationToken ct = default)
    {
        switch (flow)
        {
            case "public-create": return await Create(fixture, room, ct);
            case "private-create": return await fixture.Runtime.CreateGameAsync(19, [], null, null, ct);
            case "lazy-recovery": return await fixture.Runtime.RestorePublicRoomAsync(room, ct);
            case "eager-hydration": await fixture.Runtime.HydrateAsync(fixture.Services, ct); return null;
            case "creation-check": await fixture.Runtime.EnsurePublicRoomCreationAllowedAsync("owner", true, ct); return null;
            default: throw new ArgumentOutOfRangeException(nameof(flow));
        }
    }

    private static Task<Exception?> Observe(Task work) =>
        Record.ExceptionAsync(() => work.WaitAsync(Deadline));

    private static async Task AssertDisposed(ChangshaGameInstance instance)
    {
        Assert.Null(instance.TryEnterOperation());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => instance.Lock.WaitAsync());
        await instance.DisposeAsync();
    }

    private static ConcurrentDictionary<string, string> Bindings(ChangshaGameRuntime runtime) =>
        Assert.IsType<ConcurrentDictionary<string, string>>(typeof(ChangshaGameRuntime)
            .GetField("_publicRoomBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));

    private static IReadOnlyList<ChangshaGameInstance> UnpublishedCandidates(ChangshaGameRuntime runtime)
    {
        var field = typeof(ChangshaGameRuntime).GetField("_admissions", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) return [];
        var admissions = Assert.IsAssignableFrom<IEnumerable>(field.GetValue(runtime));
        return admissions.Cast<object>().SelectMany(admission =>
            Assert.IsAssignableFrom<IEnumerable>(admission.GetType()
                .GetField("_candidates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(admission))
                .Cast<ChangshaGameInstance>()).ToArray();
    }

    private static async Task AssertStoredPrefix(ReplayRuntimeFixture fixture, string id)
    {
        var (envelope, original, rows) = await fixture.ReadPrefix(id);
        var checkedReplay = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(checkedReplay.Success, $"{checkedReplay.Status}: {checkedReplay.Detail}");
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(checkedReplay.ReconstructedState!));
        Assert.Equal(Enumerable.Range(1, rows.Length).Select(value => (long)value), rows.Select(row => row.Sequence));
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Guid.Parse(id);
        var row = await db.ChangshaGames.AsNoTracking().SingleAsync(game => game.Id == key);
        var binding = await db.AutotableRoomBindings.AsNoTracking().SingleAsync(item => item.RuntimeGameId == key);
        Assert.Equal(key, binding.RuntimeGameId);
        Assert.Equal(row.StateVersion, checkedReplay.ReconstructedState!.StateVersion);
    }

    private static object Loaded(Assembly assembly) => new
    {
        path = assembly.Location,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()
    };

    private sealed class AdmissionWriteGate : SaveChangesInterceptor, IDisposable
    {
        private int _armed;
        private Guid? _target;
        internal ChangshaGameRuntime? Runtime { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<ChangshaGameInstance> Candidates { get; private set; } = [];
        internal string? GameId { get; private set; }
        internal bool FailWrite { get; init; }
        internal bool FailCandidateCancellation { get; init; }
        internal bool ProbeReentrantDisposal { get; init; }
        internal Exception? ReentrantFailure { get; private set; }

        internal void Arm(string? gameId)
        {
            _target = gameId is null ? null : Guid.Parse(gameId);
            Volatile.Write(ref _armed, 1);
        }

        public void Dispose() => Release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var row = eventData.Context!.ChangeTracker.Entries<ChangshaGame>()
                .FirstOrDefault(entry => _target is null || entry.Entity.Id == _target);
            if (row is null || Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
                return result;
            GameId = row.Entity.Id.ToString();
            Candidates = UnpublishedCandidates(Runtime!);
            if (FailCandidateCancellation)
                Assert.Single(Candidates).LifecycleCts.Token.Register(() =>
                    throw new InvalidOperationException("candidate-cleanup-fault"));
            if (ProbeReentrantDisposal)
                ReentrantFailure = await Record.ExceptionAsync(() => Runtime!.DisposeAsync().AsTask().WaitAsync(Deadline));
            Entered.TrySetResult();
            await Release.Task;
            if (FailWrite) throw new DbUpdateException("admission-persistence-fault");
            return result;
        }
    }
}
