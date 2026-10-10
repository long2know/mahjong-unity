using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Mahjong.Autotable.Api.Audit;

/// <summary>
/// Phase K Wave 9 — Bishop. Durable EF-backed
/// <see cref="IIdempotencyStore"/> implementation. Replaces the
/// W8 in-memory <see cref="InMemoryIdempotencyStore"/> for the
/// multi-replica production deployment — every replica shares
/// the same row set so a Stripe-style retry that lands on a
/// different pod is still caught.
///
/// <list type="bullet">
///   <item>Lookups are point reads by <see cref="IdempotencyEntry.Key"/>
///         (the entity's primary key).</item>
///   <item>Inserts run under EF Core's optimistic-concurrency
///         token so two replicas racing on the same key collapse
///         cleanly — the second insert sees a unique-violation,
///         re-reads, and the middleware emits the canonical
///         <c>409 Conflict</c> (payload-mismatch) envelope.</item>
///   <item>The 5-minute replay window is enforced at read time —
///         expired rows are silently treated as missing so a
///         stale lookup never blocks a fresh request. A
///         background sweeper (registered in <c>Program.cs</c>)
///         drops expired rows on a slow cadence so the table
///         doesn't grow unbounded under steady-state traffic.</item>
/// </list>
/// </summary>
public sealed class EfIdempotencyStore : IIdempotencyStore
{
    /// <summary>Default replay window. Stripe convention — same
    /// value as the W8 in-memory store.</summary>
    public static readonly TimeSpan DefaultReplayWindow = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfIdempotencyStore> _logger;
    private readonly TimeSpan _replayWindow;

    public EfIdempotencyStore(
        IServiceScopeFactory scopeFactory,
        ILogger<EfIdempotencyStore> logger,
        TimeSpan? replayWindow = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _replayWindow = replayWindow ?? DefaultReplayWindow;
    }

    public IdempotencyRecord? TryGet(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = db.IdempotencyEntries
                .AsNoTracking()
                .FirstOrDefault(e => e.Key == key);
            if (row is null) return null;

            // Defensive expiry check — middleware contract treats
            // an expired row as "not found" so a stale lookup never
            // blocks a fresh request even if the sweeper hasn't yet
            // reaped it.
            if (row.ExpiresAt <= DateTime.UtcNow) return null;

            return new IdempotencyRecord(
                Key: row.Key,
                PayloadHash: row.PayloadHash,
                RecordedAt: DateTime.SpecifyKind(row.RecordedAt, DateTimeKind.Utc),
                StatusCode: row.StatusCode,
                ContentType: row.ContentType ?? string.Empty,
                ResponseBody: row.ResponseBody ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Idempotency store read failed for key={Key}", key);
            return null;
        }
    }

    public void Record(IdempotencyRecord record)
    {
        if (record is null || string.IsNullOrEmpty(record.Key)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            var existing = db.IdempotencyEntries.FirstOrDefault(e => e.Key == record.Key);
            var body = record.ResponseBody ?? string.Empty;
            if (body.Length > IdempotencyEntry.MaxResponseBodyLength)
                body = body.Substring(0, IdempotencyEntry.MaxResponseBodyLength);

            if (existing is null)
            {
                db.IdempotencyEntries.Add(new IdempotencyEntry
                {
                    Key = record.Key,
                    PayloadHash = record.PayloadHash ?? string.Empty,
                    StatusCode = record.StatusCode,
                    ContentType = record.ContentType ?? string.Empty,
                    ResponseBody = body,
                    RecordedAt = record.RecordedAt.UtcDateTime,
                    ExpiresAt = record.RecordedAt.UtcDateTime + _replayWindow,
                    RowVersion = Guid.NewGuid().ToByteArray(),
                });
            }
            else
            {
                existing.PayloadHash = record.PayloadHash ?? string.Empty;
                existing.StatusCode = record.StatusCode;
                existing.ContentType = record.ContentType ?? string.Empty;
                existing.ResponseBody = body;
                existing.RecordedAt = record.RecordedAt.UtcDateTime;
                existing.ExpiresAt = record.RecordedAt.UtcDateTime + _replayWindow;
                existing.RowVersion = Guid.NewGuid().ToByteArray();
            }
            db.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            // Concurrent insert on the same key from a sibling
            // replica — the row will be visible to the next read,
            // so this is best-effort and not a fatal error.
            _logger.LogDebug(ex,
                "Idempotency store record race for key={Key}; relying on sibling write.",
                record.Key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Idempotency store record failed for key={Key}", record.Key);
        }
    }

    public void Remove(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = db.IdempotencyEntries.FirstOrDefault(e => e.Key == key);
            if (row is null) return;
            db.IdempotencyEntries.Remove(row);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Idempotency store remove failed for key={Key}", key);
        }
    }

