using Mahjong.Autotable.Api.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Lifecycle contract for the new
/// <see cref="JanusMountpointRegistry"/> + the hosted
/// <see cref="JanusMountpointLifecycleService"/>.
///
/// <list type="number">
///   <item>A fresh registry has zero entries.</item>
///   <item><c>RegisterJoin</c> adds an entry with the
///         deterministic mountpoint id.</item>
///   <item>Re-registering the same table id is idempotent —
///         active-spectator count increments.</item>
///   <item><c>RecordLeave</c> decrements the spectator count but
///         does not evict.</item>
///   <item><c>Sweep</c> evicts entries idle past the TTL.</item>
///   <item><c>Sweep</c> does not evict entries with active
///         spectators, regardless of age.</item>
///   <item>The lifecycle service exposes its
///         <see cref="JanusMountpointLifecycleService.SweepInterval"/>
///         + <see cref="JanusMountpointLifecycleService.IdleTtl"/>
///         defaults.</item>
///   <item><c>RunOnce</c> evicts everything past the TTL in a
///         single call (deterministic — no timer race).</item>
///   <item><c>Evict</c> force-removes an entry regardless of
///         TTL.</item>
///   <item><c>TryGet</c> returns null for an unregistered table
///         id.</item>
///   <item>Concurrent <c>RegisterJoin</c> calls collapse to a
///         single registry entry per table id.</item>
/// </list>
/// </summary>
public sealed class JanusMountpointLifecycleTests
{
    private static JanusMountpointRegistry NewRegistry(DateTimeOffset? clock = null)
    {
        if (clock is null) return new JanusMountpointRegistry();
        var now = clock.Value;
        return new JanusMountpointRegistry(() => now);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void FreshRegistry_HasZeroEntries()
    {
        var reg = NewRegistry();
        Assert.Equal(0, reg.Count);
        Assert.Empty(reg.Entries);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void RegisterJoin_AddsEntry_WithDeterministicMountpointId()
    {
        var reg = NewRegistry();
        var entry = reg.RegisterJoin("table-42");
        Assert.Equal("table-42", entry.TableId);
        Assert.Equal(
            JanusSpectatorVoiceHub.ComputeMountpointId("table-42"),
            entry.MountpointId);
        Assert.Equal(1, entry.ActiveSpectators);
        Assert.Equal(1, reg.Count);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void RegisterJoin_TwiceForSameTable_IsIdempotent_AndIncrementsSpectators()
    {
        var reg = NewRegistry();
        var first = reg.RegisterJoin("table-42");
        var second = reg.RegisterJoin("table-42");
        Assert.Equal(first.MountpointId, second.MountpointId);
        Assert.Equal(2, second.ActiveSpectators);
        Assert.Equal(1, reg.Count); // still only one entry
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void RecordLeave_DecrementsCount_DoesNotEvict()
    {
        var reg = NewRegistry();
        reg.RegisterJoin("table-42");
        reg.RegisterJoin("table-42");
        var modified = reg.RecordLeave("table-42");
        Assert.True(modified);
        var snap = reg.TryGet("table-42");
        Assert.NotNull(snap);
        Assert.Equal(1, snap!.ActiveSpectators);
        Assert.Equal(1, reg.Count); // entry persists
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void Sweep_EvictsIdleEntries_PastTtl()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var reg = new JanusMountpointRegistry(() => clock);
        reg.RegisterJoin("table-42");
        reg.RecordLeave("table-42"); // spectators back to 0

        clock = t0 + TimeSpan.FromMinutes(10);
        var evicted = reg.Sweep(TimeSpan.FromMinutes(5));
        Assert.Single(evicted);
        Assert.Equal("table-42", evicted[0].TableId);
        Assert.Equal(0, reg.Count);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void Sweep_DoesNotEvict_EntriesWithActiveSpectators()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var reg = new JanusMountpointRegistry(() => clock);
        reg.RegisterJoin("table-42"); // 1 active spectator
        clock = t0 + TimeSpan.FromHours(2);
        var evicted = reg.Sweep(TimeSpan.FromMinutes(5));
        Assert.Empty(evicted);
        Assert.Equal(1, reg.Count);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void LifecycleService_DefaultsArePinned()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), JanusMountpointLifecycleService.DefaultSweepInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), JanusMountpointLifecycleService.DefaultIdleTtl);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void RunOnce_EvictsExpiredEntries()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var reg = new JanusMountpointRegistry(() => clock);
        reg.RegisterJoin("a");
        reg.RegisterJoin("b");
        reg.RecordLeave("a");
        reg.RecordLeave("b");
        clock = t0 + TimeSpan.FromMinutes(10);

        var svc = new JanusMountpointLifecycleService(
            reg,
            NullLogger<JanusMountpointLifecycleService>.Instance,
            sweepInterval: TimeSpan.FromSeconds(60),
            idleTtl: TimeSpan.FromMinutes(5));
        var evicted = svc.RunOnce();
        Assert.Equal(2, evicted);
        Assert.Equal(0, reg.Count);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void Evict_RemovesEntry_RegardlessOfTtl()
    {
        var reg = NewRegistry();
        reg.RegisterJoin("table-42");
        Assert.True(reg.Evict("table-42"));
        Assert.Equal(0, reg.Count);
        Assert.False(reg.Evict("nonexistent"));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void TryGet_UnregisteredTable_ReturnsNull()
    {
        var reg = NewRegistry();
        Assert.Null(reg.TryGet("never-registered"));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public async Task ConcurrentRegisterJoin_CollapsesToSingleEntry()
    {
        var reg = NewRegistry();
        var tasks = Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => reg.RegisterJoin("hot-table"))).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(1, reg.Count);
        var entry = reg.TryGet("hot-table");
        Assert.NotNull(entry);
        Assert.Equal(32, entry!.ActiveSpectators);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-10")]
    public void LifecycleService_ExposesSweepIntervalAndTtl()
    {
        var reg = new JanusMountpointRegistry();
        var svc = new JanusMountpointLifecycleService(
            reg,
            NullLogger<JanusMountpointLifecycleService>.Instance,
            sweepInterval: TimeSpan.FromSeconds(7),
            idleTtl: TimeSpan.FromMinutes(3));
        Assert.Equal(TimeSpan.FromSeconds(7), svc.SweepInterval);
        Assert.Equal(TimeSpan.FromMinutes(3), svc.IdleTtl);
    }
}
