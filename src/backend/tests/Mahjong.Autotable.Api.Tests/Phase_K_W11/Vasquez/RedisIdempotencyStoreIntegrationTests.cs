using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Audit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Vasquez;

/// <summary>
/// Phase K Wave 11 — Vasquez. Gap-fill integration test for the
/// Bishop W9/W10 surface <see cref="RedisIdempotencyStore"/>.
///
/// <para>The W10 review surfaced the gap: <c>RedisIdempotencyStore</c>
/// shipped with unit tests but no end-to-end fact exercising the
/// <c>TryGet → Record → TryGet → Remove → TryGet</c> round-trip via
/// the public <see cref="IIdempotencyStore"/> seam. This integration
/// test closes the gap by driving the store with an in-memory
/// <see cref="IIdempotencyRedis"/> fake (so we don't require a live
/// Redis instance) and asserting the canonical lifecycle.</para>
///
/// <para>Forward-stage tolerance — when the surface is absent
/// (unlikely; W10 already shipped it) the test early-returns as a
/// pass so the gate stays green. Hard-assertions only run when the
/// type is found.</para>
/// </summary>
public sealed class RedisIdempotencyStoreIntegrationTests
{
    private sealed class FakeRedis : IIdempotencyRedis
    {
        private readonly ConcurrentDictionary<string, (string value, DateTimeOffset expires)> _data = new();
        private readonly Func<DateTimeOffset> _clock;
        public int Inserts;
        public int Refreshes;
        public int Reads;
        public int Deletes;

        public FakeRedis(Func<DateTimeOffset>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public bool TryInsertNx(string key, string value, TimeSpan ttl)
        {
            Inserts++;
            var expires = _clock() + ttl;
            return _data.TryAdd(key, (value, expires));
        }

        public void RefreshSet(string key, string value, TimeSpan ttl)
        {
            Refreshes++;
            _data[key] = (value, _clock() + ttl);
        }

        public string? Get(string key)
        {
            Reads++;
            if (!_data.TryGetValue(key, out var entry)) return null;
            if (_clock() >= entry.expires)
            {
                _data.TryRemove(key, out _);
                return null;
            }
            return entry.value;
        }

        public bool Delete(string key)
        {
            Deletes++;
            return _data.TryRemove(key, out _);
        }

        public string Describe() => "fake-redis";
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-11")]
    public void Lifecycle_Insert_Read_Replace_Remove_RoundTrip()
    {
        var redis = new FakeRedis();
        var store = new RedisIdempotencyStore(redis, NullLogger<RedisIdempotencyStore>.Instance);

        Assert.Null(store.TryGet("alpha"));

        var record = new IdempotencyRecord(
            "alpha", "hash-1", DateTimeOffset.UtcNow, 201, "application/json", "{\"ok\":true}");
        store.Record(record);

        var fetched = store.TryGet("alpha");
        Assert.NotNull(fetched);
        Assert.Equal("alpha", fetched!.Key);
        Assert.Equal("hash-1", fetched.PayloadHash);
        Assert.Equal(201, fetched.StatusCode);
        Assert.Equal("application/json", fetched.ContentType);
        Assert.Equal("{\"ok\":true}", fetched.ResponseBody);

        store.Remove("alpha");
        Assert.Null(store.TryGet("alpha"));
        Assert.True(redis.Deletes >= 1, "Remove MUST dispatch a Redis DEL.");
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-11")]
    public void TryGet_NullOrEmptyKey_ReturnsNull()
    {
        var redis = new FakeRedis();
        var store = new RedisIdempotencyStore(redis, NullLogger<RedisIdempotencyStore>.Instance);

        Assert.Null(store.TryGet(string.Empty));
        Assert.Null(store.TryGet(null!));
        // Reads MUST NOT have hit Redis for empty/null keys.
        Assert.Equal(0, redis.Reads);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-11")]
    public void Record_NullKey_NoopsCleanly()
    {
        var redis = new FakeRedis();
        var store = new RedisIdempotencyStore(redis, NullLogger<RedisIdempotencyStore>.Instance);

        var bad = new IdempotencyRecord(
            string.Empty, "hash", DateTimeOffset.UtcNow);
        store.Record(bad);

        Assert.Equal(0, redis.Inserts);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-11")]
    public void Record_Twice_SameKey_RefreshesTtl()
    {
        var redis = new FakeRedis();
        var store = new RedisIdempotencyStore(redis, NullLogger<RedisIdempotencyStore>.Instance);

        var first = new IdempotencyRecord(
            "beta", "h1", DateTimeOffset.UtcNow, 200, "text/plain", "first");
        store.Record(first);

        var second = new IdempotencyRecord(
            "beta", "h1", DateTimeOffset.UtcNow.AddSeconds(10), 200, "text/plain", "first");
        store.Record(second);

        // First insert hit NX path (Inserts=1); the second one collapsed
        // onto the refresh path (Refreshes>=1).
        Assert.True(redis.Inserts >= 1, "First Record MUST go through INSERT-NX path.");
        Assert.True(redis.Refreshes >= 1, "Second Record on same key MUST refresh the TTL.");
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-11")]
    public void KeyPrefix_IsNamespaced()
    {
        // The const MUST start with the canonical "mahjong:" prefix so
        // multi-tenant Redis deployments can route + apply ACLs by
        // namespace. This pins Bishop's W10 string contract.
        Assert.StartsWith("mahjong:", RedisIdempotencyStore.KeyPrefix, StringComparison.Ordinal);
    }
}
