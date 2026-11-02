using Mahjong.Autotable.Api.Spectator;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Hard-asserted contract for the
/// <see cref="SpectatorHandoffAuditRetentionSweep"/> background
/// service.
///
/// <list type="number">
///   <item>RunOnceAsync drops rows older than retention.</item>
///   <item>RunOnceAsync keeps rows inside retention window.</item>
///   <item>RunOnceAsync no-op on empty store.</item>
///   <item>RunOnceAsync uses DefaultRetentionDays when options &lt;= 0.</item>
///   <item>SweepIntervalMinutes default is 5.</item>
///   <item>DefaultSweepIntervalMinutes constant is 5.</item>
///   <item>RunOnceAsync count matches removed-row count.</item>
///   <item>Options null-arg throws.</item>
///   <item>Store null-arg throws.</item>
///   <item>Logger null-arg throws.</item>
/// </list>
/// </summary>
public sealed class SpectatorHandoffAuditRetentionSweepTests
{
    private static SpectatorHandoffAuditRetentionSweep NewSweep(
        SpectatorHandoffAuditOptions options,
        ISpectatorHandoffAuditStore store) =>
        new(store, options, NullLogger<SpectatorHandoffAuditRetentionSweep>.Instance);

    private static async Task SeedAsync(ISpectatorHandoffAuditStore store, DateTime issuedAt)
    {
        await store.InsertAsync(new SpectatorHandoffAuditRecord
        {
            UserId = Guid.NewGuid().ToString("N"),
            GameId = Guid.NewGuid(),
            TokenJti = Guid.NewGuid().ToString("N"),
            IssuedAt = issuedAt,
            Scope = "spectator:test",
        });
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_DropsRowsOlderThanRetention()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-31));
        await SeedAsync(store, DateTime.UtcNow.AddDays(-31));
        var sweep = NewSweep(new SpectatorHandoffAuditOptions { RetentionDays = 30 }, store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, removed);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_KeepsRowsInsideRetentionWindow()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-5));
        var sweep = NewSweep(new SpectatorHandoffAuditOptions { RetentionDays = 30 }, store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_EmptyStore_NoOp()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        var sweep = NewSweep(new SpectatorHandoffAuditOptions(), store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_UsesDefaultRetention_WhenOptionsZero()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        // Seed older than default (30d) but younger than a forced absurd value.
        await SeedAsync(store, DateTime.UtcNow.AddDays(-90));
        await SeedAsync(store, DateTime.UtcNow.AddDays(-5));
        var sweep = NewSweep(new SpectatorHandoffAuditOptions { RetentionDays = 0 }, store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, removed);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Options_SweepIntervalMinutes_DefaultIs5()
    {
        Assert.Equal(5, new SpectatorHandoffAuditOptions().SweepIntervalMinutes);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void DefaultSweepIntervalMinutes_ConstantIs5()
    {
        Assert.Equal(5, SpectatorHandoffAuditRetentionSweep.DefaultSweepIntervalMinutes);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_CountMatchesRemoved()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        for (var i = 0; i < 7; i++)
        {
            await SeedAsync(store, DateTime.UtcNow.AddDays(-100));
        }
        var sweep = NewSweep(new SpectatorHandoffAuditOptions { RetentionDays = 30 }, store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(7, removed);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Ctor_NullOptions_Throws()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        Assert.Throws<ArgumentNullException>(() =>
            new SpectatorHandoffAuditRetentionSweep(
                store, null!,
                NullLogger<SpectatorHandoffAuditRetentionSweep>.Instance));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Ctor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SpectatorHandoffAuditRetentionSweep(
                null!, new SpectatorHandoffAuditOptions(),
                NullLogger<SpectatorHandoffAuditRetentionSweep>.Instance));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void Ctor_NullLogger_Throws()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        Assert.Throws<ArgumentNullException>(() =>
            new SpectatorHandoffAuditRetentionSweep(
                store, new SpectatorHandoffAuditOptions(), null!));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public async Task RunOnceAsync_DeletesOnlyEligibleRows()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        await SeedAsync(store, DateTime.UtcNow.AddDays(-100));
        await SeedAsync(store, DateTime.UtcNow.AddDays(-5));
        var sweep = NewSweep(new SpectatorHandoffAuditOptions { RetentionDays = 30 }, store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, removed);
    }
}
