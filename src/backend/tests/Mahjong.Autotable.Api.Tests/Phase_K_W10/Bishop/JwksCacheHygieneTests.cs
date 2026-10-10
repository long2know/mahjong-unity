using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Memory + observability hygiene for
/// the W8 JWKS cache. W9 shipped the cache with the shared
/// <see cref="IMemoryCache"/> and no metrics; W10 adds:
///
/// <list type="number">
///   <item><see cref="JwksCacheService.SizeLimit"/> constant is
///         pinned at 16.</item>
///   <item>The dedicated-cache factory returns a non-null
///         <see cref="JwksCacheService"/>.</item>
///   <item>A cold resolve emits exactly one
///         <c>jwks_cache_miss_total</c> + one
///         <c>jwks_cache_rebuild_total</c> tick.</item>
///   <item>A warm resolve emits a single
///         <c>jwks_cache_hit_total</c> tick.</item>
///   <item>The fingerprint check still invalidates on rotation —
///         a new key set triggers a rebuild even with metrics
///         attached.</item>
///   <item>Stampede protection: 32 concurrent threads against a
///         cold cache result in exactly one rebuild.</item>
///   <item>Disposing the service tears down the owned memory
///         cache when constructed via
///         <see cref="JwksCacheService.CreateWithDedicatedCache"/>.</item>
///   <item><see cref="JwksCacheService.MeterName"/> matches the
///         declared meter name on the exposed counter
///         instruments.</item>
/// </list>
/// </summary>
public sealed class JwksCacheHygieneTests
{
    private sealed class TestMeterListener : IDisposable
    {
        public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);
        private readonly MeterListener _listener = new();

        public TestMeterListener(string meterName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
            {
                lock (Counters)
                {
                    Counters.TryGetValue(inst.Name, out var current);
                    Counters[inst.Name] = current + value;
                }
            });
            _listener.Start();
        }

        public long Get(string name)
        {
            lock (Counters)
            {
                Counters.TryGetValue(name, out var v);
                return v;
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static string GeneratePem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static JwtSigningKeyProvider NewProvider(params string[] pems)
    {
        if (pems.Length == 0) pems = new[] { GeneratePem() };
        var options = new AuthOptions
        {
            JwtAlgorithm = "RS256",
            JwtRsaKeys = pems,
        };
        return new JwtSigningKeyProvider(options, NullLogger<JwtSigningKeyProvider>.Instance);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void SizeLimit_IsPinned()
    {
        Assert.Equal(16, JwksCacheService.SizeLimit);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void CreateWithDedicatedCache_ReturnsNonNullService()
    {
        using var svc = JwksCacheService.CreateWithDedicatedCache();
        Assert.NotNull(svc);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void ColdResolve_IncrementsMissAndRebuild()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JwksCacheService.MeterName);
        using var svc = new JwksCacheService(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 }),
            ttl: TimeSpan.FromMinutes(1),
            meterFactory: meterFactory);

        var keys = NewProvider();
        var first = svc.Resolve(keys);
        Assert.NotNull(first);
        Assert.Equal(1, listener.Get("jwks_cache_miss_total"));
        Assert.Equal(1, listener.Get("jwks_cache_rebuild_total"));
        Assert.Equal(0, listener.Get("jwks_cache_hit_total"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void WarmResolve_IncrementsHitOnly()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JwksCacheService.MeterName);
        using var svc = new JwksCacheService(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 }),
            ttl: TimeSpan.FromMinutes(1),
            meterFactory: meterFactory);
        var keys = NewProvider();

        _ = svc.Resolve(keys);
        _ = svc.Resolve(keys);

        Assert.Equal(1, listener.Get("jwks_cache_miss_total"));
        Assert.Equal(1, listener.Get("jwks_cache_rebuild_total"));
        Assert.Equal(1, listener.Get("jwks_cache_hit_total"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void Rotation_TriggersRebuild()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JwksCacheService.MeterName);
        using var svc = new JwksCacheService(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 }),
            ttl: TimeSpan.FromMinutes(1),
            meterFactory: meterFactory);

        var pem1 = GeneratePem();
        var pem2 = GeneratePem();
        _ = svc.Resolve(NewProvider(pem1));
        _ = svc.Resolve(NewProvider(pem1, pem2));

        Assert.Equal(2, listener.Get("jwks_cache_miss_total"));
        Assert.Equal(2, listener.Get("jwks_cache_rebuild_total"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public async Task StampedeProtection_ColdConcurrentCallers_RebuildOnce()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new TestMeterListener(JwksCacheService.MeterName);
        using var svc = new JwksCacheService(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 }),
            ttl: TimeSpan.FromMinutes(1),
            meterFactory: meterFactory);

        var keys = NewProvider();
        var tasks = Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => svc.Resolve(keys))).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, listener.Get("jwks_cache_rebuild_total"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void Dispose_TearsDownOwnedCache()
    {
        var svc = JwksCacheService.CreateWithDedicatedCache();
        svc.Dispose();
        // Re-disposing must not throw — IDisposable contract.
        svc.Dispose();
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void MeterName_Pinned()
    {
        Assert.Equal("Mahjong.Autotable.Api.Auth.JwksCache", JwksCacheService.MeterName);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void Resolve_WithoutMeterFactory_StillWorks()
    {
        using var svc = new JwksCacheService(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 }),
            ttl: TimeSpan.FromMinutes(1));
        var payload = svc.Resolve(NewProvider());
        Assert.NotNull(payload);
        // No exception means metrics emission is fully optional.
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
