using StackExchange.Redis;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 13 — Bishop. Redis-backed sliding-window OAuth
/// introspection rate-limiter. The W12 in-memory limiter enforces
/// the cap per replica, which means a hostile client gets
/// <c>replicas × capacity</c> requests per window before the gate
/// engages. W13 wires Redis as a shared coordination point: every
/// replica writes timestamps to a single sorted-set keyed on
/// <c>mahjong:oauth-introspect:{clientId}</c> and reads the rolling
/// count via <c>ZCARD</c>.
///
/// <para>The Redis script is intentionally tiny — three commands:
/// <list type="bullet">
///   <item><c>ZREMRANGEBYSCORE key -inf cutoff</c> — drop timestamps
///         older than <c>now - window</c>.</item>
///   <item><c>ZCARD key</c> — current rolling count.</item>
///   <item><c>ZADD key now unique-token</c> + <c>EXPIRE key window</c>
///         — only when the count is below capacity, otherwise the
///         caller sees the deny.</item>
/// </list>
/// </para>
///
/// <para>The store falls back to the supplied
/// <see cref="OAuthIntrospectRateLimiter"/> (constructed without a
/// shared state) on any Redis error so a transient outage degrades
/// to per-replica enforcement instead of failing open. Operators
/// see the warning in the log stream and can investigate without
/// the limiter dropping every request.</para>
///
/// <para>See <c>docs/oauth-introspect-rate-limit.md §2 "Multi-replica
/// deployment"</c>.</para>
/// </summary>
public sealed class RedisOAuthIntrospectRateLimiter : IOAuthIntrospectRateLimiter
{
    /// <summary>Redis key prefix — namespaces our sorted-sets so the
    /// limiter doesn't collide with other Redis consumers sharing the
    /// same logical database.</summary>
    public const string KeyPrefix = "mahjong:oauth-introspect:";

    private readonly IConnectionMultiplexer _redis;
    private readonly OAuthIntrospectRateLimiter _fallback;
    private readonly ILogger<RedisOAuthIntrospectRateLimiter> _logger;
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly int _databaseIndex;

    public RedisOAuthIntrospectRateLimiter(
        IConnectionMultiplexer redis,
        int capacity,
        int windowSeconds,
        ILogger<RedisOAuthIntrospectRateLimiter> logger,
        int databaseIndex = -1)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = capacity > 0 ? capacity : OAuthIntrospectRateLimitOptions.DefaultRateLimitPerClient;
        _window = TimeSpan.FromSeconds(windowSeconds > 0 ? windowSeconds : OAuthIntrospectRateLimitOptions.DefaultWindowSeconds);
        _databaseIndex = databaseIndex;
        _fallback = new OAuthIntrospectRateLimiter(_capacity, (int)_window.TotalSeconds);
        _logger.LogInformation(
            "RedisOAuthIntrospectRateLimiter wired (capacity={Capacity}, window={WindowSeconds}s, fallback=InMemory).",
            _capacity, _window.TotalSeconds);
    }

    public int RequestsPerWindow => _capacity;

    public int WindowSeconds => (int)_window.TotalSeconds;

    public OAuthIntrospectRateLimitDecision TryAcquire(string clientId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(clientId)) clientId = "_anonymous";
        var key = KeyPrefix + clientId;
        var nowMs = now.ToUnixTimeMilliseconds();
        var cutoffMs = nowMs - (long)_window.TotalMilliseconds;
        try
        {
            var db = _redis.GetDatabase(_databaseIndex);
            // 1) Trim expired timestamps before counting.
            db.SortedSetRemoveRangeByScore(key, double.NegativeInfinity, cutoffMs);
            // 2) Rolling count.
            var current = db.SortedSetLength(key);
            if (current >= _capacity)
            {
                // Oldest in-window stamp drives the Retry-After hint.
                var oldest = db.SortedSetRangeByScoreWithScores(key, take: 1);
                var retryAfterSeconds = 1;
                if (oldest.Length > 0)
                {
                    var oldestMs = (long)oldest[0].Score;
                    var rollOffMs = oldestMs + (long)_window.TotalMilliseconds - nowMs;
                    retryAfterSeconds = (int)Math.Max(1, Math.Ceiling(rollOffMs / 1000.0));
                }
                return OAuthIntrospectRateLimitDecision.Deny(retryAfterSeconds);
            }
            // 3) Insert this request's timestamp. The member must be
            //    unique so two requests in the same millisecond don't
            //    collapse to a single sorted-set entry — append a
            //    cryptographic nonce to the score string.
            var member = $"{nowMs}:{Guid.NewGuid():N}";
            db.SortedSetAdd(key, member, nowMs);
            db.KeyExpire(key, _window + TimeSpan.FromSeconds(1));
            var remaining = (int)Math.Max(0, _capacity - (current + 1));
            return OAuthIntrospectRateLimitDecision.Allow(remaining);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Redis OAuth-introspect rate-limiter read/write failed for clientId={ClientId}; falling back to in-memory limiter.",
                clientId);
            return _fallback.TryAcquire(clientId, now);
        }
    }
}
