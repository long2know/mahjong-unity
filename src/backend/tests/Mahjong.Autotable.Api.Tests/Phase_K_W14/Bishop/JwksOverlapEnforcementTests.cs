using System.Security.Cryptography;
using System.Text;
using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Bishop;

/// <summary>
/// Phase K Wave 14 — Bishop. Hard-asserted contract for the
/// overlap-window rollback enforcement in
/// <see cref="JwtValidationService"/>.
///
/// <list type="number">
///   <item><see cref="JwtValidationService.ErrorRollbackRejected"/>
///         is the canonical <c>"rollback-rejected"</c> reason
///         string.</item>
///   <item>Single-arg ctor (no rotation policy) bypasses the
///         check entirely.</item>
///   <item>Two-arg ctor accepts a null policy + behaves like the
///         single-arg form.</item>
///   <item>Active key (index 0) always validates regardless of
///         rotation state.</item>
///   <item>Inside the overlap window, previous-active key with
///         <c>iat &lt; RotationStartUtc</c> validates.</item>
///   <item>Inside the overlap window, previous-active key with
///         <c>iat &gt;= RotationStartUtc</c> rejects with
///         <see cref="JwtValidationService.ErrorRollbackRejected"/>.</item>
///   <item>Outside the overlap window (policy unset / rotation
///         not started), previous-active key tokens with any
///         <c>iat</c> validate.</item>
/// </list>
/// </summary>
public sealed class JwksOverlapEnforcementTests
{
    private const string KeyA = "phase-k-w14-active-key-32-bytes!!!!"; // index 0
    private const string KeyB = "phase-k-w14-previous-key-32-bytes!!!"; // index 1
    private const string Subject = "rollback-test";

    private static JwtSigningKeyProvider NewProviderTwoKeys() =>
        new(
            new AuthOptions
            {
                JwtAlgorithm = "HS256",
                JwtSigningKeys = new[] { KeyA, KeyB },
            },
            NullLogger<JwtSigningKeyProvider>.Instance);

    private static JwtSigningKeyProvider NewProviderActiveOnly() =>
        new(
            new AuthOptions
            {
                JwtAlgorithm = "HS256",
                JwtSigningKeys = new[] { KeyA },
            },
            NullLogger<JwtSigningKeyProvider>.Instance);

