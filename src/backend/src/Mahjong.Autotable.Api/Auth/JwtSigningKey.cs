using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 4 — Bishop. Resolved HMAC-SHA256 signing-key record
/// used by <see cref="JwtIssuingService"/> +
/// <see cref="JwtValidationService"/>. The <see cref="Kid"/> is a
/// deterministic hash of the key material so two processes loading
/// the same key derive the same identifier — the JWT <c>kid</c>
/// header can therefore be matched directly to a key in the fallback
/// list without an out-of-band catalog.
///
/// <para>The <see cref="Index"/> is the position of this key inside
/// <c>AuthOptions.JwtSigningKeys</c> at load time (0 = active signer).
/// The index is preserved on the record so the audit trail can
/// emit a per-key Kind even after the operator rotates the list.</para>
/// </summary>
public sealed class JwtSigningKey
{
    /// <summary>Index in the configured fallback list at load time.
    /// Zero is the active signer; any non-zero entry is a fallback
    /// key accepted for validation only.</summary>
    public int Index { get; }

    /// <summary>Raw HMAC-SHA256 key bytes (UTF-8 encoding of the
    /// configured string).</summary>
    public byte[] Material { get; }

    /// <summary>Deterministic key identifier (8-byte truncated
    /// SHA-256 of <see cref="Material"/>, base64url-encoded). Two
    /// processes loading the same key string derive the same
    /// <see cref="Kid"/> so the JWT header is portable across
    /// nodes without an external key catalog.</summary>
    public string Kid { get; }

    public JwtSigningKey(int index, string material)
    {
        if (string.IsNullOrEmpty(material))
            throw new ArgumentException("Signing key material must not be empty.", nameof(material));
        Index = index;
        Material = Encoding.UTF8.GetBytes(material);
        Kid = ComputeKid(Material);
    }

    /// <summary>
    /// Computes the deterministic <see cref="Kid"/> from raw key
    /// bytes: first 8 bytes of SHA-256, base64url-encoded without
    /// padding. Public so call sites (audit emission, tests) can
    /// derive the same identifier without instantiating a key.
    /// </summary>
    public static string ComputeKid(ReadOnlySpan<byte> material)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(material, digest);
        Span<byte> truncated = digest[..8];
        return Convert.ToBase64String(truncated)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
