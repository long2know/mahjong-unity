using System.Collections.Concurrent;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 12 — Bishop. Per-client_id sliding window rate
/// limiter for the RFC 7662 token-introspection endpoint at
/// <c>POST /api/auth/introspect</c>. The W11 introspect surface
/// was unlimited; W12 adds a fixed-cap-per-client-per-60s
/// window so a single misbehaving verifier can't pin the
/// JwtValidationService.
///
/// <para>The default cap is 100 requests / client / 60s (matches
/// the canonical <c>docs/oauth-introspect-rate-limit.md</c>
/// guidance). Operators tune via
/// <c>OAuth:Introspect:RateLimitPerClient</c> +
/// <c>OAuth:Introspect:RateLimitWindowSeconds</c>.</para>
/// </summary>
public interface IOAuthIntrospectRateLimiter
{
    /// <summary>Attempt to acquire a slot for the supplied
    /// <paramref name="clientId"/>. Returns
    /// <see cref="OAuthIntrospectRateLimitDecision.Allowed"/>
    /// when the request fits inside the window. Returns
    /// <see cref="OAuthIntrospectRateLimitDecision.Denied"/>
    /// with a <see cref="OAuthIntrospectRateLimitDecision.RetryAfterSeconds"/>
    /// hint when the cap is hit.</summary>
    OAuthIntrospectRateLimitDecision TryAcquire(string clientId, DateTimeOffset now);

    /// <summary>Maximum request count per client per window.
    /// Surfaced so the controller can stamp the standard
    /// <c>X-RateLimit-Limit</c> response header.</summary>
    int RequestsPerWindow { get; }

    /// <summary>Window size in seconds. Default 60.</summary>
    int WindowSeconds { get; }
}

/// <summary>
/// Phase K Wave 12 — Bishop. Decision envelope returned by
/// <see cref="IOAuthIntrospectRateLimiter.TryAcquire"/>. When
/// <see cref="Allowed"/> is <c>true</c>, <see cref="Remaining"/>
/// carries the residual budget in the window; when <c>false</c>,
/// <see cref="RetryAfterSeconds"/> carries the canonical
/// <c>Retry-After</c> hint.
/// </summary>
public readonly record struct OAuthIntrospectRateLimitDecision(
    bool Allowed,
    int Remaining,
    int RetryAfterSeconds)
{
    public static OAuthIntrospectRateLimitDecision Allow(int remaining) =>
        new(true, remaining, 0);

    public static OAuthIntrospectRateLimitDecision Deny(int retryAfterSeconds) =>
        new(false, 0, retryAfterSeconds);
}

/// <summary>
/// Phase K Wave 12 — Bishop. Default in-memory implementation.
/// Singleton-shaped — one shared instance per host. Per-client
/// state lives in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// of <see cref="SlidingWindow"/> records; each window keeps a
/// bounded queue of timestamps so the rolling count is O(W) per
/// request — W is small (≤ the configured cap).
///
/// <para>Multi-replica deployments will swap this for a
/// Redis-backed implementation in W13; the W12 surface is
/// intentionally narrow so the swap is a one-line DI flip.</para>
/// </summary>
public sealed class OAuthIntrospectRateLimiter : IOAuthIntrospectRateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindow> _windows =
        new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _window;

    public OAuthIntrospectRateLimiter(int capacity, int windowSeconds)
    {
        _capacity = capacity > 0 ? capacity : 100;
        _window = TimeSpan.FromSeconds(windowSeconds > 0 ? windowSeconds : 60);
    }

    public int RequestsPerWindow => _capacity;

    public int WindowSeconds => (int)_window.TotalSeconds;

    public OAuthIntrospectRateLimitDecision TryAcquire(string clientId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(clientId)) clientId = "_anonymous";
        var window = _windows.GetOrAdd(clientId, _ => new SlidingWindow());
        return window.TryAcquire(now, _capacity, _window);
    }

    private sealed class SlidingWindow
    {
        private readonly Queue<DateTimeOffset> _stamps = new();
        private readonly object _lock = new();

        public OAuthIntrospectRateLimitDecision TryAcquire(
            DateTimeOffset now,
            int capacity,
            TimeSpan window)
        {
            lock (_lock)
            {
                var cutoff = now - window;
                while (_stamps.Count > 0 && _stamps.Peek() < cutoff)
                {
                    _stamps.Dequeue();
                }
                if (_stamps.Count >= capacity)
                {
                    var oldest = _stamps.Peek();
                    var retryAfter = (int)Math.Ceiling((oldest + window - now).TotalSeconds);
                    return OAuthIntrospectRateLimitDecision.Deny(Math.Max(1, retryAfter));
                }
                _stamps.Enqueue(now);
                var remaining = capacity - _stamps.Count;
                return OAuthIntrospectRateLimitDecision.Allow(remaining);
            }
        }
    }
}

/// <summary>
/// Phase K Wave 12 — Bishop. Configuration bound from the
/// <c>OAuth:Introspect</c> section.
/// </summary>
public sealed class OAuthIntrospectRateLimitOptions
{
    /// <summary>Default cap per client per window. Matches
    /// <c>docs/oauth-introspect-rate-limit.md</c>.</summary>
    public const int DefaultRateLimitPerClient = 100;

    /// <summary>Default sliding window in seconds.</summary>
    public const int DefaultWindowSeconds = 60;

    /// <summary>Maximum requests per client per
    /// <see cref="WindowSeconds"/>. 0 = use the default
    /// (<see cref="DefaultRateLimitPerClient"/>).</summary>
    public int RateLimitPerClient { get; set; } = DefaultRateLimitPerClient;

    /// <summary>Sliding window size in seconds. 0 = use the
    /// default (<see cref="DefaultWindowSeconds"/>).</summary>
    public int WindowSeconds { get; set; } = DefaultWindowSeconds;

    /// <summary>
    /// Phase K Wave 13 — Bishop. Implementation selector —
    /// case-insensitive. <c>"InMemory"</c> (default) uses
    /// <see cref="OAuthIntrospectRateLimiter"/>; <c>"Redis"</c>
    /// uses <see cref="RedisOAuthIntrospectRateLimiter"/> which
    /// shares a single sliding-window across all replicas via a
    /// Redis sorted-set. The Redis path falls back to the in-memory
    /// limiter when Redis is unreachable. See
    /// <c>docs/oauth-introspect-rate-limit.md §2</c>.
    /// </summary>
    public string LimiterImpl { get; set; } = "InMemory";
}
