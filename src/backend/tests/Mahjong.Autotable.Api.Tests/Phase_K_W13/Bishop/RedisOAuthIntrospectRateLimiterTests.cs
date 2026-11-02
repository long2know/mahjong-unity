using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using StackExchange.Redis.Maintenance;
using StackExchange.Redis.Profiling;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Bishop;

/// <summary>
/// Phase K Wave 13 — Bishop. Hard-asserted contract for the
/// Redis-backed <see cref="RedisOAuthIntrospectRateLimiter"/>.
/// The class wires a sliding-window sorted-set into Redis so
/// multi-replica deployments enforce the rate cap globally
/// instead of per-replica.
///
/// <list type="number">
///   <item>Type exists.</item>
///   <item>Type implements <see cref="IOAuthIntrospectRateLimiter"/>.</item>
///   <item>Key prefix is <c>mahjong:oauth-introspect:</c>
///         (operator-visible namespace; alerts grep for it).</item>
///   <item>RequestsPerWindow surfaces the capacity passed at
///         construction.</item>
///   <item>WindowSeconds surfaces the window passed at
///         construction.</item>
///   <item>Capacity coerces non-positive to default.</item>
///   <item>Window coerces non-positive to default.</item>
///   <item>OAuthIntrospectRateLimitOptions has the
///         <c>LimiterImpl</c> property (W13 toggle).</item>
///   <item>Default LimiterImpl = "InMemory" (backward compat).</item>
///   <item>Falls back to InMemory limiter when Redis throws
///         (the supplied multiplexer is broken).</item>
/// </list>
/// </summary>
public sealed class RedisOAuthIntrospectRateLimiterTests
{
    /// <summary>Minimal IConnectionMultiplexer stub whose
    /// GetDatabase throws on every operation — drives the Redis
    /// limiter's fallback path.</summary>
    private sealed class FaultyConnectionMultiplexer : IConnectionMultiplexer
    {
        public override string ToString() => "fake";
        public string ClientName => "fake";
        public string Configuration => string.Empty;
        public int TimeoutMilliseconds => 100;
        public long OperationCount => 0;
        public bool PreserveAsyncOrder { get => false; set { } }
        public bool IsConnected => false;
        public bool IsConnecting => false;
        public bool IncludeDetailInExceptions { get => false; set { } }
        public int StormLogThreshold { get => 0; set { } }

        public event EventHandler<RedisErrorEventArgs>? ErrorMessage { add { } remove { } }
        public event EventHandler<ConnectionFailedEventArgs>? ConnectionFailed { add { } remove { } }
        public event EventHandler<InternalErrorEventArgs>? InternalError { add { } remove { } }
        public event EventHandler<ConnectionFailedEventArgs>? ConnectionRestored { add { } remove { } }
        public event EventHandler<EndPointEventArgs>? ConfigurationChanged { add { } remove { } }
        public event EventHandler<EndPointEventArgs>? ConfigurationChangedBroadcast { add { } remove { } }
        public event EventHandler<HashSlotMovedEventArgs>? HashSlotMoved { add { } remove { } }
        public event EventHandler<ServerMaintenanceEvent>? ServerMaintenanceEvent { add { } remove { } }

