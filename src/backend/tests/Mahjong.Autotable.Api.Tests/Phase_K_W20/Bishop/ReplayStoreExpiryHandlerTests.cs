using Mahjong.Autotable.Api.Replays;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Tests for the
/// <see cref="ReplayStoreExpiryHandler"/> hosted service.
/// Drives the handler through its internal RunOnceAsync seam
/// against the InMemoryReplayStore so retention behaviour can
/// be asserted deterministically against a fixed clock.
/// </summary>
public sealed class ReplayStoreExpiryHandlerTests
{
    private static async Task SeedAsync(IReplayStore store, string? tenantId, DateTime completedAt)
    {
        await store.InsertAsync(new ReplayRecord
        {
            GameId = Guid.NewGuid(),
            CompletedAt = completedAt,
            Variant = "changsha-v1",
            TurnCount = 12,
            TenantId = tenantId,
            CompressedPayload = ReplayRecord.CompressPayload("{\"hand\":\"x\"}"),
            IngestedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(365),
        });
    }

    private static ReplayStoreExpiryHandler Build(
        IReplayStore store,
        int retentionDays,
        DateTime now,
        IReplayRetentionPolicyStore? policyStore = null,
        ReplayExpiryMetrics? metrics = null) =>
        new ReplayStoreExpiryHandler(
            store,
            new ReplayOptions { RetentionDays = retentionDays },
            NullLogger<ReplayStoreExpiryHandler>.Instance,
            policyStore,
            metrics,
            scopeFactory: null,
            clock: () => now);

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_NoPolicyStore_FallsBackToGlobalSweep()
    {
        var store = new InMemoryReplayStore();
        var now = DateTime.UtcNow;
        await SeedAsync(store, null, now.AddDays(-31));
        await SeedAsync(store, "tenant-a", now.AddDays(-31));
        var handler = Build(store, 30, now);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.True(breakdown[""] >= 2);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_NoPolicyStore_BucketsAllUnderEmptyKey()
    {
        var store = new InMemoryReplayStore();
        var now = DateTime.UtcNow;
        await SeedAsync(store, "tenant-a", now.AddDays(-31));
        await SeedAsync(store, "tenant-b", now.AddDays(-31));
        var metrics = new ReplayExpiryMetrics();
        var handler = Build(store, 30, now, policyStore: null, metrics: metrics);
        await handler.RunOnceAsync(CancellationToken.None);
        // Without a policy store the handler buckets every evicted
        // row under the empty-tenant key (rendered as "_unknown").
        Assert.True(metrics.Get(ReplayExpiryMetrics.UnknownTenantBucket) >= 2);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_WithPolicyStore_BreakdownIsPerTenant()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await SeedAsync(store, "tenant-a", now.AddDays(-10));
        await SeedAsync(store, "tenant-b", now.AddDays(-50));
        // tenant-a retention 5 days -- both -10d rows are over.
        // tenant-b retention 30 days -- -50d row is over.
        await policies.UpsertAsync(new ReplayRetentionPolicy { TenantId = "tenant-a", RetentionDays = 5 });
        await policies.UpsertAsync(new ReplayRetentionPolicy { TenantId = "tenant-b", RetentionDays = 30 });

        var handler = Build(store, 90, now, policies);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, breakdown["tenant-a"]);
        Assert.Equal(1, breakdown["tenant-b"]);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_WithPolicyStore_NothingDueReturnsEmpty()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await SeedAsync(store, "tenant-a", now.AddDays(-1));
        await policies.UpsertAsync(new ReplayRetentionPolicy { TenantId = "tenant-a", RetentionDays = 30 });

        var handler = Build(store, 30, now, policies);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.False(breakdown.TryGetValue("tenant-a", out _));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_WritesPerTenantMetricsCounter()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var metrics = new ReplayExpiryMetrics();
        var now = DateTime.UtcNow;
        await SeedAsync(store, "tenant-a", now.AddDays(-50));
        await SeedAsync(store, "tenant-a", now.AddDays(-60));
        await policies.UpsertAsync(new ReplayRetentionPolicy { TenantId = "tenant-a", RetentionDays = 30 });

        var handler = Build(store, 90, now, policies, metrics);
        await handler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, metrics.Get("tenant-a"));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_ZeroRetention_FallsBackToDefault()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        // 200 days old > default 90-day retention -> evicted.
        await SeedAsync(store, null, now.AddDays(-200));
        var handler = Build(store, 0, now, policies);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.True(breakdown[""] >= 1);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_PerTenantPolicyOverridesGlobal()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        // tenant-a row at -20d. Global retention 30d -> would survive.
        // tenant-a policy retention 5d -> evicted.
        await SeedAsync(store, "tenant-a", now.AddDays(-20));
        await policies.UpsertAsync(new ReplayRetentionPolicy { TenantId = "tenant-a", RetentionDays = 5 });
        var handler = Build(store, 30, now, policies);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, breakdown["tenant-a"]);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_RowsWithoutTenant_BucketedUnderEmpty()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await SeedAsync(store, null, now.AddDays(-200));
        var handler = Build(store, 30, now, policies);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.True(breakdown.ContainsKey(""));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_EmptyStore_NoOp()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var handler = Build(store, 30, DateTime.UtcNow, policies);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.Empty(breakdown);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task RunOnce_ClockIsHonoured()
    {
        var store = new InMemoryReplayStore();
        var policies = new InMemoryReplayRetentionPolicyStore();
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(store, null, now.AddDays(-60));
        var handler = Build(store, 30, now, policies);
        var breakdown = await handler.RunOnceAsync(CancellationToken.None);
        Assert.True(breakdown[""] >= 1);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Ctor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayStoreExpiryHandler(
            null!, new ReplayOptions(), NullLogger<ReplayStoreExpiryHandler>.Instance));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayStoreExpiryHandler(
            new InMemoryReplayStore(), null!, NullLogger<ReplayStoreExpiryHandler>.Instance));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayStoreExpiryHandler(
            new InMemoryReplayStore(), new ReplayOptions(), null!));
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Options_AutoExpiryTickIntervalMinutes_DefaultsTo60()
    {
        Assert.Equal(60, new ReplayOptions().AutoExpiryTickIntervalMinutes);
    }
}