    private static JwtStagedRotationPolicy NewPolicy(DateTime? startUtc, int overlapDays = 30) =>
        new(new AuthOptions
        {
            RotationStartUtc = startUtc,
            RotationOverlapDays = overlapDays,
        });

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string MintHs256Token(
        string keyMaterial,
        long iatUnix,
        long expUnix,
        string subject = Subject)
    {
        var key = new JwtSigningKey(0, keyMaterial); // index 0 here is just for Kid derivation
        var header = $"{{\"alg\":\"HS256\",\"typ\":\"JWT\",\"kid\":\"{key.Kid}\"}}";
        var payload = $"{{\"sub\":\"{subject}\",\"iat\":{iatUnix},\"exp\":{expUnix}}}";
        var headerB64 = B64Url(Encoding.UTF8.GetBytes(header));
        var payloadB64 = B64Url(Encoding.UTF8.GetBytes(payload));
        var signingInput = Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keyMaterial));
        var sig = hmac.ComputeHash(signingInput);
        return $"{headerB64}.{payloadB64}.{B64Url(sig)}";
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Error_RollbackRejected_Constant_IsStable()
    {
        Assert.Equal("rollback-rejected", JwtValidationService.ErrorRollbackRejected);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Validator_SingleArgCtor_BypassesCheck()
    {
        var provider = NewProviderTwoKeys();
        var validator = new JwtValidationService(provider);
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = iat + 3600;
        var token = MintHs256Token(KeyB, iat, exp);
        var result = validator.Validate(token);
        Assert.True(result.Ok);
        Assert.Equal(Subject, result.Subject);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void Validator_TwoArgCtor_NullPolicy_BypassesCheck()
    {
        var provider = NewProviderTwoKeys();
        var validator = new JwtValidationService(provider, rotation: null);
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = iat + 3600;
        var token = MintHs256Token(KeyB, iat, exp);
        var result = validator.Validate(token);
        Assert.True(result.Ok);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void ActiveKey_AlwaysValidates_NoPolicy()
    {
        var provider = NewProviderTwoKeys();
        var policy = NewPolicy(DateTime.UtcNow.AddHours(-1));
        var validator = new JwtValidationService(provider, policy);
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = iat + 3600;
        var token = MintHs256Token(KeyA, iat, exp); // active key
        var result = validator.Validate(token);
        Assert.True(result.Ok);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void ActiveKey_AlwaysValidates_InsideOverlap()
    {
        var provider = NewProviderTwoKeys();
        var policy = NewPolicy(DateTime.UtcNow.AddHours(-1));
        var validator = new JwtValidationService(provider, policy);
        // iat AT or AFTER rotation start, signed by active key
        var rotationStart = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var iat = rotationStart + 10;
        var exp = iat + 3600;
        var token = MintHs256Token(KeyA, iat, exp);
        var result = validator.Validate(token);
        Assert.True(result.Ok);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void PreviousKey_PreRotationIat_InsideOverlap_Validates()
    {
        var provider = NewProviderTwoKeys();
        var rotationStart = DateTime.UtcNow.AddHours(-1);
        var policy = NewPolicy(rotationStart);
        var validator = new JwtValidationService(provider, policy);
        var rotationStartUnix = new DateTimeOffset(rotationStart, TimeSpan.Zero).ToUnixTimeSeconds();
        // iat BEFORE rotation start → legitimate pre-rotation token,
        // signed by what was the active key at iat time but is now
        // index 1.
        var iat = rotationStartUnix - 60;
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = MintHs256Token(KeyB, iat, exp);
        var result = validator.Validate(token);
        Assert.True(result.Ok, $"Expected ok, got error={result.Error}");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void PreviousKey_PostRotationIat_InsideOverlap_Rejected()
    {
        var provider = NewProviderTwoKeys();
        var rotationStart = DateTime.UtcNow.AddHours(-1);
        var policy = NewPolicy(rotationStart);
        var validator = new JwtValidationService(provider, policy);
        var rotationStartUnix = new DateTimeOffset(rotationStart, TimeSpan.Zero).ToUnixTimeSeconds();
        // iat AT OR AFTER rotation start, signed by previous-active
        // key → rollback attack / buggy minter. Must reject.
        var iat = rotationStartUnix + 10;
        var exp = iat + 3600;
        var token = MintHs256Token(KeyB, iat, exp);
        var result = validator.Validate(token);
        Assert.False(result.Ok);
        Assert.Equal(JwtValidationService.ErrorRollbackRejected, result.Error);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void PreviousKey_PostRotationIat_PolicyUnset_Validates()
    {
        var provider = NewProviderTwoKeys();
        // Policy is wired but RotationStartUtc is unset → no rotation
        // in progress → check is a no-op.
        var policy = NewPolicy(startUtc: null);
        var validator = new JwtValidationService(provider, policy);
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = iat + 3600;
        var token = MintHs256Token(KeyB, iat, exp);
        var result = validator.Validate(token);
        Assert.True(result.Ok);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void PreviousKey_IatExactlyAtRotationStart_InsideOverlap_Rejected()
    {
        var provider = NewProviderTwoKeys();
        // Round the rotation start to whole seconds so the policy's
        // RotationStartUtc has no sub-second ticks (the JWT `iat`
        // is unix-seconds, so any ms residue would push iat strictly
        // less than RotationStartUtc and skip the boundary).
        var rotationStartUnix = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var rotationStart = DateTimeOffset.FromUnixTimeSeconds(rotationStartUnix).UtcDateTime;
        var policy = NewPolicy(rotationStart);
        var validator = new JwtValidationService(provider, policy);
        // Boundary: iat exactly at rotation start. The check uses
        // `>=` so we reject (defensive — a token minted in the same
        // second as the policy flip is treated as post-rotation).
        var iat = rotationStartUnix;
        var exp = iat + 3600;
        var token = MintHs256Token(KeyB, iat, exp);
        var result = validator.Validate(token);
        Assert.False(result.Ok);
        Assert.Equal(JwtValidationService.ErrorRollbackRejected, result.Error);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-14"), Trait("Lane", "Bishop")]
    public void ActiveKeyOnly_NoFallback_NoOverlap_Issue()
    {
        // Provider with only the active key + a policy in the
        // overlap window → active key still validates, no
        // rollback rejection (kid index 0).
        var provider = NewProviderActiveOnly();
        var rotationStart = DateTime.UtcNow.AddHours(-1);
        var policy = NewPolicy(rotationStart);
        var validator = new JwtValidationService(provider, policy);
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = iat + 3600;
        var token = MintHs256Token(KeyA, iat, exp);
        var result = validator.Validate(token);
        Assert.True(result.Ok);
    }
}
