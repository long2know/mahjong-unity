using Mahjong.Autotable.Api.Auth;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Hard-asserted contract for the
/// per-tenant JWKS rotation surface
/// (<see cref="IPerTenantJwksRotationStore"/> +
/// <see cref="PerTenantJwksRotationPolicy"/>).
///
/// <list type="number">
///   <item>Upsert inserts a new row when absent.</item>
///   <item>Upsert updates an existing row in place.</item>
///   <item>Get returns null for an unknown tenant.</item>
///   <item>Get returns a previously-stored row.</item>
///   <item>List returns rows ordered by tenant id.</item>
///   <item>Count reflects stored row count.</item>
///   <item>UpdatedAt is bumped on upsert.</item>
///   <item>IsWithinOverlapWindow true for an inside-window timestamp.</item>
///   <item>IsWithinOverlapWindow false for a before-window timestamp.</item>
///   <item>IsWithinOverlapWindow false for an after-window timestamp.</item>
///   <item>RotationStartUtc / RotationCompleteUtc are DateTimeOffset
///         (preserved offset across round-trip).</item>
///   <item>Empty TenantId rejects with ArgumentException.</item>
///   <item>Options.Enabled defaults to false.</item>
///   <item>Options.StorageImpl defaults to "InMemory".</item>
/// </list>
/// </summary>
public sealed class PerTenantJwksRotationStoreTests
{
    private static PerTenantJwksRotationPolicy NewPolicy(string tenantId, DateTimeOffset? start = null)
    {
        var s = start ?? DateTimeOffset.UtcNow.AddHours(-1);
        return new PerTenantJwksRotationPolicy
        {
            TenantId = tenantId,
            ActiveKid = "kid-active",
            PreviousKid = "kid-previous",
            RotationStartUtc = s,
            RotationCompleteUtc = s.AddDays(7),
        };
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Upsert_InsertsRow_WhenAbsent()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var stored = await store.UpsertAsync(NewPolicy("tenant-A"));
        Assert.Equal("tenant-A", stored.TenantId);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Upsert_UpdatesRow_InPlace()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(NewPolicy("tenant-A"));
        var updated = NewPolicy("tenant-A");
        updated.ActiveKid = "kid-rotated";
        await store.UpsertAsync(updated);
        var got = await store.GetAsync("tenant-A");
        Assert.NotNull(got);
        Assert.Equal("kid-rotated", got!.ActiveKid);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Get_ReturnsNull_ForUnknownTenant()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        Assert.Null(await store.GetAsync("tenant-Z"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Get_ReturnsStoredRow()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(NewPolicy("tenant-A"));
        var got = await store.GetAsync("tenant-A");
        Assert.NotNull(got);
        Assert.Equal("tenant-A", got!.TenantId);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task List_OrderedByTenantId()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(NewPolicy("tenant-C"));
        await store.UpsertAsync(NewPolicy("tenant-A"));
        await store.UpsertAsync(NewPolicy("tenant-B"));
        var rows = await store.ListAsync();
        Assert.Equal(new[] { "tenant-A", "tenant-B", "tenant-C" },
            rows.Select(r => r.TenantId).ToArray());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Count_ReflectsRowCount()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        Assert.Equal(0, await store.CountAsync());
        await store.UpsertAsync(NewPolicy("tenant-A"));
        await store.UpsertAsync(NewPolicy("tenant-B"));
        Assert.Equal(2, await store.CountAsync());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task UpdatedAt_IsBumped_OnUpsert()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var first = await store.UpsertAsync(NewPolicy("tenant-A"));
        var firstUpdated = first.UpdatedAt;
        await Task.Delay(20);
        var second = await store.UpsertAsync(NewPolicy("tenant-A"));
        Assert.True(second.UpdatedAt >= firstUpdated);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_InsideWindow_True()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var policy = new PerTenantJwksRotationPolicy
        {
            TenantId = "tenant-A",
            RotationStartUtc = start,
            RotationCompleteUtc = start.AddDays(7),
        };
        Assert.True(policy.IsWithinOverlapWindow(start.AddDays(3)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_BeforeStart_False()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var policy = new PerTenantJwksRotationPolicy
        {
            TenantId = "tenant-A",
            RotationStartUtc = start,
            RotationCompleteUtc = start.AddDays(7),
        };
        Assert.False(policy.IsWithinOverlapWindow(start.AddHours(-1)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_AfterComplete_False()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var policy = new PerTenantJwksRotationPolicy
        {
            TenantId = "tenant-A",
            RotationStartUtc = start,
            RotationCompleteUtc = start.AddDays(7),
        };
        Assert.False(policy.IsWithinOverlapWindow(start.AddDays(7).AddSeconds(1)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void RotationEdges_AreDateTimeOffset_PreservingOffset()
    {
        // The W14 path used DateTime; W15 widens to DateTimeOffset so
        // tenant ops teams scheduling rotations in their local
        // timezone keep the offset across persistence.
        var prop = typeof(PerTenantJwksRotationPolicy)
            .GetProperty(nameof(PerTenantJwksRotationPolicy.RotationStartUtc))!;
        Assert.Equal(typeof(DateTimeOffset), prop.PropertyType);
        var prop2 = typeof(PerTenantJwksRotationPolicy)
            .GetProperty(nameof(PerTenantJwksRotationPolicy.RotationCompleteUtc))!;
        Assert.Equal(typeof(DateTimeOffset), prop2.PropertyType);

        var local = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(-8));
        var policy = new PerTenantJwksRotationPolicy
        {
            TenantId = "t",
            RotationStartUtc = local,
            RotationCompleteUtc = local.AddDays(1),
        };
        Assert.Equal(TimeSpan.FromHours(-8), policy.RotationStartUtc.Offset);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Upsert_RejectsEmptyTenantId()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(new PerTenantJwksRotationPolicy { TenantId = "" }));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Options_EnabledDefaultsToFalse()
    {
        var opts = new PerTenantJwksRotationOptions();
        Assert.False(opts.Enabled);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Options_StorageImplDefaultsToInMemory()
    {
        var opts = new PerTenantJwksRotationOptions();
        Assert.Equal("InMemory", opts.StorageImpl);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task Get_EmptyTenantId_ReturnsNull()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        Assert.Null(await store.GetAsync(""));
    }
}
