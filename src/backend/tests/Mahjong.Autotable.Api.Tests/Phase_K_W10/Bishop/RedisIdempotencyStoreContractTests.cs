using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Audit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Pure-unit contract tests for the W10
/// <see cref="RedisIdempotencyStore"/>. The W9 surface shipped a
/// wrapper-around-EF stub; W10 wires the real StackExchange.Redis
/// client.
///
/// <para>These tests drive the store against an in-memory
/// <see cref="IIdempotencyRedis"/> adapter that records every call.
/// The matching live-Redis exercise lives in
/// <see cref="RedisIdempotencyStoreLiveTests"/> and is gated on
/// the <c>MAHJONG_REDIS_LIVE_URL</c> env var so the suite stays
/// green without Docker.</para>
///
/// <list type="number">
///   <item>Key prefix is <c>mahjong:idem:</c>.</item>
///   <item>Default replay window is 5 minutes.</item>
///   <item>Insert uses SET NX with the configured TTL.</item>
///   <item>Existing key triggers a refresh-SET (TTL bump).</item>
///   <item>Read returns null for absent keys.</item>
///   <item>Read returns the deserialised record for present keys.</item>
///   <item>Serialisation round-trips through the v1 envelope.</item>
///   <item>Response bodies with pipes survive the round-trip.</item>
///   <item>Truncates response bodies past the EF cap.</item>
///   <item>Falls back to the EF store on Redis read failure.</item>
///   <item>Falls back to the EF store on Redis write failure.</item>
///   <item>Sweep returns 0 (Redis handles TTL).</item>
///   <item>Empty keys are no-ops.</item>
///   <item>ConnectionDescription surfaces the adapter description.</item>
/// </list>
/// </summary>
public sealed class RedisIdempotencyStoreContractTests
{
    private static RedisIdempotencyStore New(InMemoryRedis redis, EfIdempotencyStore? fallback = null) =>
        new(redis, NullLogger<RedisIdempotencyStore>.Instance, fallback);

