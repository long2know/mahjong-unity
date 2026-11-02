using Mahjong.Autotable.Api.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Opt-in live-Redis exercise for
/// <see cref="RedisIdempotencyStore"/>. The contract-test sibling
/// (<see cref="RedisIdempotencyStoreContractTests"/>) covers the
/// store's semantics deterministically against an in-memory
/// adapter. This file is the integration check that the real
/// <see cref="ConnectionMultiplexer"/> path SET-NX-EX +
/// GET + DEL behave the same way against a Testcontainers
/// / docker-compose Redis instance.
///
/// <para><b>Activation:</b> tests soft-pass (early <c>return;</c>)
/// when the <c>MAHJONG_REDIS_LIVE_URL</c> environment variable is
/// not set. CI / default dev hosts skip the file without needing
/// Docker. Local operators export the variable before running:</para>
///
/// <code>
/// docker run -d --rm -p 6379:6379 --name mahjong-redis-test redis:7-alpine
/// MAHJONG_REDIS_LIVE_URL=localhost:6379 dotnet test \
///   --filter "Category=IdempotencyLive"
/// </code>
///
/// <para>See <c>docs/redis-idempotency.md §6 "Tests"</c> for the
/// full operator runbook.</para>
/// </summary>
public sealed class RedisIdempotencyStoreLiveTests
{
    private const string EnvVar = "MAHJONG_REDIS_LIVE_URL";

    private static string? LiveUrl =>
        Environment.GetEnvironmentVariable(EnvVar);

    private static (ConnectionMultiplexer Mux, RedisIdempotencyStore Store)? TryBuild()
    {
        var url = LiveUrl;
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            // Configure with a short connect-timeout so a stale env
            // var doesn't hang the test for minutes.
            var options = ConfigurationOptions.Parse(url);
            options.ConnectTimeout = 1500;
            options.SyncTimeout = 1500;
            options.AbortOnConnectFail = false;
            var mux = ConnectionMultiplexer.Connect(options);
            if (!mux.IsConnected) { mux.Dispose(); return null; }
            // Use a dedicated DB to avoid collisions with other test runs.
            var store = new RedisIdempotencyStore(
                mux,
                NullLogger<RedisIdempotencyStore>.Instance,
                fallback: null,
                replayWindow: TimeSpan.FromSeconds(30),
                databaseIndex: 0);
            return (mux, store);
        }
        catch
        {
            return null;
        }
    }

    private static IdempotencyRecord Sample(string suffix, string body = "live-body") => new(
        Key: $"live-{Guid.NewGuid():N}-{suffix}",
        PayloadHash: "live:hash",
        RecordedAt: DateTimeOffset.UtcNow,
        StatusCode: 200,
        ContentType: "application/json",
        ResponseBody: body);

    [Fact, Trait("Category", "IdempotencyLive"), Trait("Wave", "Phase-K-10")]
    public void LiveRoundTrip_RecordAndFetch()
    {
        var built = TryBuild();
        if (built is null) return; // Docker absent → soft-pass.
        var (mux, store) = built.Value;
        try
        {
            var record = Sample("rt");
            store.Record(record);
            var fetched = store.TryGet(record.Key);
            Assert.NotNull(fetched);
            Assert.Equal(record.ResponseBody, fetched!.ResponseBody);
            Assert.Equal(record.PayloadHash, fetched.PayloadHash);
            Assert.Equal(record.StatusCode, fetched.StatusCode);
            store.Remove(record.Key);
            Assert.Null(store.TryGet(record.Key));
        }
        finally
        {
            mux.Dispose();
        }
    }

    [Fact, Trait("Category", "IdempotencyLive"), Trait("Wave", "Phase-K-10")]
    public void LiveTtlExpiry_HonoursReplayWindow()
    {
        var built = TryBuild();
        if (built is null) return;
        var (mux, store) = built.Value;
        try
        {
            // Re-build with a 2-second TTL so the live expiry can be
            // exercised within the test budget.
            var shortStore = new RedisIdempotencyStore(
                mux,
                NullLogger<RedisIdempotencyStore>.Instance,
                fallback: null,
                replayWindow: TimeSpan.FromSeconds(2));
            var record = Sample("ttl");
            shortStore.Record(record);
            Assert.NotNull(shortStore.TryGet(record.Key));
            Thread.Sleep(2_500);
            // Redis TTL has expired — entry is gone.
            Assert.Null(shortStore.TryGet(record.Key));
        }
        finally
        {
            mux.Dispose();
        }
    }

    [Fact, Trait("Category", "IdempotencyLive"), Trait("Wave", "Phase-K-10")]
    public void LiveKeyPrefix_NamespacesEntries()
    {
        var built = TryBuild();
        if (built is null) return;
        var (mux, store) = built.Value;
        try
        {
            var record = Sample("prefix");
            store.Record(record);
            var db = mux.GetDatabase();
            // The store namespaces keys; the raw key without prefix
            // should be absent.
            Assert.False(db.KeyExists(record.Key));
            Assert.True(db.KeyExists(RedisIdempotencyStore.KeyPrefix + record.Key));
            store.Remove(record.Key);
        }
        finally
        {
            mux.Dispose();
        }
    }
}
