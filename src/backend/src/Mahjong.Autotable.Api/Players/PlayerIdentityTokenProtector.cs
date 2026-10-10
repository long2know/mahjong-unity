using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Autotable.Api.Auth;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Why a verdict is (or is not) trusted. Every non-<see cref="Valid"/> value is a
/// hard rejection: the caller must rotate to a freshly minted identity and must
/// NEVER fall back to the presented value.
/// </summary>
public enum PlayerIdentityTokenStatus
{
    /// <summary>Signature verified against an active key; <c>PlayerId</c> is trustworthy.</summary>
    Valid,

    /// <summary>No cookie was presented at all.</summary>
    Missing,

    /// <summary>
    /// A bare player identifier (the pre-signing Wave-6 cookie shape) or any other value
    /// that is not a versioned identity token. Player ids are PUBLIC — they appear in the
    /// <c>seats</c> / <c>nicks</c> wire keys — so an unsigned value proves nothing and is
    /// rejected outright.
    /// </summary>
    LegacyUnsigned,

    /// <summary>Structurally invalid: wrong field count, bad base64url, empty/oversized id.</summary>
    Malformed,

    /// <summary>Recognisable token envelope but an unknown scheme version.</summary>
    UnsupportedVersion,

    /// <summary>Well-formed, but no active key produces the presented MAC (forged or rotated out).</summary>
    BadSignature,
}

/// <summary>
/// Outcome of <see cref="PlayerIdentityTokenProtector.Unprotect"/>.
/// </summary>
/// <param name="Status">Verdict; only <see cref="PlayerIdentityTokenStatus.Valid"/> yields a player id.</param>
/// <param name="PlayerId">Verified durable player identifier, or <c>null</c>.</param>
/// <param name="Kid">Key id that produced the accepted MAC, or <c>null</c>.</param>
/// <param name="SignedByPrimaryKey">
/// <c>true</c> when the accepted key is the current signer (index 0). <c>false</c> means the
/// cookie was minted under an older-but-still-active key and should be transparently re-signed.
/// </param>
public readonly record struct PlayerIdentityTokenResult(
    PlayerIdentityTokenStatus Status,
    string? PlayerId,
    string? Kid,
    bool SignedByPrimaryKey)
{
    /// <summary>True only when the credential verified against an active key.</summary>
    public bool IsValid => Status == PlayerIdentityTokenStatus.Valid && PlayerId is not null;

    /// <summary>True when a cookie was presented but rejected — worth logging / counting.</summary>
    public bool WasRejected => Status is not PlayerIdentityTokenStatus.Valid
                                       and not PlayerIdentityTokenStatus.Missing;

    internal static PlayerIdentityTokenResult Fail(PlayerIdentityTokenStatus status) =>
        new(status, null, null, false);
}