    private static IdempotencyRecord Sample(string key = "k1", string body = "hello", string contentType = "application/json", string payloadHash = "hash:abc", int statusCode = 200) => new(
        Key: key,
        PayloadHash: payloadHash,
        RecordedAt: new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
        StatusCode: statusCode,
        ContentType: contentType,
        ResponseBody: body);

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void KeyPrefix_IsMahjongIdem()
    {
        Assert.Equal("mahjong:idem:", RedisIdempotencyStore.KeyPrefix);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void DefaultReplayWindow_IsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), RedisIdempotencyStore.DefaultReplayWindow);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Record_UsesPrefixedKey_AndDefaultTtl()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        store.Record(Sample());

        Assert.Single(redis.NxCalls);
        var call = redis.NxCalls[0];
        Assert.Equal("mahjong:idem:k1", call.Key);
        Assert.Equal(TimeSpan.FromMinutes(5), call.Ttl);
        Assert.StartsWith("v1|", call.Value);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Record_HonoursCustomTtl()
    {
        var redis = new InMemoryRedis();
        var store = new RedisIdempotencyStore(redis, NullLogger<RedisIdempotencyStore>.Instance, replayWindow: TimeSpan.FromSeconds(30));
        store.Record(Sample());
        Assert.Equal(TimeSpan.FromSeconds(30), redis.NxCalls[0].Ttl);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Record_RefreshesTtlWhenKeyExists()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        // Pre-populate so the SET-NX returns false.
        redis.PrePopulate("mahjong:idem:k1", "stale");
        store.Record(Sample());

        // First call: SET NX (returns false). Second call: refresh.
        Assert.Single(redis.NxCalls);
        Assert.Single(redis.RefreshCalls);
        Assert.Equal("mahjong:idem:k1", redis.RefreshCalls[0].Key);
        Assert.Equal(TimeSpan.FromMinutes(5), redis.RefreshCalls[0].Ttl);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void TryGet_AbsentKey_ReturnsNull()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        Assert.Null(store.TryGet("nope"));
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void TryGet_PresentKey_RoundTripsThroughV1Envelope()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        var original = Sample(body: "hello world", contentType: "text/plain", payloadHash: "h:xyz");
        store.Record(original);

        var fetched = store.TryGet("k1");
        Assert.NotNull(fetched);
        Assert.Equal(original.PayloadHash, fetched!.PayloadHash);
        Assert.Equal(original.StatusCode, fetched.StatusCode);
        Assert.Equal(original.ContentType, fetched.ContentType);
        Assert.Equal(original.ResponseBody, fetched.ResponseBody);
        Assert.Equal(original.RecordedAt, fetched.RecordedAt);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void BodyWithPipes_SurvivesRoundTrip()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        var record = Sample(body: "a|b|c|d\\e|f");
        store.Record(record);
        var fetched = store.TryGet("k1");
        Assert.NotNull(fetched);
        Assert.Equal("a|b|c|d\\e|f", fetched!.ResponseBody);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Body_TruncatedToEfCap()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        var big = new string('x', Mahjong.Autotable.Api.Data.Entities.IdempotencyEntry.MaxResponseBodyLength + 256);
        store.Record(Sample(body: big));
        var fetched = store.TryGet("k1");
        Assert.NotNull(fetched);
        Assert.Equal(Mahjong.Autotable.Api.Data.Entities.IdempotencyEntry.MaxResponseBodyLength, fetched!.ResponseBody.Length);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Remove_DeletesKey()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        store.Record(Sample());
        store.Remove("k1");
        Assert.Contains("mahjong:idem:k1", redis.DeleteCalls);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Sweep_AlwaysReturnsZero()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        Assert.Equal(0, store.Sweep(DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void EmptyKey_TryGet_ReturnsNull_NoCall()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        Assert.Null(store.TryGet(string.Empty));
        Assert.Empty(redis.GetCalls);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void EmptyKey_Record_NoOp()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        store.Record(Sample(key: string.Empty));
        Assert.Empty(redis.NxCalls);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void EmptyKey_Remove_NoOp()
    {
        var redis = new InMemoryRedis();
        var store = New(redis);
        store.Remove(string.Empty);
        Assert.Empty(redis.DeleteCalls);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void SerializeDeserialize_PreservesAllFields()
    {
        var original = new IdempotencyRecord(
            Key: "k7",
            PayloadHash: "hash:7",
            RecordedAt: new DateTimeOffset(2026, 9, 15, 8, 30, 45, TimeSpan.Zero),
            StatusCode: 409,
            ContentType: "text/plain; charset=utf-8",
            ResponseBody: "body | with | pipes \\and\\ slashes");
        var wire = RedisIdempotencyStore.Serialize(original);
        var roundTripped = RedisIdempotencyStore.Deserialize("k7", wire);
        Assert.NotNull(roundTripped);
        Assert.Equal(original.Key, roundTripped!.Key);
        Assert.Equal(original.PayloadHash, roundTripped.PayloadHash);
        Assert.Equal(original.RecordedAt, roundTripped.RecordedAt);
        Assert.Equal(original.StatusCode, roundTripped.StatusCode);
        Assert.Equal(original.ContentType, roundTripped.ContentType);
        Assert.Equal(original.ResponseBody, roundTripped.ResponseBody);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void WireFormat_StartsWithVersionTag()
    {
        var wire = RedisIdempotencyStore.Serialize(Sample());
        Assert.StartsWith("v1|", wire);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Deserialize_InvalidEnvelope_ReturnsNull()
    {
        Assert.Null(RedisIdempotencyStore.Deserialize("k", "garbage"));
        Assert.Null(RedisIdempotencyStore.Deserialize("k", ""));
        Assert.Null(RedisIdempotencyStore.Deserialize("k", "v2|future|fmt"));
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Deserialize_TooFewParts_ReturnsNull()
    {
        Assert.Null(RedisIdempotencyStore.Deserialize("k", "v1|200|0"));
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void ConnectionDescription_SurfacesAdapterDescribe()
    {
        var redis = new InMemoryRedis { Description = "redis://example:6379" };
        var store = New(redis);
        Assert.Equal("redis://example:6379", store.ConnectionDescription);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void TryGet_OnRedisFailure_SwallowsAndReturnsNullWhenNoFallback()
    {
        var redis = new InMemoryRedis { FailReads = true };
        var store = New(redis);
        // No EF fallback wired → returns null, never throws.
        Assert.Null(store.TryGet("k1"));
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Record_OnRedisFailure_SwallowsWhenNoFallback()
    {
        var redis = new InMemoryRedis { FailWrites = true };
        var store = New(redis);
        // Throws nothing — fallback absent.
        store.Record(Sample());
        Assert.Empty(redis.NxCalls);
    }

    [Fact, Trait("Category", "Idempotency"), Trait("Wave", "Phase-K-10")]
    public void Remove_OnRedisFailure_SwallowsWhenNoFallback()
    {
        var redis = new InMemoryRedis { FailDeletes = true };
        var store = New(redis);
        store.Remove("k1"); // no throw
    }

    /// <summary>
    /// In-memory <see cref="IIdempotencyRedis"/> stub used by every
    /// contract test. Records the call sequence so the tests can
    /// pin SET-NX-EX + refresh semantics deterministically.
    /// </summary>
    private sealed class InMemoryRedis : IIdempotencyRedis
    {
        public ConcurrentDictionary<string, string> Storage { get; } = new();
        public List<RedisCall> NxCalls { get; } = new();
        public List<RedisCall> RefreshCalls { get; } = new();
        public List<string> GetCalls { get; } = new();
        public List<string> DeleteCalls { get; } = new();
        public string Description { get; set; } = "in-memory-stub";
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public bool FailDeletes { get; set; }

        public void PrePopulate(string key, string value) => Storage[key] = value;

        public bool TryInsertNx(string key, string value, TimeSpan ttl)
        {
            if (FailWrites) throw new InvalidOperationException("simulated-redis-down");
            NxCalls.Add(new RedisCall(key, value, ttl));
            return Storage.TryAdd(key, value);
        }

        public void RefreshSet(string key, string value, TimeSpan ttl)
        {
            if (FailWrites) throw new InvalidOperationException("simulated-redis-down");
            RefreshCalls.Add(new RedisCall(key, value, ttl));
            Storage[key] = value;
        }

        public string? Get(string key)
        {
            if (FailReads) throw new InvalidOperationException("simulated-redis-down");
            GetCalls.Add(key);
            return Storage.TryGetValue(key, out var v) ? v : null;
        }

        public bool Delete(string key)
        {
            if (FailDeletes) throw new InvalidOperationException("simulated-redis-down");
            DeleteCalls.Add(key);
            return Storage.TryRemove(key, out _);
        }

        public string Describe() => Description;
    }

    public sealed record RedisCall(string Key, string Value, TimeSpan Ttl);
}
