using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. End-to-end RS256 key-rotation drill.
///
/// <para>The W6 hand-off shipped the RS256 toggle + JWKS surface;
/// Wave 7 hardens it with the full rotation flow that an operator
/// will exercise in production:</para>
/// <list type="number">
///   <item>Issue token under key A (single-key host).</item>
///   <item>Rotate keys: A → archive (validation-only), B → active
///         (issuance + validation).</item>
///   <item>Validate the original key-A token against the rotated
///         host — MUST succeed (key A is still in the fallback
///         list).</item>
///   <item>Issue a fresh token under the rotated host — MUST be
///         signed by key B (different kid).</item>
///   <item>JWKS surface MUST publish BOTH keys' public halves so a
///         downstream verifier resolves either kid.</item>
/// </list>
///
/// <para>The whole drill runs in-process via
/// <see cref="JwtSigningKeyProvider"/> + <see cref="JwtIssuingService"/> +
/// <see cref="JwtValidationService"/> rather than the controller
/// surface so the test is fast and free of the in-memory SQLite +
/// HTTP factory tax. The HTTP layer is exercised by the parallel
/// <c>RS256HappyPathTests</c> file (Vasquez's pre-stage).</para>
/// </summary>
public sealed class JwtRotationE2ETests
{
    private static string GeneratePem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static JwtSigningKeyProvider BuildProvider(params string[] pems)
        => new(
            new AuthOptions
            {
                JwtAlgorithm = "RS256",
                JwtRsaKeys = pems,
            },
            NullLogger<JwtSigningKeyProvider>.Instance);

    private static JwtIssuingService BuildIssuer(JwtSigningKeyProvider provider)
    {
        // The issuer writes an audit row to AppDbContext via a
        // service-scope. The rotation drill operates at the
        // cryptographic layer; we satisfy the dependency with a
        // synthetic provider that no-ops the scope.
        var services = new ServiceCollection();
        services.AddSingleton<NullDbContextFactoryScope>();
        var rootSp = services.BuildServiceProvider();
        return new JwtIssuingService(
            provider,
            rootSp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JwtIssuingService>.Instance);
    }

    // No-op helper to satisfy the IServiceScopeFactory dependency
    // without standing up AppDbContext. The audit write swallows
    // exceptions so an empty scope is fine.
    private sealed class NullDbContextFactoryScope { }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task FullRotation_KeyA_To_KeyB_LegacyTokensStillValidate()
    {
        var pemA = GeneratePem();
        var pemB = GeneratePem();

        // ── Phase 1: host with key A as the sole active key.
        var providerA = BuildProvider(pemA);
        var issuerA = BuildIssuer(providerA);
        var resultA = await issuerA.IssueAsync("rotation-test-user");
        Assert.NotNull(resultA.Token);
        var kidA = providerA.ActiveRsaKey.Kid;
        Assert.Equal(kidA, resultA.Kid);

        // ── Phase 2: rotate. Key B becomes active; key A demotes to
        //    the archive slot (validation-only).
        var providerRotated = BuildProvider(pemB, pemA);
        var validatorRotated = new JwtValidationService(providerRotated);
        var kidB = providerRotated.ActiveRsaKey.Kid;
        Assert.NotEqual(kidA, kidB); // sanity — A and B must be distinct

        // ── Phase 3: legacy token MUST still validate under the
        //    rotated host (A is still in the fallback list).
        var legacyValidate = validatorRotated.Validate(resultA.Token);
        Assert.True(legacyValidate.Ok,
            $"Pre-rotation token MUST validate against the rotated host. Error={legacyValidate.Error}");
        Assert.Equal(kidA, legacyValidate.Kid);

