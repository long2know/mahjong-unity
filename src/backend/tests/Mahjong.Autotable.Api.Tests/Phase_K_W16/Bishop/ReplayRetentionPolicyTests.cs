using Mahjong.Autotable.Api.Replays;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Behaviour tests for the per-tenant
/// replay retention policy store + the sweep wiring.
/// </summary>
public sealed class ReplayRetentionPolicyTests
{
    private static ReplayRetentionPolicy NewPolicy(string tenantId, int days) =>
        new() { TenantId = tenantId, RetentionDays = days };

    private static ReplayRecord NewReplay(string? tenantId, DateTime completedAt)
    {
        return new ReplayRecord
        {
            ReplayId = $"r-{Guid.NewGuid():N}",
            GameId = Guid.NewGuid(),
            TenantId = tenantId,
            CompletedAt = completedAt,
            ExpiresAt = completedAt.AddDays(30),
        };
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_InsertsRow()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        var p = await store.UpsertAsync(NewPolicy("acme", 30));
        Assert.Equal("acme", p.TenantId);
        Assert.Equal(30, p.RetentionDays);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_UpdatesInPlace()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("acme", 30));
        await store.UpsertAsync(NewPolicy("acme", 90));
        Assert.Equal(1, await store.CountAsync());
        var got = await store.GetAsync("acme");
        Assert.NotNull(got);
        Assert.Equal(90, got!.RetentionDays);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_EmptyTenantId_Throws()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpsertAsync(new ReplayRetentionPolicy { TenantId = "", RetentionDays = 30 }));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Get_ReturnsNullForUnknown()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Get_ReturnsStoredRow()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("acme", 14));
        var got = await store.GetAsync("acme");
        Assert.NotNull(got);
        Assert.Equal(14, got!.RetentionDays);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_List_OrderedByTenantId()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("gamma", 30));
        await store.UpsertAsync(NewPolicy("alpha", 7));
        await store.UpsertAsync(NewPolicy("beta", 14));
        var rows = await store.ListAsync();
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, rows.Select(r => r.TenantId).ToArray());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Delete_RemovesRow()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("acme", 30));
        Assert.Equal(1, await store.DeleteAsync("acme"));
        Assert.Equal(0, await store.CountAsync());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Delete_UnknownIsNoOp()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        Assert.Equal(0, await store.DeleteAsync("missing"));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_UpdatedAt_BumpedOnUpsert()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        var p1 = await store.UpsertAsync(NewPolicy("acme", 30));
        await Task.Delay(15);
        var p2 = await store.UpsertAsync(NewPolicy("acme", 60));
        Assert.True(p2.UpdatedAt >= p1.UpdatedAt);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_FallbackForUntaggedRows()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await replayStore.InsertAsync(NewReplay(null, now.AddDays(-31)));
        await replayStore.InsertAsync(NewReplay(null, now.AddDays(-3)));
        var removed = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 30, utcNow: now);
        Assert.Equal(1, removed);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_AppliesPerTenantWindow()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await policyStore.UpsertAsync(NewPolicy("short-tenant", 5));
        await policyStore.UpsertAsync(NewPolicy("long-tenant", 90));
        // short-tenant row at 7 days old → over 5-day window → swept
        await replayStore.InsertAsync(NewReplay("short-tenant", now.AddDays(-7)));
        // long-tenant row at 60 days old → under 90-day window → kept
        await replayStore.InsertAsync(NewReplay("long-tenant", now.AddDays(-60)));
        var removed = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 30, utcNow: now);
        Assert.Equal(1, removed);
        Assert.Equal(1, await replayStore.CountAsync());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_TaggedRowWithoutPolicyUsesFallback()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        // tagged tenant but no policy row → falls back to global 30-day window
        await replayStore.InsertAsync(NewReplay("unmapped", now.AddDays(-45)));
        var removed = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 30, utcNow: now);
        Assert.Equal(1, removed);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_ZeroFallback_KeepsUntaggedRows()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await replayStore.InsertAsync(NewReplay(null, now.AddDays(-365)));
        var removed = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 0, utcNow: now);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_NullPolicyStore_Throws()
    {
        var replayStore = new InMemoryReplayStore();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => replayStore.SweepWithPerTenantPolicyAsync(null!, 30, DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_MidFlightPolicyUpdate_AppliesNextTick()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await policyStore.UpsertAsync(NewPolicy("acme", 90));
        await replayStore.InsertAsync(NewReplay("acme", now.AddDays(-30)));
        var firstSweep = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 30, utcNow: now);
        Assert.Equal(0, firstSweep);

        // Operator tightens the window
        await policyStore.UpsertAsync(NewPolicy("acme", 7));
        var secondSweep = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 30, utcNow: now);
        Assert.Equal(1, secondSweep);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_ZeroOrNegativeRetention_TreatedAsAbsent()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await policyStore.UpsertAsync(NewPolicy("acme", 0));
        await replayStore.InsertAsync(NewReplay("acme", now.AddDays(-45)));
        var removed = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 30, utcNow: now);
        // Zero retention from row → fall back to global 30-day window → 45d > 30d → swept
        Assert.Equal(1, removed);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_NeverDeletesFreshRows()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await policyStore.UpsertAsync(NewPolicy("acme", 30));
        await replayStore.InsertAsync(NewReplay("acme", now.AddDays(-3)));
        await replayStore.InsertAsync(NewReplay("acme", now.AddDays(-29)));
        var removed = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 30, utcNow: now);
        Assert.Equal(0, removed);
        Assert.Equal(2, await replayStore.CountAsync());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ReplayStore_SweepWithPerTenantPolicy_MultiTenantIsolation()
    {
        var replayStore = new InMemoryReplayStore();
        var policyStore = new InMemoryReplayRetentionPolicyStore();
        var now = DateTime.UtcNow;
        await policyStore.UpsertAsync(NewPolicy("alpha", 7));
        await policyStore.UpsertAsync(NewPolicy("beta", 90));
        await replayStore.InsertAsync(NewReplay("alpha", now.AddDays(-10)));   // swept
        await replayStore.InsertAsync(NewReplay("alpha", now.AddDays(-3)));    // kept
        await replayStore.InsertAsync(NewReplay("beta", now.AddDays(-60)));    // kept
        await replayStore.InsertAsync(NewReplay("beta", now.AddDays(-100)));   // swept
        var removed = await replayStore.SweepWithPerTenantPolicyAsync(
            policyStore, fallbackDays: 365, utcNow: now);
        Assert.Equal(2, removed);
        Assert.Equal(2, await replayStore.CountAsync());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ReplayRetentionPolicy_DefaultValues()
    {
        var p = new ReplayRetentionPolicy();
        Assert.Equal(string.Empty, p.TenantId);
        Assert.Equal(0, p.RetentionDays);
        Assert.True(p.CreatedAt <= DateTime.UtcNow.AddSeconds(1));
        Assert.True(p.UpdatedAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ReplayRecord_TenantId_DefaultsToNull()
    {
        var r = new ReplayRecord();
        Assert.Null(r.TenantId);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_GetAsync_EmptyTenant_ReturnsNull()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        Assert.Null(await store.GetAsync(""));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_DeleteAsync_EmptyTenant_ReturnsZero()
    {
        var store = new InMemoryReplayRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("acme", 30));
        Assert.Equal(0, await store.DeleteAsync(""));
        Assert.Equal(1, await store.CountAsync());
    }
}
