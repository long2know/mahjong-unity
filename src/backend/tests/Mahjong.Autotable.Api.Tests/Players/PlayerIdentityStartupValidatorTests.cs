using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Players;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Burke — production fail-closed guard for the identity-signing key.
///
/// <para>The identity cookie is signed with a key derived from the active JWT signing key.
/// <see cref="JwtSigningKeyProvider"/>'s own <c>requireOperatorKeys</c> guard only covers HS256,
/// so an RS256 Production host with no HMAC key silently falls back to a per-process random
/// key. That is not an impersonation hole (the key is still secret) but it IS an availability +
/// continuity hole: every restart invalidates every identity, and no node in a multi-instance
/// deployment can verify another's cookies. Production must refuse to boot instead.</para>
/// </summary>
public sealed class PlayerIdentityStartupValidatorTests
{
    private const string OperatorKey = "T3BlcmF0b3JLZXlPcGVyYXRvcktleU9wZXJhdG9yS2V5T3BlcmF0b3JLZXk=";

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-failclosed")]
    public void Production_WithEphemeralSigningKey_RefusesToBoot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlayerIdentityStartupValidator.Validate(isProduction: true, usingEphemeralSigningKey: true));

        Assert.Equal(PlayerIdentityStartupValidator.ProductionRequiresStableSigningKeyMessage, ex.Message);
        Assert.Contains("Authentication__JwtSigningKeys__0", ex.Message, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-failclosed")]
    public void Production_WithOperatorKey_Boots()
    {
        PlayerIdentityStartupValidator.Validate(isProduction: true, usingEphemeralSigningKey: false);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-failclosed")]
    public void Development_WithEphemeralSigningKey_BootsButIsExplicit()
    {
        // Dev/test keep the zero-config shape; the restart-resets-identity trade-off is logged.
        PlayerIdentityStartupValidator.Validate(isProduction: false, usingEphemeralSigningKey: true);
        Assert.Contains("reset on every restart",
            PlayerIdentityStartupValidator.EphemeralKeyDevelopmentMessage, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-failclosed")]
    public void Rs256ProductionWithoutHmacKeys_IsExactlyTheGapThisGuardCloses()
    {
        // Reproduce the provider state a Production RS256 host lands in: no HMAC keys configured,
        // requireOperatorKeys does NOT throw for RS256, so an ephemeral HMAC key is minted.
        var provider = new JwtSigningKeyProvider(
            new AuthOptions
            {
                JwtAlgorithm = "RS256",
                JwtSigningKeys = Array.Empty<string>(),
                JwtRsaKeys = Array.Empty<string>(),
            },
            NullLogger<JwtSigningKeyProvider>.Instance,
            requireOperatorKeys: false);

        Assert.True(provider.UsingEphemeralFallbackKey);
        Assert.Throws<InvalidOperationException>(() =>
            PlayerIdentityStartupValidator.Validate(
                isProduction: true, usingEphemeralSigningKey: provider.UsingEphemeralFallbackKey));
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-failclosed")]
    public void OperatorKeys_ProduceAStableCredentialAcrossProcessRestarts()
    {
        // Two independently constructed providers modelling two processes / two pods.
        var playerId = Guid.NewGuid().ToString("N");
        var processA = NewProtector(OperatorKey);
        var processB = NewProtector(OperatorKey);

        Assert.False(NewProvider(OperatorKey).UsingEphemeralFallbackKey);
        Assert.Equal(processA.Protect(playerId), processB.Protect(playerId));

        var verdict = processB.Unprotect(processA.Protect(playerId));
        Assert.True(verdict.IsValid);
        Assert.Equal(playerId, verdict.PlayerId);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-failclosed")]
    public void EphemeralKeys_DoNotCrossProcessBoundaries_SoDevIdentitiesResetNotLeak()
    {
        // Two "processes" without operator keys: each mints its own random key. The failure mode
        // is a fresh identity (fail closed), never acceptance of the other's cookie.
        var playerId = Guid.NewGuid().ToString("N");
        var processA = new PlayerIdentityTokenProtector(NewProvider(null));
        var processB = new PlayerIdentityTokenProtector(NewProvider(null));

        var verdict = processB.Unprotect(processA.Protect(playerId));

        Assert.Equal(PlayerIdentityTokenStatus.BadSignature, verdict.Status);
        Assert.Null(verdict.PlayerId);
    }

    private static PlayerIdentityTokenProtector NewProtector(string key) => new(NewProvider(key));

    private static JwtSigningKeyProvider NewProvider(string? key) =>
        new(new AuthOptions
        {
            JwtSigningKeys = key is null ? Array.Empty<string>() : new[] { key },
        },
        NullLogger<JwtSigningKeyProvider>.Instance);
}
