using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Integration tests asserting that
/// <see cref="JwtIssuingService"/> and
/// <see cref="JwtValidationService"/> both observe the W19
/// <see cref="JwtDurationMetrics"/> collector on the wire-stable
/// happy path.
/// </summary>
public sealed class JwtServiceDurationIntegrationTests
{
    private const string TestKey = "w19-test-key-0123456789abcdefghij";

    private static (JwtIssuingService issuer,
        JwtValidationService validator,
        JwtDurationMetrics metrics) Build()
    {
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var metrics = new JwtDurationMetrics();
        var scopeFactory = new NullScope();
        var issuer = new JwtIssuingService(
            keys, scopeFactory, NullLogger<JwtIssuingService>.Instance,
            perTenantValidator: null, blockedMetrics: null, durationMetrics: metrics);
        var validator = new JwtValidationService(keys, rotation: null, durationMetrics: metrics);
        return (issuer, validator, metrics);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task IssueAsync_StampsIssueHistogram()
    {
        var (issuer, _, metrics) = Build();
        await issuer.IssueAsync("subject-1");
        Assert.Equal(1, metrics.TotalIssueObservations);
        // No tenant claim => the _global bucket is used.
        Assert.True(metrics.SnapshotIssue().ContainsKey(JwtDurationMetrics.GlobalTenantLabel));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task IssueAsync_WithTenantClaim_StampsPerTenantBucket()
    {
        var (issuer, _, metrics) = Build();
        var claims = new Dictionary<string, object?> { ["tenant"] = "tenant-x" };
        await issuer.IssueAsync("subject-1", claims);
        Assert.True(metrics.SnapshotIssue().ContainsKey("tenant-x"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Validate_StampsValidatorHistogram()
    {
        var (issuer, validator, metrics) = Build();
        var token = (await issuer.IssueAsync("subject-1")).Token;
        validator.Validate(token);
        Assert.Equal(1, metrics.TotalValidatorCheckObservations);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Validate_WithTenantClaim_StampsPerTenantBucket()
    {
        var (issuer, validator, metrics) = Build();
        var claims = new Dictionary<string, object?> { ["tenant"] = "tenant-x" };
        var token = (await issuer.IssueAsync("subject-1", claims)).Token;
        validator.Validate(token);
        Assert.True(metrics.SnapshotValidatorCheck().ContainsKey("tenant-x"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Validate_NullToken_StillStampsValidatorHistogram()
    {
        var (_, validator, metrics) = Build();
        validator.Validate(null);
        Assert.Equal(1, metrics.TotalValidatorCheckObservations);
        Assert.True(metrics.SnapshotValidatorCheck().ContainsKey(JwtDurationMetrics.UnknownTenantLabel));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Validate_MalformedToken_StillStampsValidatorHistogram()
    {
        var (_, validator, metrics) = Build();
        validator.Validate("not.a.jwt");
        Assert.Equal(1, metrics.TotalValidatorCheckObservations);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Validate_NoTenantClaim_FoldsIntoUnknownBucket()
    {
        // Hand-crafted minimal valid-ish token shape — three
        // base64url segments without a tenant claim. The
        // validator will fail the signature, but the histogram
        // must still observe.
        var (_, validator, metrics) = Build();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["alg"] = "HS256", ["typ"] = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["sub"] = "x" }));
        var sig = Base64Url(new byte[32]);
        var token = $"{header}.{payload}.{sig}";
        validator.Validate(token);
        Assert.True(metrics.SnapshotValidatorCheck().ContainsKey(JwtDurationMetrics.UnknownTenantLabel));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task IssueAsync_RepeatedCalls_AccumulateCount()
    {
        var (issuer, _, metrics) = Build();
        for (var i = 0; i < 5; i++)
        {
            await issuer.IssueAsync($"sub-{i}");
        }
        Assert.Equal(5, metrics.TotalIssueObservations);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Validate_RepeatedCalls_AccumulateCount()
    {
        var (issuer, validator, metrics) = Build();
        var token = (await issuer.IssueAsync("subject-1")).Token;
        for (var i = 0; i < 3; i++)
        {
            validator.Validate(token);
        }
        Assert.Equal(3, metrics.TotalValidatorCheckObservations);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void ValidatorWithoutMetrics_IsBackwardsCompatible()
    {
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys);
        // Should not throw — the no-metrics overload is the W14
        // single-arg constructor.
        validator.Validate("not.a.jwt");
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // Minimal IServiceScopeFactory stub for tests that don't
    // need a DB. JwtIssuingService writes audit rows through
    // this factory; the no-op scope means audit writes are
    // skipped silently.
    private sealed class NullScope : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new Scope();

        private sealed class Scope : Microsoft.Extensions.DependencyInjection.IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new EmptySp();
            public void Dispose() { }
        }

        private sealed class EmptySp : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
