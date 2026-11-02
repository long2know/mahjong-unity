using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 8 — Bishop. Cache layer for the RS256 JWKS
/// document. Wave 7 shipped the endpoint with per-request RSA-key
/// marshalling — a heavy CPU cost (base64-url encoding +
/// JsonSerializer allocations) repeated on every request. Under
/// load the endpoint became a hot path: federated verifiers re-pull
/// the document at every token refresh and many CDN edges request
/// it on cold cache.
///
/// <para>The cache holds the pre-serialised JSON body + the
/// strong-ETag header value (SHA-256 over the body) keyed by the
/// active RSA key set's combined kid list. A change to any key id
/// (rotation, key addition / removal) invalidates the cache so the
/// next request re-emits the fresh document.</para>
///
/// <para>TTL is 60 seconds — short enough that key rotations land in
/// the document within one CDN refresh cycle, long enough that a
/// 1000-RPS burst hits the cached body roughly 60,000 times per
/// miss. The <see cref="DefaultTtl"/> is a constant so the W8
/// contract test can pin the value; tests that need a different TTL
/// construct the cache directly with a custom value.</para>
///
/// <para>The W8 endpoint also honours <c>If-None-Match</c>: when the
/// inbound ETag matches the cached value the endpoint returns
/// <c>304 Not Modified</c> with no body, saving the network bytes
/// entirely.</para>
///
/// <para>Phase K Wave 10 — Bishop hygiene additions:</para>
/// <list type="bullet">
///   <item>A dedicated <see cref="MemoryCache"/> instance with a
///         hard <see cref="MemoryCacheOptions.SizeLimit"/> so the
///         service no longer borrows the shared application cache
///         (which could be evicted by unrelated callers).</item>
///   <item>Cache hit / miss / rebuild counters via
///         <see cref="IMeterFactory"/> — exposed as
///         <c>jwks_cache_hit_total</c>,
///         <c>jwks_cache_miss_total</c>,
///         <c>jwks_cache_rebuild_total</c>.</item>
///   <item>Stampede protection via a <see cref="SemaphoreSlim"/>
///         so a concurrent miss only rebuilds the payload once —
///         the second caller blocks on the gate and reads the
///         freshly cached entry instead of paying the
///         re-serialisation cost twice.</item>
/// </list>
/// </summary>
public sealed class JwksCacheService : IDisposable
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);
    private const string CacheKey = "Mahjong.Autotable.Api.Auth.JwksCacheService::doc";

    /// <summary>Phase K Wave 10 — Bishop. Meter name for the
    /// cache-hygiene counters. Surfaced as a constant so the
    /// contract tests + the Prometheus exporter can pin the
    /// vocabulary.</summary>
    public const string MeterName = "Mahjong.Autotable.Api.Auth.JwksCache";

    /// <summary>Hard cap on the cache size. The cache only ever
    /// holds the single JWKS payload — the limit is set
    /// conservatively at 16 so any unexpected key collision (e.g.
    /// a misuse that records under a per-tenant key) can't grow
    /// without bound.</summary>
    public const int SizeLimit = 16;

    private readonly IMemoryCache _cache;
    private bool _ownsCache;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _stampedeGate = new(1, 1);

    private readonly Counter<long>? _hitCounter;
    private readonly Counter<long>? _missCounter;
    private readonly Counter<long>? _rebuildCounter;

    public JwksCacheService(IMemoryCache cache, TimeSpan? ttl = null, IMeterFactory? meterFactory = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ttl = ttl ?? DefaultTtl;
        _ownsCache = false;
        if (meterFactory is not null)
        {
            var meter = meterFactory.Create(MeterName);
            _hitCounter = meter.CreateCounter<long>("jwks_cache_hit_total",
                unit: null, description: "JWKS cache hits (entry served from memory).");
            _missCounter = meter.CreateCounter<long>("jwks_cache_miss_total",
                unit: null, description: "JWKS cache misses (entry rebuilt on demand).");
            _rebuildCounter = meter.CreateCounter<long>("jwks_cache_rebuild_total",
                unit: null, description: "JWKS payloads serialised + ETagged from the key provider.");
        }
    }

    /// <summary>
    /// Phase K Wave 10 — Bishop. Convenience factory used by
    /// Program.cs so the production registration owns a dedicated
    /// <see cref="MemoryCache"/> with the
    /// <see cref="SizeLimit"/> applied. Tests can still pass an
    /// arbitrary <see cref="IMemoryCache"/> via the ctor.
    /// </summary>
    public static JwksCacheService CreateWithDedicatedCache(
        TimeSpan? ttl = null,
        IMeterFactory? meterFactory = null)
    {
        var dedicated = new MemoryCache(new MemoryCacheOptions { SizeLimit = SizeLimit });
        var svc = new JwksCacheService(dedicated, ttl, meterFactory)
        {
            _ownsCache = true,
        };
        return svc;
    }

    /// <summary>
    /// Resolves the cached JWKS payload for the supplied key
    /// provider. The cache key includes the active + archived kid
    /// list so any rotation invalidates the entry deterministically.
    /// </summary>
    public JwksPayload Resolve(JwtSigningKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var fingerprint = ComputeFingerprint(keys);

        if (TryReadFresh(fingerprint, out var cachedFast))
        {
            _hitCounter?.Add(1);
            return cachedFast!;
        }

        // Phase K Wave 10 — Bishop. Stampede protection: only one
        // concurrent caller rebuilds the payload. The second caller
        // blocks on the gate, then reads the cached value populated
        // by the winner.
        _stampedeGate.Wait();
        try
        {
            if (TryReadFresh(fingerprint, out var cachedAfterGate))
            {
                _hitCounter?.Add(1);
                return cachedAfterGate!;
            }

            _missCounter?.Add(1);
            var payload = Build(keys, fingerprint);
            _rebuildCounter?.Add(1);
            var entryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _ttl,
                Size = 1,
            };
            _cache.Set(CacheKey, payload, entryOptions);
            return payload;
        }
        finally
        {
            _stampedeGate.Release();
        }
    }

    private bool TryReadFresh(string fingerprint, out JwksPayload? payload)
    {
        if (_cache.TryGetValue(CacheKey, out JwksPayload? cached)
            && cached is not null
            && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            payload = cached;
            return true;
        }
        payload = null;
        return false;
    }

    /// <summary>
    /// Removes any cached entry. Used by tests + by operators who
    /// want to force the next request to re-marshal the keys (e.g.
    /// after a manual rotation).
    /// </summary>
    public void Invalidate() => _cache.Remove(CacheKey);

    public void Dispose()
    {
        _stampedeGate.Dispose();
        if (_ownsCache && _cache is IDisposable disposableCache)
        {
            disposableCache.Dispose();
        }
    }

    private static string ComputeFingerprint(JwtSigningKeyProvider keys)
    {
        if (keys.AllRsaKeys.Count == 0) return "empty";
        var sb = new StringBuilder();
        foreach (var k in keys.AllRsaKeys)
        {
            sb.Append(k.Kid).Append('|');
        }
        return sb.ToString();
    }

    private static JwksPayload Build(JwtSigningKeyProvider keys, string fingerprint)
    {
        var publishedKeys = keys.AllRsaKeys.Select(k => new
        {
            kty = "RSA",
            kid = k.Kid,
            use = "sig",
            alg = "RS256",
            n = k.ModulusBase64Url,
            e = k.ExponentBase64Url,
        }).ToArray();

        // Pre-serialise once — the controller writes the body bytes
        // directly to the response, skipping the per-request JSON
        // allocation that was the W7 bottleneck.
        var body = JsonSerializer.Serialize(new { keys = publishedKeys });
        var etag = ComputeStrongEtag(body);
        return new JwksPayload(body, etag, fingerprint, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// RFC 7232 strong ETag — SHA-256 over the body, hex-lowercase,
    /// wrapped in double quotes. Strong (no leading <c>W/</c>) is
    /// correct because the body is a deterministic byte sequence
    /// for the given fingerprint.
    /// </summary>
    public static string ComputeStrongEtag(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        var sb = new StringBuilder(bytes.Length * 2 + 2);
        sb.Append('"');
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// Phase K Wave 8 — Bishop. Cached JWKS payload. The body is the
/// canonical JSON string (already serialised) and ETag is the
/// matching strong-ETag header value. <see cref="Fingerprint"/> is
/// the kid-list signature; the cache layer uses it to detect
/// rotations against the live key provider.
/// </summary>
public sealed record JwksPayload(
    string Body,
    string ETag,
    string Fingerprint,
    DateTimeOffset BuiltAt);