        // ── Phase 4: fresh token under the rotated host MUST be
        //    signed by key B (not A).
        var issuerRotated = BuildIssuer(providerRotated);
        var resultB = await issuerRotated.IssueAsync("rotation-test-user");
        Assert.Equal(kidB, resultB.Kid);
        var freshValidate = validatorRotated.Validate(resultB.Token);
        Assert.True(freshValidate.Ok, $"Post-rotation token MUST validate. Error={freshValidate.Error}");
        Assert.Equal(kidB, freshValidate.Kid);

        // ── Phase 5: the post-rotation host MUST surface BOTH keys
        //    on its JWKS so downstream verifiers can resolve either
        //    kid until the operator drops key A in the next cycle.
        Assert.Equal(2, providerRotated.AllRsaKeys.Count);
        Assert.NotNull(providerRotated.TryGetRsaByKid(kidA));
        Assert.NotNull(providerRotated.TryGetRsaByKid(kidB));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task FullRotation_AlgorithmConfusionAttack_Rejected()
    {
        // A token forged with alg=HS256 BUT carrying the public-key
        // material as the "secret" would slip past a naive validator
        // (CVE-2015-9235 family). The W6 algorithm guard refuses to
        // cross HS/RS families — pinning that here as a W7 hard
        // assertion.
        var pemA = GeneratePem();
        var providerRs = BuildProvider(pemA);
        var validatorRs = new JwtValidationService(providerRs);

        var issuerRs = BuildIssuer(providerRs);
        var rs256 = await issuerRs.IssueAsync("alg-confusion-test");
        var parts = rs256.Token.Split('.');
        Assert.Equal(3, parts.Length);

        // Forge: replace the header with alg=HS256 (keep kid) and
        // sign with HMAC against the public-key bytes (the classical
        // confusion attack). Header bytes:
        var forgedHeader = new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT",
            ["kid"] = providerRs.ActiveRsaKey.Kid,
        };
        var headerJson = JsonSerializer.SerializeToUtf8Bytes(forgedHeader);
        var headerSeg = Convert.ToBase64String(headerJson)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        // Reuse the payload segment verbatim (legitimate iat/exp).
        var signingInput = $"{headerSeg}.{parts[1]}";
        var hmacKey = providerRs.ActiveRsaKey.Rsa.ExportSubjectPublicKeyInfo();
        Span<byte> sig = stackalloc byte[32];
        HMACSHA256.HashData(hmacKey, Encoding.ASCII.GetBytes(signingInput), sig);
        var sigSeg = Convert.ToBase64String(sig.ToArray())
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var forged = $"{signingInput}.{sigSeg}";

        var verdict = validatorRs.Validate(forged);
        Assert.False(verdict.Ok,
            "RS256 host MUST reject a token claiming alg=HS256 (algorithm-confusion guard).");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public void Jwks_NAndE_Are_Base64UrlNoPadding_Per_Rfc7517()
    {
        // RFC 7517 §6.3.1 pins the n / e encoding as base64url
        // without padding. Wave-6 ships the encoder; Wave 7 hard-
        // asserts the wire shape so a downstream verifier
        // (Auth0 / Cognito / jose-jwt / pyJWT) parses the document
        // without manual padding fixups.
        var pem = GeneratePem();
        var provider = BuildProvider(pem);
        var k = provider.ActiveRsaKey;

        Assert.DoesNotContain('=', k.ModulusBase64Url);
        Assert.DoesNotContain('=', k.ExponentBase64Url);
        Assert.DoesNotContain('+', k.ModulusBase64Url);
        Assert.DoesNotContain('/', k.ModulusBase64Url);
        Assert.DoesNotContain('+', k.ExponentBase64Url);
        Assert.DoesNotContain('/', k.ExponentBase64Url);

        // The decoded modulus MUST match the actual key params bytes
        // — guards against an encoder regression.
        var n = Base64UrlDecode(k.ModulusBase64Url);
        var e = Base64UrlDecode(k.ExponentBase64Url);
        var parameters = k.Rsa.ExportParameters(includePrivateParameters: false);
        Assert.Equal(parameters.Modulus, n);
        Assert.Equal(parameters.Exponent, e);
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
