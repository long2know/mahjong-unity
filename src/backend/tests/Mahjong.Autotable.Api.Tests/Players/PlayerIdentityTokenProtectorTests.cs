using System.Text;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Players;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Burke — unit contract for the durable identity credential
/// (<see cref="PlayerIdentityTokenProtector"/>).
///
/// <para><b>Threat being pinned.</b> A player id is PUBLIC: it is broadcast in the autotable
/// <c>seats</c>/<c>nicks</c> wire keys. Before this credential existed, the <c>mahjong_pid</c>
/// cookie WAS the player id and was shape-validated only, so any peer could replay a victim's
/// public id and inherit their durable identity. These facts pin that the cookie is now a
/// bearer credential the server alone can mint:</para>
/// <list type="bullet">
///   <item>signed round-trip returns the exact player id (stable, deterministic);</item>
///   <item>a bare/public player id is rejected as <see cref="PlayerIdentityTokenStatus.LegacyUnsigned"/>;</item>
///   <item>tampering with the id, the MAC, the kid hint, or the version is rejected;</item>
///   <item>a token minted under an unrelated key is rejected (no cross-deployment replay);</item>
///   <item>rotation: the primary key signs, every active key still verifies, and a
///         non-primary acceptance is reported so the caller can re-sign.</item>
/// </list>
/// </summary>
public sealed class PlayerIdentityTokenProtectorTests
{
    private const string KeyA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string KeyB = "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQg==";
    private const string KeyC = "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQw==";

    private static PlayerIdentityTokenProtector Protector(params string[] keys) =>
        new(new JwtSigningKeyProvider(
            new AuthOptions { JwtSigningKeys = keys },
            NullLogger<JwtSigningKeyProvider>.Instance));

