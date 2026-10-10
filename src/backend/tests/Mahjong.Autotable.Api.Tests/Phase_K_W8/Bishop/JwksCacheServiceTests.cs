using System.Security.Cryptography;
using System.Text;
using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Unit-level facts for the
/// <see cref="JwksCacheService"/>.
///
/// <para>The cache stores the pre-serialized JWKS body + a strong
/// ETag so the <c>GET /.well-known/jwks.json</c> endpoint can
/// short-circuit on <c>If-None-Match</c>. Cache TTL is 60s (constant
/// <see cref="JwksCacheService.DefaultTtl"/>). Cache invalidates
/// when the active kid list rotates.</para>
/// </summary>
public sealed class JwksCacheServiceTests
{
    private static JwksCacheService NewService(TimeSpan? ttl = null)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        return new JwksCacheService(memory, ttl);
    }

    private static JwtSigningKeyProvider NewProvider(params string[] keys)
    {
        var options = new AuthOptions
        {
            JwtAlgorithm = "HS256",
            JwtSigningKeys = keys.Length == 0
                ? new[] { "phase-k-w8-jwks-cache-test-key-32-bytes!" }
                : keys,
        };
        return new JwtSigningKeyProvider(options, NullLogger<JwtSigningKeyProvider>.Instance);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void DefaultTtl_Is60Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), JwksCacheService.DefaultTtl);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void Resolve_Returns_NonEmptyBody_And_StrongEtag()
    {
        var svc = NewService();
        var keys = NewProvider();
        var payload = svc.Resolve(keys);
        Assert.False(string.IsNullOrEmpty(payload.Body));
        Assert.False(string.IsNullOrEmpty(payload.ETag));
        Assert.StartsWith("\"", payload.ETag);
        Assert.EndsWith("\"", payload.ETag);
        Assert.DoesNotContain("W/", payload.ETag);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void Resolve_SameProvider_ReturnsSameInstance_OnHit()
    {
        var svc = NewService();
        var keys = NewProvider();
        var a = svc.Resolve(keys);
        var b = svc.Resolve(keys);
        Assert.Same(a, b);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void Resolve_AfterInvalidate_RebuildsPayload()
    {
        var svc = NewService();
        var keys = NewProvider();
        var a = svc.Resolve(keys);
        svc.Invalidate();
        var b = svc.Resolve(keys);
        Assert.NotSame(a, b);
        Assert.Equal(a.Body, b.Body);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void Resolve_Payload_HasNonEmptyFingerprint()
    {
        var svc = NewService();
        var keys = NewProvider();
        var payload = svc.Resolve(keys);
        Assert.False(string.IsNullOrEmpty(payload.Fingerprint));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void Resolve_Payload_BuiltAtIsRecent()
    {
        var svc = NewService();
        var keys = NewProvider();
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        var payload = svc.Resolve(keys);
        var after = DateTimeOffset.UtcNow.AddSeconds(2);
        Assert.InRange(payload.BuiltAt, before, after);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void Resolve_BodyParses_AsJsonObject_WithKeysArray()
    {
        var svc = NewService();
        var keys = NewProvider();
        var payload = svc.Resolve(keys);
        using var doc = System.Text.Json.JsonDocument.Parse(payload.Body);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("keys", out var arr));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, arr.ValueKind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void ComputeStrongEtag_IsDeterministic_ForSameBody()
    {
        var body = "{\"keys\":[]}";
        var a = JwksCacheService.ComputeStrongEtag(body);
        var b = JwksCacheService.ComputeStrongEtag(body);
        Assert.Equal(a, b);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void ComputeStrongEtag_DiffersForDifferentBodies()
    {
        var a = JwksCacheService.ComputeStrongEtag("{\"keys\":[]}");
        var b = JwksCacheService.ComputeStrongEtag("{\"keys\":[{}]}");
        Assert.NotEqual(a, b);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void ComputeStrongEtag_IsHexSha256_OfBody()
    {
        var body = "phase-k-w8";
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var etag = JwksCacheService.ComputeStrongEtag(body);
        Assert.Contains(expectedHash, etag);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-8")]
    public void ComputeStrongEtag_ProducesQuotedHex()
    {
        var etag = JwksCacheService.ComputeStrongEtag("body");
        Assert.True(etag.Length > 2);
        Assert.Matches("^\"[a-f0-9]{64}\"$", etag);
    }
}