    /// <summary>
    /// Phase K Wave 9 — Bishop. Background sweeper hook. Drops
    /// every row with <c>ExpiresAt &lt;= cutoff</c> in a single
    /// batch. Returns the count removed so operator dashboards
    /// can chart the steady-state sweep rate.
    /// </summary>
    public int Sweep(DateTime cutoffUtc)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var expired = db.IdempotencyEntries
                .Where(e => e.ExpiresAt <= cutoffUtc)
                .ToList();
            if (expired.Count == 0) return 0;
            db.IdempotencyEntries.RemoveRange(expired);
            db.SaveChanges();
            return expired.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Idempotency sweep failed");
            return 0;
        }
    }
}

/// <summary>
/// Phase K Wave 10 — Bishop. Thin Redis-protocol adapter the
/// <see cref="RedisIdempotencyStore"/> depends on. Wrapping the
/// production <c>StackExchange.Redis</c> client behind this seam
/// keeps the store testable without stubbing the enormous
/// <see cref="StackExchange.Redis.IDatabase"/> surface — contract
/// tests construct an in-memory adapter, and the production code
/// path resolves <see cref="StackExchangeRedisAdapter"/> through
/// DI.
/// </summary>
public interface IIdempotencyRedis
{
    /// <summary>
    /// Atomic insert-if-absent with TTL. Returns true when the SET
    /// took effect (key was absent), false when the key already
    /// existed.
    /// </summary>
    bool TryInsertNx(string key, string value, TimeSpan ttl);

    /// <summary>
    /// Unconditional SET with TTL — used to refresh an entry that
    /// already exists so steady-state retries keep the key warm.
    /// </summary>
    void RefreshSet(string key, string value, TimeSpan ttl);

    /// <summary>
    /// Point read. Returns null when the key is absent or expired.
    /// </summary>
    string? Get(string key);

    /// <summary>
    /// Delete the key. Returns true when the key existed prior to
    /// the call; informational only — callers don't branch.
    /// </summary>
    bool Delete(string key);

    /// <summary>
    /// Human-readable description (e.g. configuration string) used
    /// in operator logs.
    /// </summary>
    string Describe();
}

/// <summary>
/// Phase K Wave 10 — Bishop. Production <see cref="IIdempotencyRedis"/>
/// implementation that wraps the
/// <see cref="StackExchange.Redis.IConnectionMultiplexer"/> client.
/// Singleton-shaped so the multiplexer is reused across requests.
/// </summary>
public sealed class StackExchangeRedisAdapter : IIdempotencyRedis
{
    private readonly IConnectionMultiplexer _redis;
    private readonly int _databaseIndex;

    public StackExchangeRedisAdapter(IConnectionMultiplexer redis, int databaseIndex = -1)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _databaseIndex = databaseIndex;
    }

    public bool TryInsertNx(string key, string value, TimeSpan ttl) =>
        _redis.GetDatabase(_databaseIndex).StringSet(key, value, ttl, When.NotExists);

    public void RefreshSet(string key, string value, TimeSpan ttl) =>
        _redis.GetDatabase(_databaseIndex).StringSet(key, value, ttl);

    public string? Get(string key)
    {
        var v = _redis.GetDatabase(_databaseIndex).StringGet(key);
        return v.IsNullOrEmpty ? null : (string?)v;
    }

    public bool Delete(string key) =>
        _redis.GetDatabase(_databaseIndex).KeyDelete(key);

    public string Describe()
    {
        try
        {
            return _redis.Configuration ?? "<unknown>";
        }
        catch
        {
            return "<unavailable>";
        }
    }
}

