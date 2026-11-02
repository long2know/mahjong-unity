using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 1 — HMAC-signed OAuth state token. The Wave 8
/// implementation stored a plain 32-byte nonce in a cookie and compared
/// it to the <c>state</c> query param on callback. That defends against
/// cross-site CSRF (because the attacker can't read the cookie) but
/// gives no protection against an attacker tampering with the state
/// value itself.
///
/// <para>This class issues a state of the form
/// <c>base64url(nonce(16) | expiryUnix(8) | hmac(32))</c>:</para>
/// <list type="bullet">
///   <item><b>nonce</b> — 16 random bytes (unique per authorize request).</item>
///   <item><b>expiryUnix</b> — big-endian unix-seconds when this state
///         expires (caller-supplied TTL, default 10 minutes).</item>
///   <item><b>hmac</b> — HMAC-SHA256 over (nonce || expiry) using the
///         configured signing key, truncated to 32 bytes.</item>
/// </list>
///
/// <para><see cref="Verify"/> checks the HMAC + expiry. Returns the
/// embedded nonce on success so the caller can pin it against the
/// callback cookie for an additional CSRF layer. The signing key is
/// derived from <see cref="AuthOptions.StateSigningKey"/>; when empty
/// a per-process random key is minted at startup (a warning is logged
/// so operators see the surface).</para>
/// </summary>
public sealed class OAuthStateProtector
{
    /// <summary>Default state TTL — 10 minutes is enough for the user to
    /// complete a provider login on a slow connection without giving
    /// an attacker a long replay window.</summary>
    public static readonly TimeSpan DefaultStateTtl = TimeSpan.FromMinutes(10);

    private const int NonceLength = 16;
    private const int ExpiryLength = 8;
    private const int HmacLength = 32;
    private const int TotalLength = NonceLength + ExpiryLength + HmacLength;

    private readonly byte[] _signingKey;

    public OAuthStateProtector(AuthOptions options, ILogger<OAuthStateProtector>? logger = null)
    {
        var configured = options?.StateSigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            _signingKey = RandomNumberGenerator.GetBytes(32);
            logger?.LogWarning(
                "Authentication:StateSigningKey is empty; minted a per-process random HMAC key. "
                + "In production set a stable secret so rolling restarts don't invalidate in-flight OAuth states.");
        }
        else
        {
            // Hash the configured string into a stable 256-bit key. This
            // accepts hex / base64 / passphrase inputs uniformly.
            _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        }
    }

    /// <summary>
    /// Issues a signed state token whose embedded nonce is also returned.
    /// The caller is expected to write the raw nonce into a session
    /// cookie so the callback can re-verify the binding.
    /// </summary>
    public StateIssue Issue(TimeSpan? ttl = null)
    {
        var nonceBytes = RandomNumberGenerator.GetBytes(NonceLength);
        var expiry = DateTimeOffset.UtcNow.Add(ttl ?? DefaultStateTtl).ToUnixTimeSeconds();

        var payload = new byte[NonceLength + ExpiryLength];
        Buffer.BlockCopy(nonceBytes, 0, payload, 0, NonceLength);
        WriteInt64BigEndian(payload, NonceLength, expiry);

        var hmac = HMACSHA256.HashData(_signingKey, payload);

        var combined = new byte[TotalLength];
        Buffer.BlockCopy(payload, 0, combined, 0, payload.Length);
        Buffer.BlockCopy(hmac, 0, combined, payload.Length, HmacLength);

        var token = Base64UrlEncode(combined);
        var nonce = Base64UrlEncode(nonceBytes);
        return new StateIssue(token, nonce);
    }

    /// <summary>
    /// Validates a state token. Returns <see cref="StateVerifyResult.Success(string)"/>
    /// (with the embedded nonce, base64-url encoded so callers can
    /// compare to the cookie) on success, or a failure variant on
    /// bad signature / expiry / malformed input.
    /// </summary>
    public StateVerifyResult Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return StateVerifyResult.Malformed("empty");
        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return StateVerifyResult.Malformed("base64 decode failed");
        }
        if (bytes.Length != TotalLength) return StateVerifyResult.Malformed("length mismatch");

        var payload = new byte[NonceLength + ExpiryLength];
        Buffer.BlockCopy(bytes, 0, payload, 0, payload.Length);
        var hmac = new byte[HmacLength];
        Buffer.BlockCopy(bytes, payload.Length, hmac, 0, HmacLength);

        var expected = HMACSHA256.HashData(_signingKey, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, hmac))
        {
            return StateVerifyResult.BadSignature();
        }

        var expiry = ReadInt64BigEndian(payload, NonceLength);
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
        {
            return StateVerifyResult.Expired();
        }

        var nonceBytes = new byte[NonceLength];
        Buffer.BlockCopy(payload, 0, nonceBytes, 0, NonceLength);
        return StateVerifyResult.Success(Base64UrlEncode(nonceBytes));
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 0: break;
            default: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(padded);
    }

    private static void WriteInt64BigEndian(byte[] buf, int offset, long value)
    {
        for (var i = 7; i >= 0; i--)
        {
            buf[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    private static long ReadInt64BigEndian(byte[] buf, int offset)
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | buf[offset + i];
        }
        return value;
    }
}

/// <summary>
/// Phase K Wave 1 — paired output of <see cref="OAuthStateProtector.Issue"/>.
/// </summary>
/// <param name="Token">Base64-url state token to embed in the
/// <c>state</c> query param sent to the provider.</param>
/// <param name="Nonce">Base64-url nonce extracted from the token —
/// the caller stores this in a cookie so the callback can verify
/// that the user-agent that initiated the flow is the same one
/// completing it.</param>
public sealed record StateIssue(string Token, string Nonce);

/// <summary>
/// Phase K Wave 1 — outcome of <see cref="OAuthStateProtector.Verify"/>.
/// </summary>
public sealed record StateVerifyResult(bool Ok, string? Nonce, string? Reason)
{
    public static StateVerifyResult Success(string nonce) => new(true, nonce, null);
    public static StateVerifyResult BadSignature() => new(false, null, "bad-signature");
    public static StateVerifyResult Expired() => new(false, null, "expired");
    public static StateVerifyResult Malformed(string reason) => new(false, null, "malformed:" + reason);
}
