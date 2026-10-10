using System.Diagnostics.Metrics;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Bishop;

/// <summary>
/// Phase K Wave 11 — Bishop. Hard-asserted contract for the
/// age-at-publish histogram added to
/// <see cref="SignalRBackpressureBroadcaster{THub}"/>.
///
/// <list type="number">
///   <item>Histogram name is pinned to
///         <c>signalr_message_age_at_publish_seconds</c>.</item>
///   <item>Histogram unit is the SI second symbol "s".</item>
///   <item><see cref="SignalRBackpressureBroadcaster{THub}.AgeAtPublishBuckets"/>
///         vocabulary is exactly
///         <c>[0.01, 0.05, 0.1, 0.5, 1, 5, 10]</c>.</item>
///   <item>A successful publish records exactly one
///         observation.</item>
///   <item>The observation carries the <c>hub</c> tag set to
///         the SignalR hub type name.</item>
///   <item>The observation is non-negative.</item>
///   <item>Multiple publishes accumulate multiple observations
///         (one per publish).</item>
///   <item>A broadcaster constructed without a meter factory
///         publishes without recording the histogram (no
///         exception).</item>
/// </list>
/// </summary>
public sealed class SignalRAgeAtPublishHistogramFacts
{
    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public async Task HistogramName_IsPinned()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance, meterFactory: meterFactory);
        await b.PublishAsync("g", "M", 1);

        Assert.Contains(listener.Instruments,
            i => i.Name == "signalr_message_age_at_publish_seconds");
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public async Task HistogramUnit_IsSeconds()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance, meterFactory: meterFactory);
        await b.PublishAsync("g", "M", 1);

        var inst = listener.Instruments.FirstOrDefault(
            i => i.Name == "signalr_message_age_at_publish_seconds");
        Assert.NotNull(inst);
        Assert.Equal("s", inst!.Unit);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public void AgeAtPublishBuckets_VocabularyIsPinned()
    {
        var buckets = SignalRBackpressureBroadcaster<FakeHub>.AgeAtPublishBuckets;
        Assert.Equal(new double[] { 0.01, 0.05, 0.1, 0.5, 1, 5, 10 }, buckets);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public async Task PublishAsync_RecordsOneObservation()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance, meterFactory: meterFactory);

        await b.PublishAsync("g", "M", 1);

        var obs = listener.Observations
            .Where(o => o.Name == "signalr_message_age_at_publish_seconds")
            .ToList();
        Assert.Single(obs);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public async Task PublishAsync_RecordedObservationCarriesHubTag()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance, meterFactory: meterFactory);

        await b.PublishAsync("g", "M", 1);

        var obs = listener.Observations
            .First(o => o.Name == "signalr_message_age_at_publish_seconds");
        Assert.Contains(obs.Tags, t => t.Key == "hub" && (t.Value?.ToString() == "FakeHub"));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public async Task PublishAsync_RecordedObservationIsNonNegative()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance, meterFactory: meterFactory);

        await b.PublishAsync("g", "M", 1);

        var obs = listener.Observations
            .First(o => o.Name == "signalr_message_age_at_publish_seconds");
        Assert.True(obs.Value >= 0);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public async Task MultiplePublishes_AccumulateObservations()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(SignalRBackpressureBroadcaster<FakeHub>.MeterName);
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance, meterFactory: meterFactory);

        await b.PublishAsync("g", "M", 1);
        await b.PublishAsync("g", "M", 2);
        await b.PublishAsync("g", "M", 3);

        var obs = listener.Observations
            .Where(o => o.Name == "signalr_message_age_at_publish_seconds")
            .ToList();
        Assert.Equal(3, obs.Count);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-11")]
    public async Task NoMeterFactory_PublishStillSucceeds()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance);

        var ok = await b.PublishAsync("g", "M", 1);
        Assert.True(ok);
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) =>
            Task.CompletedTask;
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
        public List<Instrument> Instruments { get; } = new();
        public List<(string Name, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)> Observations { get; } = new();
        private readonly MeterListener _listener = new();

        public TestMeterListener(string meterName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    lock (Instruments) Instruments.Add(instrument);
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((inst, value, tags, state) =>
            {
                lock (Observations)
                {
                    var snapshot = new List<KeyValuePair<string, object?>>();
                    foreach (var t in tags) snapshot.Add(new(t.Key, t.Value));
                    Observations.Add((inst.Name, value, snapshot));
                }
            });
            _listener.Start();
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
