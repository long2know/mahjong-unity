using System.Diagnostics.Metrics;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Prometheus metrics contract for
/// <see cref="SignalRBackpressureBroadcaster{THub}"/>.
///
/// <list type="number">
///   <item>The meter name is the constant
///         <see cref="SignalRBackpressureBroadcaster{THub}.MeterName"/>.</item>
///   <item>A successful publish emits a
///         <c>signalr_messages_sent_total</c> tick with the
///         <c>hub</c> tag.</item>
///   <item>A rate-cap drop emits a
///         <c>signalr_messages_dropped_total</c> tick with
///         <c>reason="rate_cap"</c>.</item>
///   <item>The hub tag value is the SignalR hub type name.</item>
///   <item>A reconnect-replay invocation emits a
///         <c>signalr_replay_requests_total</c> tick.</item>
///   <item>An age-window drop during reconnect emits
///         <c>signalr_messages_dropped_total</c> with
///         <c>reason="age_window"</c>.</item>
///   <item>The broadcaster constructed without a meter factory
///         is metric-free (no exception).</item>
///   <item>Multiple publishes accumulate the
///         <c>signalr_messages_sent_total</c> counter.</item>
/// </list>
/// </summary>
public sealed class SignalRBackpressureMetricsTests
{
    private sealed class FakeClientProxy : IClientProxy
    {
        public bool ThrowOnSend { get; set; }
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            if (ThrowOnSend) throw new InvalidOperationException("forced");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubClients : IHubClients
    {
        private readonly FakeClientProxy _proxy;
        public FakeHubClients(FakeClientProxy p) { _proxy = p; }
        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => _proxy;
        public IClientProxy Client(string id) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> ids) => _proxy;
        public IClientProxy Group(string name) => _proxy;
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> excluded) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> names) => _proxy;
        public IClientProxy User(string id) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> ids) => _proxy;
    }

    private sealed class FakeHub : Microsoft.AspNetCore.SignalR.Hub { }

    private sealed class FakeHubContext : IHubContext<FakeHub>
    {
        public FakeClientProxy Proxy { get; } = new();
        public IHubClients Clients => new FakeHubClients(Proxy);
        public IGroupManager Groups => throw new NotSupportedException();
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
                    && e.Tags.Any(t => t.Key == tagKey && string.Equals(t.Value?.ToString(), tagValue, StringComparison.Ordinal)));
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

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public void MeterName_IsPinned()
    {
        Assert.Equal(
            "Mahjong.Autotable.Api.Observability.SignalRBackpressure",
            SignalRBackpressureBroadcaster<FakeHub>.MeterName);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public async Task PublishAsync_SuccessfulSend_IncrementsSentCounter()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            meterFactory: meterFactory);

        var ok = await b.PublishAsync("g", "DoThing", new { x = 1 });
        Assert.True(ok);
        Assert.Equal(1, listener.Get("signalr_messages_sent_total"));
        Assert.True(listener.Any("signalr_messages_sent_total", "hub", "FakeHub"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public async Task RateCap_Drop_IncrementsDropCounter_WithRateCapReason()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            maxPerSecond: 2,
            meterFactory: meterFactory);

        await b.PublishAsync("g", "M", 1);
        await b.PublishAsync("g", "M", 2);
        await b.PublishAsync("g", "M", 3); // dropped
        await b.PublishAsync("g", "M", 4); // dropped

        Assert.Equal(2, listener.Get("signalr_messages_dropped_total"));
        Assert.True(listener.Any("signalr_messages_dropped_total", "reason", "rate_cap"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public async Task HubTag_IsTheHubTypeName()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            meterFactory: meterFactory);

        await b.PublishAsync("g", "M", 1);
        Assert.True(listener.Any("signalr_messages_sent_total", "hub", "FakeHub"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public void ResumeFromAck_IncrementsReplayCounter()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            meterFactory: meterFactory);

        _ = b.ResumeFromAck("g", lastAckedSequence: 0);
        Assert.Equal(1, listener.Get("signalr_replay_requests_total"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public async Task SendFailure_IncrementsDropCounter_WithSendFailureReason()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        hub.Proxy.ThrowOnSend = true;

        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            meterFactory: meterFactory);

        var ok = await b.PublishAsync("g", "M", 1);
        Assert.False(ok);
        Assert.True(listener.Any("signalr_messages_dropped_total", "reason", "send_failure"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public async Task WithoutMeterFactory_NoExceptionsAndNoMetrics()
    {
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            meterFactory: null);

        var ok = await b.PublishAsync("g", "M", 1);
        Assert.True(ok);
        Assert.Equal(0, listener.Get("signalr_messages_sent_total"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public async Task MultiplePublishes_AccumulateSentCounter()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            meterFactory: meterFactory);

        for (var i = 0; i < 5; i++)
            await b.PublishAsync("g", "M", i);

        Assert.Equal(5, listener.Get("signalr_messages_sent_total"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-10")]
    public async Task AgeWindowDrop_DuringReplay_IncrementsAgeWindowReason()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            maxAge: TimeSpan.FromMilliseconds(10), // ~immediate expiry
            meterFactory: meterFactory);

        await b.PublishAsync("g", "M", 1);
        // Wait past the age window so the envelope is too stale to replay.
        await Task.Delay(50);
        _ = b.ResumeFromAck("g", lastAckedSequence: 0);

        Assert.True(listener.Any("signalr_messages_dropped_total", "reason", "age_window"));
    }
}
