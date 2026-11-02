using System.Diagnostics.Metrics;
using Mahjong.Autotable.Api.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Bishop;

/// <summary>
/// Phase K Wave 11 — Bishop. Hard-asserted contract for the
/// mountpoint-eviction metric emitted by
/// <see cref="JanusMountpointLifecycleService"/>.
///
/// <list type="number">
///   <item>Meter name is pinned to
///         <c>Mahjong.Autotable.Api.Voice.JanusMountpoint</c>.</item>
///   <item>Counter name is pinned to
///         <c>signalr_mountpoint_evictions_total</c>.</item>
///   <item>Idle-sweep eviction emits the counter tagged
///         <c>reason="idle"</c>.</item>
///   <item><see cref="JanusMountpointLifecycleService.EvictForGameEnded"/>
///         emits the counter tagged
///         <c>reason="gameEnded"</c>.</item>
///   <item><see cref="JanusMountpointLifecycleService.EvictAllForJanusUnhealthy"/>
///         emits the counter tagged
///         <c>reason="janusUnhealthy"</c> once per mountpoint
///         evicted.</item>
///   <item><see cref="MountpointEvictionReason.Idle"/> /
///         <see cref="MountpointEvictionReason.GameEnded"/> /
///         <see cref="MountpointEvictionReason.JanusUnhealthy"/>
///         constants are pinned to the canonical strings.</item>
///   <item>Service constructed without an IMeterFactory is
///         metric-free (no throw).</item>
///   <item><c>EvictForGameEnded</c> on a non-existent table does
///         not emit a counter tick.</item>
///   <item><c>EvictAllForJanusUnhealthy</c> on an empty registry
///         does not emit a counter tick.</item>
/// </list>
/// </summary>
public sealed class MountpointEvictionMetricsFacts
{
    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void MeterName_IsPinned()
    {
        Assert.Equal("Mahjong.Autotable.Api.Voice.JanusMountpoint",
            JanusMountpointLifecycleService.MeterName);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void CounterName_IsPinned()
    {
        Assert.Equal("signalr_mountpoint_evictions_total",
            JanusMountpointLifecycleService.EvictionCounterName);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void ReasonConstants_ArePinned()
    {
        Assert.Equal("idle", MountpointEvictionReason.Idle);
        Assert.Equal("gameEnded", MountpointEvictionReason.GameEnded);
        Assert.Equal("janusUnhealthy", MountpointEvictionReason.JanusUnhealthy);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void IdleSweep_EmitsReasonIdle()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var registry = new JanusMountpointRegistry(() => clock);
        // Seed a mountpoint then immediately drop the spectator
        // count so it qualifies for an idle sweep.
        registry.RegisterJoin("table-1");
        registry.RecordLeave("table-1");
        // Advance the simulated clock past the idle TTL.
        clock = now + TimeSpan.FromMinutes(10);

        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JanusMountpointLifecycleService.MeterName);
        var service = new JanusMountpointLifecycleService(
            registry, NullLogger<JanusMountpointLifecycleService>.Instance,
            sweepInterval: null, idleTtl: TimeSpan.FromMinutes(5),
            meterFactory: meterFactory);

        var evicted = service.RunOnce();
        Assert.Equal(1, evicted);
        Assert.Equal(1, listener.Get(JanusMountpointLifecycleService.EvictionCounterName));
        Assert.True(listener.Any(
            JanusMountpointLifecycleService.EvictionCounterName,
            "reason", MountpointEvictionReason.Idle));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void EvictForGameEnded_EmitsReasonGameEnded()
    {
        var registry = new JanusMountpointRegistry();
        registry.RegisterJoin("table-1");

        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JanusMountpointLifecycleService.MeterName);
        var service = new JanusMountpointLifecycleService(
            registry, NullLogger<JanusMountpointLifecycleService>.Instance,
            meterFactory: meterFactory);

        var evicted = service.EvictForGameEnded("table-1");
        Assert.True(evicted);
        Assert.Equal(1, listener.Get(JanusMountpointLifecycleService.EvictionCounterName));
        Assert.True(listener.Any(
            JanusMountpointLifecycleService.EvictionCounterName,
            "reason", MountpointEvictionReason.GameEnded));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void EvictForGameEnded_NonExistentTable_NoCounterTick()
    {
        var registry = new JanusMountpointRegistry();

        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JanusMountpointLifecycleService.MeterName);
        var service = new JanusMountpointLifecycleService(
            registry, NullLogger<JanusMountpointLifecycleService>.Instance,
            meterFactory: meterFactory);

        var evicted = service.EvictForGameEnded("does-not-exist");
        Assert.False(evicted);
        Assert.Equal(0, listener.Get(JanusMountpointLifecycleService.EvictionCounterName));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void EvictAllForJanusUnhealthy_EmitsReasonJanusUnhealthy_PerMountpoint()
    {
        var registry = new JanusMountpointRegistry();
        registry.RegisterJoin("table-1");
        registry.RegisterJoin("table-2");
        registry.RegisterJoin("table-3");

        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JanusMountpointLifecycleService.MeterName);
        var service = new JanusMountpointLifecycleService(
            registry, NullLogger<JanusMountpointLifecycleService>.Instance,
            meterFactory: meterFactory);

        var count = service.EvictAllForJanusUnhealthy();
        Assert.Equal(3, count);
        Assert.Equal(3, listener.Get(JanusMountpointLifecycleService.EvictionCounterName));
        // All 3 ticks tagged reason=janusUnhealthy.
        var jUnhealthy = listener.Events.Count(e =>
            e.Name == JanusMountpointLifecycleService.EvictionCounterName
            && e.Tags.Any(t => t.Key == "reason"
                && string.Equals(t.Value?.ToString(), MountpointEvictionReason.JanusUnhealthy, StringComparison.Ordinal)));
        Assert.Equal(3, jUnhealthy);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void EvictAllForJanusUnhealthy_EmptyRegistry_NoCounterTick()
    {
        var registry = new JanusMountpointRegistry();

        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JanusMountpointLifecycleService.MeterName);
        var service = new JanusMountpointLifecycleService(
            registry, NullLogger<JanusMountpointLifecycleService>.Instance,
            meterFactory: meterFactory);

        var count = service.EvictAllForJanusUnhealthy();
        Assert.Equal(0, count);
        Assert.Equal(0, listener.Get(JanusMountpointLifecycleService.EvictionCounterName));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void ServiceWithoutMeterFactory_DoesNotThrow()
    {
        var registry = new JanusMountpointRegistry();
        registry.RegisterJoin("table-1");
        var service = new JanusMountpointLifecycleService(
            registry, NullLogger<JanusMountpointLifecycleService>.Instance);

        var ex = Record.Exception(() => service.EvictForGameEnded("table-1"));
        Assert.Null(ex);
    }

    private sealed class TestMeterListener : IDisposable
    {
        public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);
        public List<(string Name, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)> Events { get; } = new();
        private readonly MeterListener _listener = new();

        public TestMeterListener(string meterName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
            {
                lock (Counters)
                {
                    Counters.TryGetValue(inst.Name, out var current);
                    Counters[inst.Name] = current + value;
                    var snapshot = new List<KeyValuePair<string, object?>>();
                    foreach (var t in tags) snapshot.Add(new(t.Key, t.Value));
                    Events.Add((inst.Name, value, snapshot));
                }
            });
            _listener.Start();
        }

        public long Get(string name)
        {
            lock (Counters)
            {
                Counters.TryGetValue(name, out var v);
                return v;
            }
        }

        public bool Any(string name, string tagKey, string tagValue)
        {
            lock (Counters)
            {
                return Events.Any(e =>
                    e.Name == name
                    && e.Tags.Any(t => t.Key == tagKey
                        && string.Equals(t.Value?.ToString(), tagValue, StringComparison.Ordinal)));
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options)
        {
            var m = new Meter(options.Name, options.Version);
            _meters.Add(m);
            return m;
        }
        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
        }
    }
}