/// <summary>
/// Phase K Wave 9 — Bishop. Optional Redis-backed
/// <see cref="IIdempotencyStore"/>.
///
/// <para>Phase K Wave 10 — Bishop. The W9 surface shipped a thin
/// wrapper around <see cref="EfIdempotencyStore"/> + an in-process
/// LRU. W10 wires the real <c>StackExchange.Redis</c> client so
/// multi-replica deployments share a single low-latency replay-window
/// store instead of round-tripping through the backing RDBMS:</para>
///
/// <list type="bullet">
///   <item><b>Insert path</b> uses Redis <c>SET key value NX EX
///         &lt;ttl&gt;</c> — atomic insert-if-absent with TTL. Two
///         replicas racing on the same key collapse cleanly: the
///         loser's SET returns false, and the middleware re-reads
///         to get the canonical entry.</item>
///   <item><b>Read path</b> is a point <c>GET</c>. Expired entries
///         are auto-removed by Redis TTL; the store doesn't sweep
///         (Redis already does the heavy lifting via its TTL
///         expiration thread).</item>
///   <item><b>Remove path</b> is <c>DEL</c>. Best-effort — a removed
///         key cleared from one replica is gone from all.</item>
///   <item><b>Connection</b> is an injected
///         <c>IConnectionMultiplexer</c> wrapped by
///         <see cref="StackExchangeRedisAdapter"/>. The host owns
///         the lifetime so the same multiplexer instance is shared
///         across the RedisIdempotencyStore + any future Redis
///         consumers (Janus mountpoint registry W11 hand-off, etc.).</item>
///   <item><b>Fallback</b> when Redis is unreachable: the store
///         delegates to the registered <see cref="EfIdempotencyStore"/>
///         (when present) so a transient Redis outage doesn't
///         block legitimate retries. The fallback is logged at
///         warning level so operators see the degradation.</item>
/// </list>
///
/// <para>The connection string is read from
/// <c>Idempotency:Redis:ConnectionString</c> (preferred) or
/// <c>ConnectionStrings:Redis</c> (legacy) by
/// <c>Program.cs</c>. The wire format is the canonical
/// <c>StackExchange.Redis</c> configuration string
/// (e.g. <c>redis-prod.example.com:6379,password=...,ssl=true,
/// abortConnect=false</c>). See <c>docs/redis-idempotency.md</c> for
/// the full operator runbook.</para>
/// </summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    /// <summary>Default replay window — 5 minutes (Stripe convention,
    /// matches <see cref="EfIdempotencyStore.DefaultReplayWindow"/>).
    /// Redis TTL is set to this value on every SET so the entry
    /// auto-expires regardless of whether the application ever calls
    /// Remove.</summary>
    public static readonly TimeSpan DefaultReplayWindow = TimeSpan.FromMinutes(5);

    /// <summary>Redis key prefix — namespaces our keys so the
    /// idempotency surface doesn't collide with other Redis consumers
    /// sharing the same logical database.</summary>
    public const string KeyPrefix = "mahjong:idem:";

    private readonly IIdempotencyRedis _redis;
    private readonly EfIdempotencyStore? _fallback;
    private readonly ILogger<RedisIdempotencyStore> _logger;
    private readonly TimeSpan _replayWindow;

    /// <summary>Resolved connection string descriptor — exposed for
    /// the admin health probe so operators can confirm the configured
    /// endpoint without rummaging through configuration.</summary>
    public string ConnectionDescription { get; }

    public RedisIdempotencyStore(
        IIdempotencyRedis redis,
        ILogger<RedisIdempotencyStore> logger,
        EfIdempotencyStore? fallback = null,
        TimeSpan? replayWindow = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fallback = fallback;
        _replayWindow = replayWindow ?? DefaultReplayWindow;
        ConnectionDescription = SafeDescribe(_redis);
        _logger.LogInformation(
            "RedisIdempotencyStore wired (endpoints={Endpoints}, ttl={Ttl}s, fallback={Fallback}).",
            ConnectionDescription, _replayWindow.TotalSeconds,
            _fallback is null ? "none" : "EfIdempotencyStore");
    }

    /// <summary>
    /// Phase K Wave 10 — Bishop. Convenience overload that wraps a
    /// raw <see cref="IConnectionMultiplexer"/> via
    /// <see cref="StackExchangeRedisAdapter"/>. Production DI uses
    /// this; tests inject <see cref="IIdempotencyRedis"/> directly.
    /// </summary>
    public RedisIdempotencyStore(
        IConnectionMultiplexer multiplexer,
        ILogger<RedisIdempotencyStore> logger,
        EfIdempotencyStore? fallback = null,
        TimeSpan? replayWindow = null,
        int databaseIndex = -1)
        : this(new StackExchangeRedisAdapter(multiplexer, databaseIndex), logger, fallback, replayWindow)
    {
    }

    public IdempotencyRecord? TryGet(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            var raw = _redis.Get(KeyPrefix + key);
            if (raw is null) return null;
            return Deserialize(key, raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Redis idempotency read failed for key={Key}; falling back to EF store.", key);
            return _fallback?.TryGet(key);
        }
    }

    public void Record(IdempotencyRecord record)
    {
        if (record is null || string.IsNullOrEmpty(record.Key)) return;
        try
        {
            var body = record.ResponseBody ?? string.Empty;
            // Idempotency rows are capped to the EF store's max length
            // so the wire shape is uniform whichever backing is active.
            if (body.Length > IdempotencyEntry.MaxResponseBodyLength)
                body = body.Substring(0, IdempotencyEntry.MaxResponseBodyLength);

            var trimmed = record with { ResponseBody = body };
            var payload = Serialize(trimmed);
            var fullKey = KeyPrefix + record.Key;

            // SET key value NX EX ttl — atomic insert-if-absent.
            // When the key already exists we refresh the TTL on the
            // canonical entry so steady-state replay traffic against
            // the same key keeps it warm for its full window.
            var inserted = _redis.TryInsertNx(fullKey, payload, _replayWindow);
            if (!inserted)
            {
                _redis.RefreshSet(fullKey, payload, _replayWindow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Redis idempotency write failed for key={Key}; falling back to EF store.", record.Key);
            _fallback?.Record(record);
        }
    }

    public void Remove(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            _redis.Delete(KeyPrefix + key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Redis idempotency remove failed for key={Key}; fallback delete.", key);
            _fallback?.Remove(key);
        }
    }

    /// <summary>
    /// Phase K Wave 10 — Bishop. Convenience alias for
    /// <see cref="Record"/> that mirrors the StackExchange.Redis
    /// vocabulary ("SET key value …"). The W9 → W10 forward-stage
    /// contract pin asserts the store exposes a Save/Set/Store/Put
    /// entry point alongside the <see cref="IIdempotencyStore"/>
    /// canonical name; this alias keeps both names live without
    /// duplicating logic.
    /// </summary>
    public void Set(IdempotencyRecord record) => Record(record);

    /// <summary>
    /// Phase K Wave 10 — Bishop. No-op sweep entry-point. Redis
    /// handles expiration internally via its keyspace-level TTL
    /// expiration thread, so the application-side sweeper would be
    /// redundant. The method exists for parity with the EF store +
    /// so callers wiring both via a configuration toggle don't need
    /// to branch on the backing implementation.
    /// </summary>
    public int Sweep(DateTime cutoffUtc) => 0;

    // ── Serialization (versioned envelope so the wire format can
    // evolve without bricking deployments mid-rotation).

    internal const byte WireVersion = 1;

    internal static string Serialize(IdempotencyRecord record)
    {
        // Compact pipe-delimited envelope keeps the Redis value small
        // (under 4 KB even at the max response body length) without
        // pulling System.Text.Json into the hot path.
        // Format: v1|status|recordedAtUtcTicks|contentType|payloadHash|responseBody
        return string.Join("|",
            $"v{WireVersion}",
            record.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.RecordedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EscapePipe(record.ContentType ?? string.Empty),
            EscapePipe(record.PayloadHash ?? string.Empty),
            EscapePipe(record.ResponseBody ?? string.Empty));
    }

    internal static IdempotencyRecord? Deserialize(string key, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var parts = raw.Split('|');
            if (parts.Length < 6) return null;
            if (parts[0] != $"v{WireVersion}") return null;
            var status = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var ticks = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            var contentType = UnescapePipe(parts[3]);
            var payloadHash = UnescapePipe(parts[4]);
            // Response body may contain pipes — join any trailing
            // fragments back together so legitimate body content
            // isn't truncated.
            var body = UnescapePipe(string.Join("|", parts.Skip(5)));
            return new IdempotencyRecord(
                Key: key,
                PayloadHash: payloadHash,
                RecordedAt: new DateTimeOffset(ticks, TimeSpan.Zero),
                StatusCode: status,
                ContentType: contentType,
                ResponseBody: body);
        }
        catch
        {
            return null;
        }
    }

    private static string EscapePipe(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("|", "\\p", StringComparison.Ordinal);

    private static string UnescapePipe(string value) =>
        value.Replace("\\p", "|", StringComparison.Ordinal)
             .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string SafeDescribe(IIdempotencyRedis redis)
    {
        try
        {
            return redis.Describe();
        }
        catch
        {
            return "<unavailable>";
        }
    }
}