        public void Close(bool allowCommandsToComplete = true) { }
        public Task CloseAsync(bool allowCommandsToComplete = true) => Task.CompletedTask;
        public bool Configure(System.IO.TextWriter? log = null) => false;
        public Task<bool> ConfigureAsync(System.IO.TextWriter? log = null) => Task.FromResult(false);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void ExportConfiguration(System.IO.Stream destination, ExportOptions options = (ExportOptions)(-1)) { }
        public ServerCounters GetCounters() => throw new InvalidOperationException("test-stub");
        public IDatabase GetDatabase(int db = -1, object? asyncState = null) =>
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test-faulty");
        public System.Net.EndPoint[] GetEndPoints(bool configuredOnly = false) => Array.Empty<System.Net.EndPoint>();
        public int GetHashSlot(RedisKey key) => 0;
        public IServer GetServer(string host, int port, object? asyncState = null) => throw new InvalidOperationException("test-stub");
        public IServer GetServer(string hostAndPort, object? asyncState = null) => throw new InvalidOperationException("test-stub");
        public IServer GetServer(System.Net.IPAddress host, int port) => throw new InvalidOperationException("test-stub");
        public IServer GetServer(System.Net.EndPoint endpoint, object? asyncState = null) => throw new InvalidOperationException("test-stub");
        public IServer[] GetServers() => Array.Empty<IServer>();
        public string GetStatus() => "disconnected";
        public void GetStatus(System.IO.TextWriter log) { }
        public string GetStormLog() => string.Empty;
        public ISubscriber GetSubscriber(object? asyncState = null) => throw new InvalidOperationException("test-stub");
        public int HashSlot(RedisKey key) => 0;
        public long PublishReconfigure(CommandFlags flags = CommandFlags.None) => 0;
        public Task<long> PublishReconfigureAsync(CommandFlags flags = CommandFlags.None) => Task.FromResult(0L);
        public void RegisterProfiler(Func<ProfilingSession?> profilingSessionProvider) { }
        public void ResetStormLog() { }
        public void Wait(Task task) { }
        public T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
        public void WaitAll(params Task[] tasks) { }
        public void AddLibraryNameSuffix(string suffix) { }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Limiter_TypeExists()
    {
        Assert.NotNull(typeof(RedisOAuthIntrospectRateLimiter));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Limiter_ImplementsInterface()
    {
        Assert.True(typeof(IOAuthIntrospectRateLimiter)
            .IsAssignableFrom(typeof(RedisOAuthIntrospectRateLimiter)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Limiter_KeyPrefixIsStable()
    {
        Assert.Equal("mahjong:oauth-introspect:", RedisOAuthIntrospectRateLimiter.KeyPrefix);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Limiter_SurfacesCapacityAndWindow()
    {
        var limiter = new RedisOAuthIntrospectRateLimiter(
            new FaultyConnectionMultiplexer(), capacity: 17, windowSeconds: 90,
            NullLogger<RedisOAuthIntrospectRateLimiter>.Instance);
        Assert.Equal(17, limiter.RequestsPerWindow);
        Assert.Equal(90, limiter.WindowSeconds);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Limiter_CoercesNonPositiveCapacity()
    {
        var limiter = new RedisOAuthIntrospectRateLimiter(
            new FaultyConnectionMultiplexer(), capacity: 0, windowSeconds: 60,
            NullLogger<RedisOAuthIntrospectRateLimiter>.Instance);
        Assert.Equal(OAuthIntrospectRateLimitOptions.DefaultRateLimitPerClient,
            limiter.RequestsPerWindow);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Limiter_CoercesNonPositiveWindow()
    {
        var limiter = new RedisOAuthIntrospectRateLimiter(
            new FaultyConnectionMultiplexer(), capacity: 10, windowSeconds: 0,
            NullLogger<RedisOAuthIntrospectRateLimiter>.Instance);
        Assert.Equal(OAuthIntrospectRateLimitOptions.DefaultWindowSeconds,
            limiter.WindowSeconds);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Options_HasLimiterImplProperty()
    {
        var opts = new OAuthIntrospectRateLimitOptions();
        Assert.NotNull(opts.LimiterImpl);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Options_DefaultLimiterImplIsInMemory()
    {
        var opts = new OAuthIntrospectRateLimitOptions();
        Assert.Equal("InMemory", opts.LimiterImpl);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Limiter_FallsBackToInMemory_WhenRedisThrows()
    {
        var limiter = new RedisOAuthIntrospectRateLimiter(
            new FaultyConnectionMultiplexer(), capacity: 3, windowSeconds: 60,
            NullLogger<RedisOAuthIntrospectRateLimiter>.Instance);
        var now = DateTimeOffset.UtcNow;
        // Faulty Redis client → GetDatabase throws → falls back to
        // in-memory limiter which should allow the first 3 then deny.
        var d1 = limiter.TryAcquire("client-A", now);
        var d2 = limiter.TryAcquire("client-A", now);
        var d3 = limiter.TryAcquire("client-A", now);
        var d4 = limiter.TryAcquire("client-A", now);
        Assert.True(d1.Allowed);
        Assert.True(d2.Allowed);
        Assert.True(d3.Allowed);
        Assert.False(d4.Allowed);
    }
}
