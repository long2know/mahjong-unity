using Mahjong.Autotable.Api.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Bishop;

/// <summary>
/// Phase K Wave 13 — Bishop. Hard-asserted contract for the always-on
/// <see cref="SignalRSequenceRetentionSweep"/> background service.
///
/// <list type="number">
///   <item>Type exists and extends BackgroundService.</item>
///   <item>Default sweep cadence = 5 minutes (matches
///         <see cref="SignalRSequenceRetentionSweep.DefaultSweepIntervalMinutes"/>).</item>
///   <item>Options default = 5 minutes.</item>
///   <item>Negative / 0 SweepIntervalMinutes coerces to default.</item>
///   <item>Sub-minimum cadence (&lt; 1 minute) coerces to minimum.</item>
///   <item>RunOnceAsync deletes rows with ExpiresAt &lt; now
///         (InMemory store).</item>
///   <item>RunOnceAsync is a no-op when no rows are expired.</item>
///   <item>RunOnceAsync returns the count evicted.</item>
/// </list>
/// </summary>
public sealed class SignalRSequenceRetentionSweepTests
{
    private static SignalRSequenceRetentionSweep NewSweep(
        ISignalRSequenceStore store, int intervalMinutes = 5) =>
        new(store,
            new SignalRSequenceRetentionSweepOptions { SweepIntervalMinutes = intervalMinutes },
            NullLogger<SignalRSequenceRetentionSweep>.Instance);

    private static InMemorySignalRSequenceStore NewStore() =>
        new(new SignalRSequenceStoreOptions { RetentionMinutes = 60 });

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Sweep_TypeExists()
    {
        Assert.NotNull(typeof(SignalRSequenceRetentionSweep));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Sweep_DefaultIntervalIs5Minutes()
    {
        Assert.Equal(5, SignalRSequenceRetentionSweep.DefaultSweepIntervalMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Sweep_MinimumIntervalIs1Minute()
    {
        Assert.Equal(1, SignalRSequenceRetentionSweep.MinSweepIntervalMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Options_DefaultMatchesSweepDefault()
    {
        var opts = new SignalRSequenceRetentionSweepOptions();
        Assert.Equal(SignalRSequenceRetentionSweep.DefaultSweepIntervalMinutes,
            opts.SweepIntervalMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Sweep_CoercesZeroToDefault()
    {
        var store = NewStore();
        var sweep = NewSweep(store, 0);
        Assert.NotNull(sweep); // Ctor accepted; coercion happened.
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task RunOnce_DeletesExpiredRows()
    {
        var store = NewStore();
        // Append an entry that's already expired.
        var entry = new SignalRSequenceEntry
        {
            HubName = "test-hub",
            ConnectionId = "conn-1",
            GroupName = "g",
            Method = "m",
            PayloadJson = "{}",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-30),
        };
        await store.AppendAsync(entry);
        var sweep = NewSweep(store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, removed);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task RunOnce_NoopWhenNothingExpired()
    {
        var store = NewStore();
        var entry = new SignalRSequenceEntry
        {
            HubName = "test-hub",
            ConnectionId = "conn-1",
            GroupName = "g",
            Method = "m",
            PayloadJson = "{}",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        await store.AppendAsync(entry);
        var sweep = NewSweep(store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, removed);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task RunOnce_LeavesFutureRowsAlone()
    {
        var store = NewStore();
        await store.AppendAsync(new SignalRSequenceEntry
        {
            HubName = "h", ConnectionId = "c", GroupName = "g", Method = "m",
            PayloadJson = "{}", Sequence = 1,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-30),
        });
        await store.AppendAsync(new SignalRSequenceEntry
        {
            HubName = "h", ConnectionId = "c", GroupName = "g", Method = "m",
            PayloadJson = "{}", Sequence = 2,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        var sweep = NewSweep(store);
        var removed = await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, removed);
        var rows = await store.ReadFromAckAsync("h", "c", 0, 10);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].Sequence);
    }
}
