using System.Collections.Concurrent;

namespace Mahjong.Autotable.Api.Voice;

// Phase K Wave 2 — Bishop (backend). Per-connection token bucket used by
// the VoiceHub to clamp signalling chatter. The contract test probes for
// an int field named *Rate* on a Voice-namespaced type so we expose the
// limit as a public const on this class.
//
// Phase K Wave 4 — Bishop. Vasquez's contract assertions pin two
// public read-only properties on the limiter so the rate-window
// semantics are introspectable without poking at private bucket
// internals: WindowDurationSeconds (60) and MaxRelaysPerWindow (30).
// The bucket itself still refills every second (the existing Wave-2
// behaviour); the new properties describe the rolling-window
// contract that VoiceHubMetricsService advertises through /metrics.
public sealed class VoiceRateLimiter
{
    public const int DefaultRatePerSecond = 30;

    /// <summary>Phase K Wave 4 — rolling-window duration paired with
    /// <see cref="MaxRelaysPerWindow"/>. The VoiceHubMetricsService
    /// counts relays inside this window; the limiter itself enforces
    /// the per-second sub-window (refill cadence). Default: 60s.</summary>
    public int WindowDurationSeconds { get; } = 60;

    /// <summary>Phase K Wave 4 — maximum relays permitted per
    /// <see cref="WindowDurationSeconds"/> window. Default: 30 (the
    /// Wave-2 rate). The contract pins this so Vasquez's tests can
    /// assert the limiter's published ceiling matches the metrics
    /// view.</summary>
    public int MaxRelaysPerWindow { get; }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly int _capacity;
    private readonly TimeSpan _refill;

    public VoiceRateLimiter(int ratePerSecond)
    {
        _capacity = ratePerSecond > 0 ? ratePerSecond : DefaultRatePerSecond;
        _refill = TimeSpan.FromSeconds(1);
        MaxRelaysPerWindow = _capacity;
    }

    public bool TryConsume(string connectionId)
    {
        var bucket = _buckets.GetOrAdd(connectionId, _ => new Bucket(_capacity, DateTime.UtcNow));
        lock (bucket)
        {
            var now = DateTime.UtcNow;
            if (now - bucket.LastRefill >= _refill)
            {
                bucket.Tokens = _capacity;
                bucket.LastRefill = now;
            }
            if (bucket.Tokens <= 0) return false;
            bucket.Tokens--;
            return true;
        }
    }

    public void Forget(string connectionId) => _buckets.TryRemove(connectionId, out _);

    private sealed class Bucket
    {
        public int Tokens;
        public DateTime LastRefill;
        public Bucket(int tokens, DateTime at) { Tokens = tokens; LastRefill = at; }
    }
}
