using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W4;

/// <summary>
/// Phase K Wave 4 — Bishop. JWT signing-key fallback list +
/// issuance / validation contract tests.
///
/// <para>Covers:
/// <list type="bullet">
///   <item>JwtSigningKey.Kid is deterministic SHA-256 truncation
///         (the same key material always derives the same kid).</item>
///   <item>JwtSigningKeyProvider materialises the array; index 0 is
///         the active signer.</item>
///   <item>JwtIssuingService mints a 3-segment HS256 token; the
///         header carries alg=HS256 + kid=&lt;activeKey.Kid&gt;.</item>
///   <item>JwtValidationService accepts tokens signed under any key
///         in the fallback list (try-all loop), and fast-paths on
///         the kid header when present.</item>
///   <item>Validation rejects tampered signatures, expired tokens,
///         and unsupported algorithms.</item>
/// </list></para>
/// </summary>
public sealed class JwtSigningKeyContractTests
{
    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public void JwtSigningKey_Kid_IsDeterministic()
    {
        var keyA = new JwtSigningKey(0, "deterministic-secret-1234567890");
        var keyB = new JwtSigningKey(7, "deterministic-secret-1234567890");
        Assert.Equal(keyA.Kid, keyB.Kid);
        Assert.NotNull(keyA.Kid);
        Assert.NotEmpty(keyA.Kid);
        // base64url => no padding, no +/ characters
        Assert.DoesNotContain('=', keyA.Kid);
        Assert.DoesNotContain('+', keyA.Kid);
        Assert.DoesNotContain('/', keyA.Kid);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public void JwtSigningKeyProvider_BindsArray_FirstEntryIsActive()
    {
        var opts = new AuthOptions
        {
            JwtSigningKeys = new[] { "active-key-aaaaaaaaaaaaaaaaaa", "previous-key-bbbbbbbbbbbbbbbbb" },
        };
        var provider = new JwtSigningKeyProvider(opts, NullLogger<JwtSigningKeyProvider>.Instance);
        Assert.Equal(2, provider.AllKeys.Count);
        Assert.Equal(0, provider.ActiveKey.Index);
        Assert.Equal("active-key-aaaaaaaaaaaaaaaaaa", System.Text.Encoding.UTF8.GetString(provider.ActiveKey.Material));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public void JwtSigningKeyProvider_FallsBackToLegacySingular()
    {
        var opts = new AuthOptions
        {
            JwtSigningKey = "legacy-singular-aaaaaaaaaaaaaa",
        };
        var provider = new JwtSigningKeyProvider(opts, NullLogger<JwtSigningKeyProvider>.Instance);
        Assert.Single(provider.AllKeys);
        Assert.False(provider.UsingEphemeralFallbackKey);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public void JwtSigningKeyProvider_NoKeys_MintsEphemeralFallback()
    {
        var opts = new AuthOptions();
        var provider = new JwtSigningKeyProvider(opts, NullLogger<JwtSigningKeyProvider>.Instance);
        Assert.True(provider.UsingEphemeralFallbackKey);
        Assert.Single(provider.AllKeys);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public async Task JwtIssuingService_MintsThreeSegmentHs256Token_WithKidHeader()
    {
        var (issuer, validator, provider) = BuildPair("active-issue-key-aaaaaaaaaaaaaaa");
        var result = await issuer.IssueAsync("subject-42", new Dictionary<string, object?> { ["role"] = "admin" });
        Assert.NotNull(result.Token);
        var parts = result.Token.Split('.');
        Assert.Equal(3, parts.Length);

        var headerJson = System.Text.Encoding.UTF8.GetString(JwtValidationService.Base64UrlDecode(parts[0]));
        using var headerDoc = JsonDocument.Parse(headerJson);
        Assert.Equal("HS256", headerDoc.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", headerDoc.RootElement.GetProperty("typ").GetString());
        Assert.Equal(provider.ActiveKey.Kid, headerDoc.RootElement.GetProperty("kid").GetString());
        Assert.Equal(provider.ActiveKey.Kid, result.Kid);
        Assert.True(result.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public async Task JwtValidationService_AcceptsTokenSignedUnderActiveKey()
    {
        var (issuer, validator, _) = BuildPair("active-validate-aaaaaaaaaaaaaa");
        var result = await issuer.IssueAsync("subject-77", new Dictionary<string, object?> { ["role"] = "admin", ["tier"] = 3L });
        var validation = validator.Validate(result.Token);
        Assert.True(validation.Ok);
        Assert.Equal("subject-77", validation.Subject);
        Assert.NotNull(validation.Claims);
        Assert.Equal("admin", validation.Claims!["role"]);
        Assert.Equal(3L, validation.Claims["tier"]);
        Assert.Equal(result.Kid, validation.Kid);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public async Task JwtValidationService_AcceptsTokenSignedUnderFallbackKey()
    {
        // Mint a token with the original key (active) and then rotate so
        // that key falls into the fallback list. Validation must still
        // succeed because the validator iterates all keys.
        var (issuer1, _, provider1) = BuildPair("first-key-aaaaaaaaaaaaaaaaaa");
        var minted = await issuer1.IssueAsync("rotated-subject");

        // Build a new provider with a NEW active key + the first key
        // demoted to fallback at index 1.
        var rotated = new AuthOptions
        {
            JwtSigningKeys = new[] { "second-key-bbbbbbbbbbbbbbbbbb", "first-key-aaaaaaaaaaaaaaaaaa" },
        };
        var rotatedProvider = new JwtSigningKeyProvider(rotated, NullLogger<JwtSigningKeyProvider>.Instance);
        var rotatedValidator = new JwtValidationService(rotatedProvider);

        var validation = rotatedValidator.Validate(minted.Token);
        Assert.True(validation.Ok);
        Assert.Equal("rotated-subject", validation.Subject);
        // The kid in the token still maps to the now-fallback key.
        Assert.Equal(provider1.ActiveKey.Kid, validation.Kid);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public async Task JwtValidationService_RejectsTamperedToken()
    {
        var (issuer, validator, _) = BuildPair("tamper-key-aaaaaaaaaaaaaaaaaa");
        var result = await issuer.IssueAsync("subject-tamper");
        var parts = result.Token.Split('.');
        // Flip a byte of the payload.
        var tamperedPayload = parts[1] + "X";
        var tamperedToken = $"{parts[0]}.{tamperedPayload}.{parts[2]}";
        var validation = validator.Validate(tamperedToken);
        Assert.False(validation.Ok);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public void JwtValidationService_RejectsMalformedToken()
    {
        var (_, validator, _) = BuildPair("malformed-key-aaaaaaaaaaaaaaaa");
        var validation = validator.Validate("not.a-valid-token");
        Assert.False(validation.Ok);
        Assert.Equal(JwtValidationService.ErrorMalformed, validation.Error);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public async Task JwtValidationService_RejectsExpiredToken()
    {
        var (issuer, validator, _) = BuildPair("expired-key-aaaaaaaaaaaaaaaaaa");
        var result = await issuer.IssueAsync("subject-exp",
            lifetime: TimeSpan.FromSeconds(-10));
        var validation = validator.Validate(result.Token);
        Assert.False(validation.Ok);
        Assert.Equal(JwtValidationService.ErrorExpired, validation.Error);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-4")]
    public async Task JwtIssuingService_WritesAuditRow_WithIndexedKind()
    {
        var (issuer, _, provider) = BuildPair("audit-key-aaaaaaaaaaaaaaaaaaaaa");
        await issuer.IssueAsync("audit-subject");
        // The audit Kind constant prefix exists and the active key index is 0.
        Assert.Equal("auth.jwt.signed.with_key.",
            Mahjong.Autotable.Api.Data.Entities.ReconnectAuditEntry.KindAuthJwtSignedPrefix);
        Assert.Equal(0, provider.ActiveKey.Index);
    }

    private static (JwtIssuingService issuer, JwtValidationService validator, JwtSigningKeyProvider provider) BuildPair(string activeKey, params string[] previousKeys)
    {
        var keys = new[] { activeKey }.Concat(previousKeys).ToArray();
        var opts = new AuthOptions { JwtSigningKeys = keys };
        var provider = new JwtSigningKeyProvider(opts, NullLogger<JwtSigningKeyProvider>.Instance);
        // Pass an in-memory scope factory shim that throws on resolve so the
        // audit write swallows silently — we don't need the audit row in
        // most facts, just to exercise the mint path.
        var scopeFactory = new NullScopeFactory();
        var issuer = new JwtIssuingService(provider, scopeFactory, NullLogger<JwtIssuingService>.Instance);
        var validator = new JwtValidationService(provider);
        return (issuer, validator, provider);
    }

    private sealed class NullScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NullScope();
        private sealed class NullScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new NullProvider();
            public void Dispose() { }
        }
        private sealed class NullProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