/// <summary>
/// Issues + verifies the <b>durable identity credential</b> carried by the
/// <c>mahjong_pid</c> cookie.
///
/// <para><b>Threat model.</b> A player id is a PUBLIC identifier: it is broadcast in the
/// autotable <c>seats</c> / <c>nicks</c> wire keys, returned by <c>POST /api/identity</c>, and
/// stamped on leaderboard rows. Before this class the cookie <i>was</i> the bare player id and
/// was only shape-checked, so any peer could read a victim's id off the wire, replay it as their
/// own cookie, and inherit the victim's durable identity — and with it the reconnect seat
/// inference that projects the victim's concealed hand and authorises seat actions. The cookie
/// must therefore be a <b>bearer credential</b> that only the server can produce, not a
/// restatement of a public identifier.</para>
///
/// <para><b>Token format (v1).</b> Four dot-separated fields, all cookie-safe:</para>
/// <code>
///   mpid1.&lt;base64url(playerId)&gt;.&lt;kid&gt;.&lt;base64url(HMAC-SHA256)&gt;
/// </code>
/// <para>The MAC covers a canonical, injective, length-prefixed encoding — the same shape the
/// repository already uses for opaque tile handles (see <c>OpaqueTileHandleProvider</c>):</para>
/// <code>
///   version(1) ‖ len(domain)(4,BE) ‖ domain ‖ len(playerId)(4,BE) ‖ playerId(UTF-8)
/// </code>
/// <para>Length prefixes make the encoding injective (no two distinct inputs share a byte
/// stream); the version byte + domain label give message-level domain separation. The
/// <c>kid</c> is a lookup hint only and is deliberately outside the MAC: verification falls
/// back to trying every active key, so tampering with the hint cannot downgrade anything.</para>
///
/// <para><b>Keys + rotation (reused, not reinvented).</b> Key material comes from the existing
/// <see cref="JwtSigningKeyProvider"/>: index 0 is the primary signer and every loaded key
/// verifies, exactly matching the JWT <c>kid</c>-fast-path-then-try-all rotation semantics
/// documented in <c>docs/jwt-rotation.md</c>. The raw signing key is <b>never</b> used as the MAC
/// key: each key is treated as HKDF input key material and expanded
/// (<c>HKDF(SHA-256, info = domain label)</c>) into a dedicated identity-cookie MAC key, so this
/// subsystem shares no effective key with JWT issuance. Nothing here mints or stores its own
/// secret, and no secret is ever logged.</para>
///
/// <para><b>Restart semantics.</b> Production must supply operator keys
/// (<c>Authentication__JwtSigningKeys__0</c>), so cookies survive restarts and rolling deploys —
/// <see cref="PlayerIdentityStartupValidator"/> fails the boot closed when they are absent. In
/// Development/Test the provider mints a per-process key, so identities intentionally reset when
/// the process restarts; that is logged explicitly at startup.</para>
/// </summary>
public sealed class PlayerIdentityTokenProtector
{
    /// <summary>Scheme prefix + version. Bump to roll the derivation without changing keys.</summary>
    public const string SchemePrefix = "mpid1";

    /// <summary>Wire-format version byte mixed into every MAC input.</summary>
    private const byte Version = 1;

    /// <summary>Derived MAC-key length (bytes) — matches HMAC-SHA256.</summary>
    private const int DerivedKeyLength = 32;

    /// <summary>Expected MAC length (bytes) — full, untruncated HMAC-SHA256.</summary>
    private const int MacLength = 32;

    /// <summary>Domain separation for both the HKDF <c>info</c> and every MAC message.</summary>
    private static readonly byte[] DomainLabel =
        Encoding.ASCII.GetBytes("mahjong-autotable/player-identity-cookie/v1");

    private readonly IdentityKey[] _keys;

    public PlayerIdentityTokenProtector(JwtSigningKeyProvider signingKeys)
    {
        ArgumentNullException.ThrowIfNull(signingKeys);
        var source = signingKeys.AllKeys;
        if (source.Count == 0)
        {
            // Unreachable in practice: the provider always resolves at least one key or throws
            // at construction. Fail loudly rather than silently minting an ad-hoc secret.
            throw new InvalidOperationException(
                "No JWT signing keys are loaded; the player-identity cookie cannot be signed. "
                + "See docs/jwt-rotation.md §1.");
        }

        _keys = new IdentityKey[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            _keys[i] = new IdentityKey(source[i].Kid, DeriveMacKey(source[i].Material));
        }
    }

    /// <summary>Key id of the current signer — the key new cookies are minted under.</summary>
    public string PrimaryKid => _keys[0].Kid;

    /// <summary>Number of keys accepted on verification (1 = no rotation window configured).</summary>
    public int ActiveKeyCount => _keys.Length;

    /// <summary>
    /// Signs <paramref name="playerId"/> with the primary key and returns the cookie value.
    /// The player id must already satisfy <see cref="PlayerIdentityService.IsValidPlayerId"/>.
    /// </summary>
    public string Protect(string playerId)
    {
        if (!PlayerIdentityService.IsValidPlayerId(playerId))
            throw new ArgumentException("playerId must be a non-empty opaque token.", nameof(playerId));

        var idBytes = Encoding.UTF8.GetBytes(playerId);
        var mac = HMACSHA256.HashData(_keys[0].MacKey, BuildMessage(idBytes));
        return string.Concat(
            SchemePrefix, ".",
            Base64UrlEncode(idBytes), ".",
            _keys[0].Kid, ".",
            Base64UrlEncode(mac));
    }

