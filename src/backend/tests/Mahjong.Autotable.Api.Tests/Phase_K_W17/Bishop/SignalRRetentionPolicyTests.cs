using Mahjong.Autotable.Api.Observability;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Behaviour tests for the per-tenant
/// SignalR-sequence retention policy store + the sweep wiring.
/// Mirrors <c>Phase_K_W16/Bishop/ReplayRetentionPolicyTests</c>.
/// </summary>
public sealed class SignalRRetentionPolicyTests
{
    private static SignalRRetentionPolicy NewPolicy(string tenantId, int minutes) =>
        new() { TenantId = tenantId, RetentionMinutes = minutes };

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_InsertsRow()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        var p = await store.UpsertAsync(NewPolicy("acme", 60));
        Assert.Equal("acme", p.TenantId);
        Assert.Equal(60, p.RetentionMinutes);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_UpdatesInPlace()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("acme", 60));
        await store.UpsertAsync(NewPolicy("acme", 1440));
        Assert.Equal(1, await store.CountAsync());
        var got = await store.GetAsync("acme");
        Assert.NotNull(got);
        Assert.Equal(1440, got!.RetentionMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_EmptyTenantId_Throws()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpsertAsync(new SignalRRetentionPolicy { TenantId = "", RetentionMinutes = 60 }));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_NullPolicy_Throws()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.UpsertAsync(null!));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Get_ReturnsNullForUnknown()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Get_ReturnsNullForEmpty()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        Assert.Null(await store.GetAsync(""));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Get_ReturnsStoredRow()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("acme", 120));
        var got = await store.GetAsync("acme");
        Assert.NotNull(got);
        Assert.Equal(120, got!.RetentionMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_List_OrderedByTenantId()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("gamma", 60));
        await store.UpsertAsync(NewPolicy("alpha", 30));
        await store.UpsertAsync(NewPolicy("beta", 90));
        var rows = await store.ListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal("alpha", rows[0].TenantId);
        Assert.Equal("beta", rows[1].TenantId);
        Assert.Equal("gamma", rows[2].TenantId);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Delete_RemovesRow()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(NewPolicy("acme", 60));
        var deleted = await store.DeleteAsync("acme");
        Assert.Equal(1, deleted);
        Assert.Null(await store.GetAsync("acme"));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Delete_Unknown_ReturnsZero()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        var deleted = await store.DeleteAsync("missing");
        Assert.Equal(0, deleted);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Delete_EmptyTenantId_ReturnsZero()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        var deleted = await store.DeleteAsync("");
        Assert.Equal(0, deleted);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PolicyStore_Upsert_BumpsUpdatedAt()
    {
        var store = new InMemorySignalRRetentionPolicyStore();
        var initial = await store.UpsertAsync(NewPolicy("acme", 60));
        var initialUpdated = initial.UpdatedAt;
        await Task.Delay(15);
        var updated = await store.UpsertAsync(NewPolicy("acme", 1440));
        Assert.True(updated.UpdatedAt >= initialUpdated);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void DefaultRetentionMinutes_Is24Hours()
    {
        Assert.Equal(1440, SignalRRetentionPolicy.DefaultRetentionMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void MaxRetentionMinutes_Is60Days()
    {
        Assert.Equal(60 * 24 * 60, SignalRRetentionPolicy.MaxRetentionMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void CreatedAtOffset_RoundTrips()
    {
        var p = NewPolicy("acme", 60);
        p.CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero, p.CreatedAtOffset.Offset);
        Assert.Equal(p.CreatedAt, p.CreatedAtOffset.UtcDateTime);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void UpdatedAtOffset_RoundTrips()
    {
        var p = NewPolicy("acme", 60);
        p.UpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero, p.UpdatedAtOffset.Offset);
        Assert.Equal(p.UpdatedAt, p.UpdatedAtOffset.UtcDateTime);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void CreatedAtOffset_SetterUpdatesUtc()
    {
        var p = NewPolicy("acme", 60);
        var target = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(-7));
        p.CreatedAtOffset = target;
        Assert.Equal(target.UtcDateTime, p.CreatedAt);
    }

    // -------- Per-tenant sweep on in-memory sequence store --------

    private static SignalRSequenceEntry MakeSeqRow(string tenantId, DateTime createdAt)
    {
        return new SignalRSequenceEntry
        {
            Id = Guid.NewGuid(),
            HubName = "TestHub",
            ConnectionId = $"c-{Guid.NewGuid():N}",
            GroupName = "g",
            Method = "m",
            Sequence = 1,
            PayloadJson = "{}",
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddHours(24),
            TenantId = tenantId,
        };
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PerTenantSweep_EvictsStaleRowsForTenantWithShorterPolicy()
    {
        var seqOptions = new SignalRSequenceStoreOptions { RetentionMinutes = 1440 };
        var seq = new InMemorySignalRSequenceStore(seqOptions);
        var policies = new InMemorySignalRRetentionPolicyStore();

        // acme = 30 minutes; default fallback = 1440.
        await policies.UpsertAsync(NewPolicy("acme", 30));

        var now = DateTime.UtcNow;
        // acme row created 45 minutes ago — beyond the 30-min policy.
        await seq.AppendAsync(MakeSeqRow("acme", now.AddMinutes(-45)));
        // bravo row created 60 minutes ago — under the 1440-min fallback.
        await seq.AppendAsync(MakeSeqRow("bravo", now.AddMinutes(-60)));

        var removed = await seq.SweepExpiredWithPerTenantPolicyAsync(
            now, policies, globalFallbackMinutes: 1440);

        Assert.Equal(1, removed);
        Assert.Equal(1, await seq.CountAsync());
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PerTenantSweep_TenantlessRowsFollowGlobalFallback()
    {
        var seq = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions { RetentionMinutes = 60 });
        var policies = new InMemorySignalRRetentionPolicyStore();

        var now = DateTime.UtcNow;
        // Tenantless row created 90 minutes ago — beyond the 60-min global.
        await seq.AppendAsync(MakeSeqRow(string.Empty, now.AddMinutes(-90)));

        var removed = await seq.SweepExpiredWithPerTenantPolicyAsync(
            now, policies, globalFallbackMinutes: 60);

        Assert.Equal(1, removed);
        Assert.Equal(0, await seq.CountAsync());
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PerTenantSweep_KeepsRowsUnderPolicy()
    {
        var seq = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions { RetentionMinutes = 1440 });
        var policies = new InMemorySignalRRetentionPolicyStore();
        await policies.UpsertAsync(NewPolicy("acme", 30));

        var now = DateTime.UtcNow;
        // acme row 5 minutes ago — under the 30-min policy.
        await seq.AppendAsync(MakeSeqRow("acme", now.AddMinutes(-5)));

        var removed = await seq.SweepExpiredWithPerTenantPolicyAsync(
            now, policies, globalFallbackMinutes: 1440);

        Assert.Equal(0, removed);
        Assert.Equal(1, await seq.CountAsync());
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PerTenantSweep_ZeroPolicy_FallsBackToGlobal()
    {
        var seq = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions { RetentionMinutes = 60 });
        var policies = new InMemorySignalRRetentionPolicyStore();
        // Zero-minute policy → invalid → fall back to global (60 min).
        await policies.UpsertAsync(NewPolicy("acme", 0));

        var now = DateTime.UtcNow;
        await seq.AppendAsync(MakeSeqRow("acme", now.AddMinutes(-30)));

        var removed = await seq.SweepExpiredWithPerTenantPolicyAsync(
            now, policies, globalFallbackMinutes: 60);

        Assert.Equal(0, removed);
        Assert.Equal(1, await seq.CountAsync());
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PerTenantSweep_NullPolicyStore_Throws()
    {
        var seq = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions { RetentionMinutes = 60 });
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => seq.SweepExpiredWithPerTenantPolicyAsync(DateTime.UtcNow, null!, 60));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PerTenantSweep_MultipleTenants_AppliesIndividualPolicies()
    {
        var seq = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions { RetentionMinutes = 1440 });
        var policies = new InMemorySignalRRetentionPolicyStore();
        await policies.UpsertAsync(NewPolicy("free", 30));
        await policies.UpsertAsync(NewPolicy("pro", 240));

        var now = DateTime.UtcNow;
        await seq.AppendAsync(MakeSeqRow("free", now.AddMinutes(-45)));   // expired
        await seq.AppendAsync(MakeSeqRow("free", now.AddMinutes(-15)));   // alive
        await seq.AppendAsync(MakeSeqRow("pro", now.AddMinutes(-300)));   // expired
        await seq.AppendAsync(MakeSeqRow("pro", now.AddMinutes(-100)));   // alive
        await seq.AppendAsync(MakeSeqRow("enterprise", now.AddMinutes(-1500))); // expired (global)
        await seq.AppendAsync(MakeSeqRow("enterprise", now.AddMinutes(-500))); // alive (global)

        var removed = await seq.SweepExpiredWithPerTenantPolicyAsync(
            now, policies, globalFallbackMinutes: 1440);

        Assert.Equal(3, removed);
        Assert.Equal(3, await seq.CountAsync());
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task PerTenantSweep_ZeroGlobalFallback_UsesDefault()
    {
        var seq = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions
        {
            RetentionMinutes = SignalRSequenceStoreOptions.DefaultRetentionMinutes,
        });
        var policies = new InMemorySignalRRetentionPolicyStore();

        var now = DateTime.UtcNow;
        await seq.AppendAsync(MakeSeqRow(string.Empty, now.AddMinutes(-1)));

        // Pass zero — should clamp up to DefaultRetentionMinutes (60).
        var removed = await seq.SweepExpiredWithPerTenantPolicyAsync(
            now, policies, globalFallbackMinutes: 0);

        Assert.Equal(0, removed);
    }
}
