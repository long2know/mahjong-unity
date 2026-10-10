using Mahjong.Autotable.Api.Replays;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Hard-asserted contract for the
/// W15 <see cref="ReplayStoreRetentionSweep"/> hosted service
/// + <see cref="IReplayStore.SweepByCompletedAtAsync"/> seam.
///
/// <list type="number">
///   <item>SweepByCompletedAt drops CompletedAt-older-than-cutoff rows.</item>
///   <item>SweepByCompletedAt keeps rows inside retention window.</item>
///   <item>SweepByCompletedAt returns 0 when retention &lt;= 0.</item>
///   <item>SweepByCompletedAt no-op on empty store.</item>
///   <item>RunOnceAsync uses configured retention.</item>
///   <item>RunOnceAsync uses DefaultRetentionDays when options retention &lt;= 0.</item>
///   <item>RunOnceAsync re-evaluates each tick (dial down honoured).</item>
///   <item>DefaultSweepIntervalMinutes constant is 60.</item>
///   <item>Options.StoreSweepIntervalMinutes default is 60.</item>
///   <item>Ctor null-checks options/store/logger.</item>
/// </list>
/// </summary>
public sealed class ReplayStoreRetentionSweepTests
{
    private static async Task SeedAsync(IReplayStore store, DateTime completedAt)
    {
        await store.InsertAsync(new ReplayRecord
        {
            GameId = Guid.NewGuid(),
            CompletedAt = completedAt,
            Variant = "changsha-v1",
            TurnCount = 12,
            CompressedPayload = ReplayRecord.CompressPayload("{\"hand\":\"x\"}"),
            IngestedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(365),
        });
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task SweepByCompletedAt_DropsRowsOlderThanCutoff()
    {
        var store = new InMemoryReplayStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-31));
        await SeedAsync(store, DateTime.UtcNow.AddDays(-31));
        var removed = await store.SweepByCompletedAtAsync(30, DateTime.UtcNow);
        Assert.Equal(2, removed);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task SweepByCompletedAt_KeepsRowsInsideWindow()
    {
        var store = new InMemoryReplayStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-5));
        var removed = await store.SweepByCompletedAtAsync(30, DateTime.UtcNow);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task SweepByCompletedAt_ZeroRetention_ReturnsZero()
    {
        var store = new InMemoryReplayStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-365));
        var removed = await store.SweepByCompletedAtAsync(0, DateTime.UtcNow);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task SweepByCompletedAt_NegativeRetention_ReturnsZero()
    {
        var store = new InMemoryReplayStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-365));
        var removed = await store.SweepByCompletedAtAsync(-1, DateTime.UtcNow);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task SweepByCompletedAt_EmptyStore_NoOp()
    {
        var store = new InMemoryReplayStore();
        var removed = await store.SweepByCompletedAtAsync(30, DateTime.UtcNow);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_UsesConfiguredRetention()
    {
        var store = new InMemoryReplayStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-15));
        var options = new ReplayOptions { RetentionDays = 10 };
        var sweep = new ReplayStoreRetentionSweep(
            store, options, NullLogger<ReplayStoreRetentionSweep>.Instance);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, removed);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_DialDown_HonoredOnNextTick()
    {
        var store = new InMemoryReplayStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-15));
        var options = new ReplayOptions { RetentionDays = 30 };
        var sweep = new ReplayStoreRetentionSweep(
            store, options, NullLogger<ReplayStoreRetentionSweep>.Instance);
        // First tick — no eviction with 30-day window.
        Assert.Equal(0, await sweep.RunOnceAsync(CancellationToken.None));
        // Operator dials retention down — next tick must evict.
        options.RetentionDays = 10;
        Assert.Equal(1, await sweep.RunOnceAsync(CancellationToken.None));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void DefaultSweepIntervalMinutes_ConstantIs60()
    {
        Assert.Equal(60, ReplayStoreRetentionSweep.DefaultSweepIntervalMinutes);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Options_StoreSweepIntervalMinutes_DefaultIs60()
    {
        Assert.Equal(60, new ReplayOptions().StoreSweepIntervalMinutes);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Ctor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReplayStoreRetentionSweep(
                null!, new ReplayOptions(),
                NullLogger<ReplayStoreRetentionSweep>.Instance));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReplayStoreRetentionSweep(
                new InMemoryReplayStore(), null!,
                NullLogger<ReplayStoreRetentionSweep>.Instance));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReplayStoreRetentionSweep(
                new InMemoryReplayStore(), new ReplayOptions(), null!));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_FallsBack_WhenOptionsRetentionZero()
    {
        var store = new InMemoryReplayStore();
        var defaultRetention = ReplayOptions.DefaultRetentionDays;
        await SeedAsync(store, DateTime.UtcNow.AddDays(-(defaultRetention + 5)));
        await SeedAsync(store, DateTime.UtcNow.AddDays(-5));
        var options = new ReplayOptions { RetentionDays = 0 };
        var sweep = new ReplayStoreRetentionSweep(
            store, options, NullLogger<ReplayStoreRetentionSweep>.Instance);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, removed);
    }
}