    /// <summary>
    /// Verifies a presented cookie value. Never throws; every failure mode maps to an explicit
    /// <see cref="PlayerIdentityTokenStatus"/> so callers can fail closed and reissue.
    /// Comparison is constant-time for every candidate key.
    /// </summary>
    public PlayerIdentityTokenResult Unprotect(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return PlayerIdentityTokenResult.Fail(PlayerIdentityTokenStatus.Missing);

        var parts = token.Split('.');
        if (parts.Length != 4)
        {
            // A bare player id (the pre-signing cookie shape) lands here. Call it out
            // specifically so migration telemetry can distinguish it from garbage.
            return PlayerIdentityTokenResult.Fail(
                parts.Length == 1 && PlayerIdentityService.IsValidPlayerId(token)
                    ? PlayerIdentityTokenStatus.LegacyUnsigned
                    : PlayerIdentityTokenStatus.Malformed);
        }

        if (!string.Equals(parts[0], SchemePrefix, StringComparison.Ordinal))
        {
            // Any other `<scheme>.<...>` envelope: unknown/rolled scheme version.
            return PlayerIdentityTokenResult.Fail(PlayerIdentityTokenStatus.UnsupportedVersion);
        }

        if (!TryBase64UrlDecode(parts[1], out var idBytes)
            || !TryBase64UrlDecode(parts[3], out var mac)
            || mac.Length != MacLength
            || idBytes.Length == 0)
        {
            return PlayerIdentityTokenResult.Fail(PlayerIdentityTokenStatus.Malformed);
        }

        string playerId;
        try
        {
            playerId = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(idBytes);
        }
        catch (ArgumentException)
        {
            return PlayerIdentityTokenResult.Fail(PlayerIdentityTokenStatus.Malformed);
        }

        // Re-apply the shape rule on the SIGNED payload too: the id flows into seat state, log
        // scopes and persistence keys, so a key compromise must not also become log injection.
        if (!PlayerIdentityService.IsValidPlayerId(playerId))
            return PlayerIdentityTokenResult.Fail(PlayerIdentityTokenStatus.Malformed);

        var message = BuildMessage(idBytes);
        var kidHint = parts[2];

        // kid fast-path, then try-all fallback — identical to JwtValidationService, so a cookie
        // minted under any still-active key keeps verifying across a rotation.
        var hinted = IndexOfKid(kidHint);
        if (hinted >= 0 && Verify(_keys[hinted], message, mac))
            return new PlayerIdentityTokenResult(PlayerIdentityTokenStatus.Valid, playerId, _keys[hinted].Kid, hinted == 0);

        for (var i = 0; i < _keys.Length; i++)
        {
            if (i == hinted) continue;
            if (Verify(_keys[i], message, mac))
                return new PlayerIdentityTokenResult(PlayerIdentityTokenStatus.Valid, playerId, _keys[i].Kid, i == 0);
        }

        return PlayerIdentityTokenResult.Fail(PlayerIdentityTokenStatus.BadSignature);
    }

    private static bool Verify(in IdentityKey key, byte[] message, byte[] presentedMac)
    {
        Span<byte> expected = stackalloc byte[MacLength];
        HMACSHA256.HashData(key.MacKey, message, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presentedMac);
    }

    private int IndexOfKid(string kid)
    {
        for (var i = 0; i < _keys.Length; i++)
        {
            if (string.Equals(_keys[i].Kid, kid, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static byte[] DeriveMacKey(byte[] signingKeyMaterial) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: signingKeyMaterial,
            outputLength: DerivedKeyLength,
            salt: null,
            info: DomainLabel);

    /// <summary>
    /// Canonical, injective MAC input:
    /// <c>version ‖ len(domain) ‖ domain ‖ len(playerId) ‖ playerId</c>.
    /// </summary>
    private static byte[] BuildMessage(ReadOnlySpan<byte> idBytes)
    {
        var buffer = new byte[1 + 4 + DomainLabel.Length + 4 + idBytes.Length];
        var span = buffer.AsSpan();
        var offset = 0;
        span[offset++] = Version;
        WriteLengthPrefixed(span, ref offset, DomainLabel);
        WriteLengthPrefixed(span, ref offset, idBytes);
        return buffer;
    }

    private static void WriteLengthPrefixed(Span<byte> span, ref int offset, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(offset, 4), value.Length);
        offset += 4;
        value.CopyTo(span.Slice(offset, value.Length));
        offset += value.Length;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value)) return false;
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 0: break;
            default: return false;
        }
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private readonly record struct IdentityKey(string Kid, byte[] MacKey);
}
