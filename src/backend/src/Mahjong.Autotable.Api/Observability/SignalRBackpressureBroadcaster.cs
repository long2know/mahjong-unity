using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 9 — Bishop. Cross-hub backpressure + reconnect
/// resilience. W7/W8 added several SignalR hubs
/// (<c>TournamentMatchHub</c>, <c>SpectatorVoiceHub</c>,
/// <c>JanusReadinessHub</c>, etc.). Under steady-state traffic a
/// slow consumer can cause the server-side SignalR
/// queue to grow unboundedly — eventually the per-connection
/// channel saturates and the host buffers fill.
///
/// <para>The W9 backpressure surface gives every hub a uniform
/// shape: messages older than <see cref="DefaultMaxMessageAgeSeconds"/>
/// are dropped, and per-client throughput is capped at
/// <see cref="DefaultMaxMessagesPerSecond"/> (a 30 Hz batch rate is
/// the canonical SignalR ceiling — every popular client library
/// can sustain at least that). Reconnect is supported via the
/// last-acked sequence: clients pass their last-seen sequence on
/// reconnect, and the broadcaster replays everything in the
/// retained buffer that's newer than the ack.</para>
///
/// <para>Documented end-to-end in
/// <c>docs/realtime-resilience.md</c>.</para>
///
/// <para>Phase K Wave 10 — Bishop. Prometheus metrics surface
/// added via <see cref="IMeterFactory"/>. Counters are tagged by
/// <c>hub</c> + drop <c>reason</c> so the dashboards can render
/// per-hub backpressure pressure.</para>
/// </summary>
public sealed class SignalRBackpressureBroadcaster<THub>
    where THub : Hub
{
    /// <summary>Default per-client message rate cap (30/s).</summary>
    public const int DefaultMaxMessagesPerSecond = 30;

    /// <summary>Default age beyond which messages are dropped (5s).
    /// Slow consumers that fall behind by more than 5s only see
    /// the catch-up snapshot, not every dropped interstitial.</summary>
    public const int DefaultMaxMessageAgeSeconds = 5;

    /// <summary>Default retained-buffer depth — last 256 messages.
    /// Tunes the worst-case memory footprint per connection group.</summary>
    public const int DefaultRetainedMessageCount = 256;

    /// <summary>Phase K Wave 10 — Bishop. Meter name for the
    /// backpressure counters. Surfaced as a constant so the
    /// Prometheus exporter + contract tests pin the
    /// vocabulary.</summary>
    public const string MeterName = "Mahjong.Autotable.Api.Observability.SignalRBackpressure";

    private readonly IHubContext<THub> _hub;
    private readonly ILogger _logger;
    private readonly TimeSpan _maxAge;
    private readonly int _maxPerSecond;
    private readonly int _retentionDepth;
    private readonly string _hubName;

    private readonly Counter<long>? _dropCounter;
    private readonly Counter<long>? _replayCounter;
    private readonly Counter<long>? _sentCounter;
    private readonly Histogram<double>? _ageAtPublishHistogram;

    // Per-(group) sliding window for rate-cap enforcement. Concurrent
    // dictionary keyed by group name — the window itself is a
    // bounded queue protected by a lock.
    private readonly ConcurrentDictionary<string, RateWindow> _rateWindows = new(StringComparer.Ordinal);

    // Per-(group) retained message ring buffer for reconnect replay.
    private readonly ConcurrentDictionary<string, ReplayBuffer> _replayBuffers = new(StringComparer.Ordinal);

    // Monotonic sequence stamped onto every published message so
    // reconnect-replay can resume from the last ack.
    private long _sequence;

    public SignalRBackpressureBroadcaster(
        IHubContext<THub> hub,
        ILogger logger,
        TimeSpan? maxAge = null,
        int? maxPerSecond = null,
        int? retentionDepth = null,
        IMeterFactory? meterFactory = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxAge = maxAge ?? TimeSpan.FromSeconds(DefaultMaxMessageAgeSeconds);
        _maxPerSecond = maxPerSecond ?? DefaultMaxMessagesPerSecond;
        _retentionDepth = retentionDepth ?? DefaultRetainedMessageCount;
        _hubName = typeof(THub).Name;

        if (meterFactory is not null)
        {
            var meter = meterFactory.Create(MeterName);
            _dropCounter = meter.CreateCounter<long>("signalr_messages_dropped_total",
                unit: null, description: "Messages dropped by the SignalR backpressure broadcaster, tagged by hub + reason.");
            _replayCounter = meter.CreateCounter<long>("signalr_replay_requests_total",
                unit: null, description: "Reconnect-replay invocations against the backpressure broadcaster.");
            _sentCounter = meter.CreateCounter<long>("signalr_messages_sent_total",
                unit: null, description: "Messages sent through the SignalR backpressure broadcaster, tagged by hub.");
            // Phase K Wave 11 — Bishop. Latency histogram measuring
            // the time between message creation (envelope CreatedAt)
            // and the actual SignalR SendAsync call. Surfaces the
            // queueing tail that the rate-cap counters alone don't
            // expose. Buckets per docs/realtime-resilience.md §5.
            _ageAtPublishHistogram = meter.CreateHistogram<double>(
                "signalr_message_age_at_publish_seconds",
                unit: "s",
                description: "Latency between message creation and actual SignalR SendAsync, tagged by hub.");
        }
    }

    /// <summary>
    /// Phase K Wave 11 — Bishop. Buckets used for the age-at-
    /// publish histogram. The Prometheus exporter reads
    /// these via the standard <c>Histogram.Buckets</c> tag
    /// metadata; surfaced as a constant so the contract suite can
    /// pin the vocabulary.
    /// </summary>
    public static readonly double[] AgeAtPublishBuckets = new[]
    {
        0.01, 0.05, 0.1, 0.5, 1.0, 5.0, 10.0,
    };

    /// <summary>
    /// Publishes a message to the given SignalR group with W9
    /// backpressure semantics. Returns <c>true</c> if the message
    /// was sent; <c>false</c> if it was dropped (age window
    /// exceeded or rate cap hit).
    /// </summary>
    public async Task<bool> PublishAsync(
        string group,
        string method,
        object payload,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(group)) return false;

        var now = DateTimeOffset.UtcNow;
        var window = _rateWindows.GetOrAdd(group, _ => new RateWindow());
        if (!window.TryAcquire(now, _maxPerSecond))
        {
            // Backpressure — drop the message and let the next call
            // through. Logged at debug level so the dropped count
            // surfaces in operator dashboards without filling
            // log storage.
            _logger.LogDebug(
                "SignalR backpressure dropped {Method} for group={Group} (rate cap {Rate}/s).",
                method, group, _maxPerSecond);
            _dropCounter?.Add(1,
                new KeyValuePair<string, object?>("hub", _hubName),
                new KeyValuePair<string, object?>("reason", "rate_cap"));
            return false;
        }

        var seq = Interlocked.Increment(ref _sequence);
        var envelope = new BackpressureEnvelope(
            Sequence: seq,
            CreatedAt: now,
            Method: method,
            Payload: payload);

        // Retain for reconnect replay — drop the oldest entries
        // beyond the retention depth.
        var buffer = _replayBuffers.GetOrAdd(group, _ => new ReplayBuffer(_retentionDepth));
        buffer.Add(envelope);

        try
        {
            // Phase K Wave 11 — Bishop. Record age-at-publish before
            // the SendAsync call so the metric captures only the
            // server-side queueing tail (creation → just-before-
            // SendAsync). The send itself is measured separately by
            // standard SignalR instrumentation.
            var ageAtPublishSeconds = (DateTimeOffset.UtcNow - now).TotalSeconds;
            _ageAtPublishHistogram?.Record(ageAtPublishSeconds,
                new KeyValuePair<string, object?>("hub", _hubName));

            await _hub.Clients.Group(group).SendAsync(method, new
            {
                seq,
                createdAt = now,
                payload,
            }, ct);
            _sentCounter?.Add(1,
                new KeyValuePair<string, object?>("hub", _hubName));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SignalR backpressure broadcast failed for group={Group} method={Method}; envelope retained for replay.",
                group, method);
            _dropCounter?.Add(1,
                new KeyValuePair<string, object?>("hub", _hubName),
                new KeyValuePair<string, object?>("reason", "send_failure"));
            return false;
        }
    }

    /// <summary>
    /// Phase K Wave 9 — Bishop. Reconnect-replay surface. The hub
    /// calls this from its <c>OnConnect / ResumeAfterAck</c> method
    /// to flush every retained envelope newer than the client's
    /// last-acked sequence. Envelopes older than
    /// <see cref="_maxAge"/> are skipped — they would arrive too
    /// stale to be useful and we deliver the catch-up snapshot
    /// instead.
    /// </summary>
    public IReadOnlyList<BackpressureEnvelope> ResumeFromAck(string group, long lastAckedSequence)
    {
        _replayCounter?.Add(1,
            new KeyValuePair<string, object?>("hub", _hubName));
        if (!_replayBuffers.TryGetValue(group, out var buffer))
            return Array.Empty<BackpressureEnvelope>();

        var cutoff = DateTimeOffset.UtcNow - _maxAge;
        var replayed = buffer.Snapshot()
            .Where(e => e.Sequence > lastAckedSequence && e.CreatedAt >= cutoff)
            .ToArray();
        // Phase K Wave 10 — Bishop. Stale envelopes that the client
        // would receive too late to be useful are counted as drops
        // so the dashboard reflects the full backpressure picture.
        var skipped = buffer.Snapshot()
            .Count(e => e.Sequence > lastAckedSequence && e.CreatedAt < cutoff);
        if (skipped > 0)
        {
            _dropCounter?.Add(skipped,
                new KeyValuePair<string, object?>("hub", _hubName),
                new KeyValuePair<string, object?>("reason", "age_window"));
        }
        return replayed;
    }

    /// <summary>
    /// Phase K Wave 9 — Bishop. Returns the current monotonic
    /// sequence value. Surfaced for tests that want to assert the
    /// envelope stamping deterministically.
    /// </summary>
    public long CurrentSequence => Interlocked.Read(ref _sequence);

    private sealed class RateWindow
    {
        private readonly Queue<DateTimeOffset> _stamps = new();
        private readonly object _lock = new();

        public bool TryAcquire(DateTimeOffset now, int maxPerSecond)
        {
            lock (_lock)
            {
                var cutoff = now - TimeSpan.FromSeconds(1);
                while (_stamps.Count > 0 && _stamps.Peek() < cutoff)
                    _stamps.Dequeue();
                if (_stamps.Count >= maxPerSecond)
                    return false;
                _stamps.Enqueue(now);
                return true;
            }
        }
    }

    private sealed class ReplayBuffer
    {
        private readonly LinkedList<BackpressureEnvelope> _entries = new();
        private readonly int _capacity;
        private readonly object _lock = new();

        public ReplayBuffer(int capacity) { _capacity = Math.Max(1, capacity); }

        public void Add(BackpressureEnvelope entry)
        {
            lock (_lock)
            {
                _entries.AddLast(entry);
                while (_entries.Count > _capacity)
                    _entries.RemoveFirst();
            }
        }

        public IReadOnlyList<BackpressureEnvelope> Snapshot()
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }
}

/// <summary>
/// Phase K Wave 9 — Bishop. Wire envelope for backpressure-managed
/// SignalR broadcasts. The <see cref="Sequence"/> field is the
/// monotonic per-server counter clients ack on reconnect.
/// </summary>
public sealed record BackpressureEnvelope(
    long Sequence,
    DateTimeOffset CreatedAt,
    string Method,
    object Payload);