    // ── 1. round-trip ────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void Protect_RoundTrips_ToTheExactPlayerId()
    {
        var protector = Protector(KeyA);
        var playerId = Guid.NewGuid().ToString("N");

        var result = protector.Unprotect(protector.Protect(playerId));

        Assert.Equal(PlayerIdentityTokenStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.Equal(playerId, result.PlayerId);
        Assert.True(result.SignedByPrimaryKey);
        Assert.Equal(protector.PrimaryKid, result.Kid);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void Protect_IsDeterministic_SoReconnectsPresentTheSameCookie()
    {
        var protector = Protector(KeyA);
        var playerId = Guid.NewGuid().ToString("N");

        Assert.Equal(protector.Protect(playerId), protector.Protect(playerId));
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void Protect_EmitsTheVersionedFourFieldEnvelope()
    {
        var token = Protector(KeyA).Protect("abc123");
        var parts = token.Split('.');

        Assert.Equal(4, parts.Length);
        Assert.Equal(PlayerIdentityTokenProtector.SchemePrefix, parts[0]);
        Assert.Equal("abc123", Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
        Assert.Equal(32, Base64UrlDecode(parts[3]).Length);         // full HMAC-SHA256, untruncated
        Assert.DoesNotContain(';', token);                          // cookie-safe
        Assert.DoesNotContain(',', token);
        Assert.DoesNotContain(' ', token);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void Protect_DifferentPlayers_ProduceDifferentMacs()
    {
        var protector = Protector(KeyA);
        Assert.NotEqual(protector.Protect("player-one"), protector.Protect("player-two"));
    }

    // ── 2. the exploit: a public player id proves nothing ────────────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public void BarePublicPlayerId_IsRejectedAsLegacyUnsigned()
    {
        // Exactly what Frost replayed: read the victim's playerId off the wire, set it as the
        // cookie. It must never resolve to that identity.
        var victimPublicId = Guid.NewGuid().ToString("N");

        var result = Protector(KeyA).Unprotect(victimPublicId);

        Assert.Equal(PlayerIdentityTokenStatus.LegacyUnsigned, result.Status);
        Assert.False(result.IsValid);
        Assert.Null(result.PlayerId);
        Assert.True(result.WasRejected);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public void ForgingAValidTokenForAKnownPlayerId_IsInfeasibleWithoutTheKey()
    {
        // The attacker knows the victim's public id AND the exact token format, but not the key.
        var victim = Guid.NewGuid().ToString("N");
        var server = Protector(KeyA);
        var attacker = Protector(KeyB);   // any key the attacker can choose

        var forged = attacker.Protect(victim);

        var verdict = server.Unprotect(forged);
        Assert.Equal(PlayerIdentityTokenStatus.BadSignature, verdict.Status);
        Assert.Null(verdict.PlayerId);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public void SwappingThePayloadOfAValidToken_IsRejected()
    {
        // Attacker owns a legitimate cookie and swaps in the victim's public id.
        var server = Protector(KeyA);
        var victim = Guid.NewGuid().ToString("N");
        var attackerToken = server.Protect(Guid.NewGuid().ToString("N"));
        var parts = attackerToken.Split('.');

        var swapped = string.Join('.',
            parts[0], Base64UrlEncode(Encoding.UTF8.GetBytes(victim)), parts[2], parts[3]);

        var verdict = server.Unprotect(swapped);
        Assert.Equal(PlayerIdentityTokenStatus.BadSignature, verdict.Status);
        Assert.Null(verdict.PlayerId);
    }

    // ── 3. tampering / malformed ─────────────────────────────────────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void TamperedSignature_IsRejected()
    {
        var protector = Protector(KeyA);
        var parts = protector.Protect("stable-player").Split('.');
        var mac = Base64UrlDecode(parts[3]);
        mac[0] ^= 0x01;                                             // flip a single bit

        var verdict = protector.Unprotect(string.Join('.', parts[0], parts[1], parts[2], Base64UrlEncode(mac)));

        Assert.Equal(PlayerIdentityTokenStatus.BadSignature, verdict.Status);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void TamperedKidHint_FallsBackToTryAll_AndStillVerifies()
    {
        // The kid is a lookup hint, deliberately outside the MAC. Corrupting it must not be a
        // downgrade vector, and must not break a genuine cookie either.
        var protector = Protector(KeyA, KeyB);
        var parts = protector.Protect("hinted-player").Split('.');

        var verdict = protector.Unprotect(string.Join('.', parts[0], parts[1], "not-a-real-kid", parts[3]));

        Assert.Equal(PlayerIdentityTokenStatus.Valid, verdict.Status);
        Assert.Equal("hinted-player", verdict.PlayerId);
    }

    [Theory, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    [InlineData("")]
    [InlineData(null)]
    public void MissingCookie_IsMissing_NotMalformed(string? token)
    {
        var verdict = Protector(KeyA).Unprotect(token);
        Assert.Equal(PlayerIdentityTokenStatus.Missing, verdict.Status);
        Assert.False(verdict.WasRejected);                          // absence is not an attack
    }

    [Theory, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    [InlineData("mpid1.only.three")]
    [InlineData("mpid1.a.b.c.d")]
    [InlineData("mpid1...")]
    [InlineData("mpid1.!!!!.kid.aGVsbG8")]
    [InlineData("mpid1.YWJj.kid.short")]
    [InlineData("tampered cookie")]
    [InlineData("a b c d")]
    public void MalformedTokens_AreRejectedWithoutThrowing(string token)
    {
        var verdict = Protector(KeyA).Unprotect(token);
        Assert.False(verdict.IsValid);
        Assert.True(verdict.WasRejected);
        Assert.Null(verdict.PlayerId);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void UnknownSchemeVersion_IsRejectedExplicitly()
    {
        var parts = Protector(KeyA).Protect("versioned-player").Split('.');

        var verdict = Protector(KeyA).Unprotect(string.Join('.', "mpid9", parts[1], parts[2], parts[3]));

        Assert.Equal(PlayerIdentityTokenStatus.UnsupportedVersion, verdict.Status);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public void SignedPayloadStillHonoursTheShapeRule_NoLogInjectionEvenWithAKey()
    {
        // Defence in depth: even a validly-signed payload must satisfy IsValidPlayerId, because
        // the id flows into seat state, log scopes, and persistence keys.
        var protector = Protector(KeyA);
        Assert.Throws<ArgumentException>(() => protector.Protect("bad\r\nid"));

        var parts = protector.Protect("goodid").Split('.');
        var forgedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes("bad\r\nid"));
        var verdict = protector.Unprotect(string.Join('.', parts[0], forgedPayload, parts[2], parts[3]));

        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Status, new[]
        {
            PlayerIdentityTokenStatus.Malformed, PlayerIdentityTokenStatus.BadSignature,
        });
    }

    // ── 4. key rotation ──────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-rotation")]
    public void PrimaryKeySigns_AndEveryActiveKeyVerifies()
    {
        var before = Protector(KeyA, KeyB);
        var playerId = Guid.NewGuid().ToString("N");
        var issuedUnderA = before.Protect(playerId);
        var issuedUnderB = Protector(KeyB).Protect(playerId);

        // Operator rotates: B becomes primary, A stays in the fallback window.
        var after = Protector(KeyB, KeyA);

        var oldCookie = after.Unprotect(issuedUnderA);
        Assert.Equal(PlayerIdentityTokenStatus.Valid, oldCookie.Status);
        Assert.Equal(playerId, oldCookie.PlayerId);
        Assert.False(oldCookie.SignedByPrimaryKey);                 // → caller re-signs

        var newCookie = after.Unprotect(issuedUnderB);
        Assert.Equal(PlayerIdentityTokenStatus.Valid, newCookie.Status);
        Assert.True(newCookie.SignedByPrimaryKey);

        // New issuance uses the new primary.
        Assert.Equal(issuedUnderB, after.Protect(playerId));
        Assert.Equal(2, after.ActiveKeyCount);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-rotation")]
    public void KeyRetiredFromTheActiveSet_NoLongerVerifies()
    {
        var retiredCookie = Protector(KeyA).Protect(Guid.NewGuid().ToString("N"));

        // A fully rotated deployment (A dropped) must reject rather than silently accept.
        var verdict = Protector(KeyB, KeyC).Unprotect(retiredCookie);

        Assert.Equal(PlayerIdentityTokenStatus.BadSignature, verdict.Status);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-rotation")]
    public void MacKeyIsHkdfDerived_NotTheRawJwtSigningKey()
    {
        // Key separation: the identity MAC must NOT be computable from the raw signing key, or a
        // JWT-key disclosure in one subsystem would forge identities in another.
        var playerId = "separation-check";
        var token = Protector(KeyA).Protect(playerId);
        var presentedMac = Base64UrlDecode(token.Split('.')[3]);

        var rawKeyMac = System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(KeyA), Encoding.UTF8.GetBytes(playerId));

        Assert.NotEqual(Convert.ToBase64String(rawKeyMac), Convert.ToBase64String(presentedMac));
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-rotation")]
    public void DifferentSigningKeys_ProduceDifferentKids_SoRotationIsObservable()
    {
        Assert.NotEqual(Protector(KeyA).PrimaryKid, Protector(KeyB).PrimaryKid);
        Assert.Equal(Protector(KeyA).PrimaryKid, Protector(KeyA).PrimaryKid);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
