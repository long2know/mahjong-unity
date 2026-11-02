using System.Security.Cryptography;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 6 — Bishop. Resolved RSA private-key record for RS256
/// JWT signing. Mirrors <see cref="JwtSigningKey"/> for the HMAC path:
/// the <see cref="Kid"/> is a deterministic hash of the public-key
/// SPKI so any two processes loading the same PEM derive the same
/// identifier — the JWT <c>kid</c> header is therefore portable
/// across nodes without an external key catalog.
///
/// <para>The <see cref="Rsa"/> instance owns the loaded private key
/// for the lifetime of the process (the
/// <see cref="JwtSigningKeyProvider"/> is a singleton and never
/// disposes the keys mid-process).</para>
///
/// <para>The kid is derived from the public-key SPKI (NOT the private
/// key material) so the JWKS document — which only carries the public
/// half — references the same identifier the issuer stamps in the
/// JWT header.</para>
/// </summary>
public sealed class JwtRsaSigningKey : IDisposable
{
    /// <summary>Index in <see cref="AuthOptions.JwtRsaKeys"/> at load
    /// time. Zero is the active signer; later entries are accepted on
    /// validation only.</summary>
    public int Index { get; }

    /// <summary>The RSA instance carrying the imported private key.
    /// Used directly by <see cref="JwtIssuingService"/> for signing
    /// and by <see cref="JwtValidationService"/> for verification.</summary>
    public RSA Rsa { get; }

    /// <summary>Deterministic key identifier — 8-byte truncated
    /// SHA-256 of the public-key SPKI, base64url-encoded without
    /// padding. The SPKI is portable across PKCS#1/PKCS#8 imports so
    /// the kid is independent of the source PEM encoding.</summary>
    public string Kid { get; }

    /// <summary>RSA modulus (n) — base64url-encoded big-endian bytes
    /// for the JWKS publish path.</summary>
    public string ModulusBase64Url { get; }

    /// <summary>RSA public exponent (e) — base64url-encoded
    /// big-endian bytes for the JWKS publish path. Typically
    /// <c>"AQAB"</c> (65537) but the encoder honours whatever the
    /// imported key carries.</summary>
    public string ExponentBase64Url { get; }

    public JwtRsaSigningKey(int index, string pem)
    {
        if (string.IsNullOrEmpty(pem))
            throw new ArgumentException("RSA private key PEM must not be empty.", nameof(pem));

        Index = index;
        Rsa = RSA.Create();
        Rsa.ImportFromPem(pem);

        var parameters = Rsa.ExportParameters(includePrivateParameters: false);
        ModulusBase64Url = Base64UrlEncode(parameters.Modulus ?? Array.Empty<byte>());
        ExponentBase64Url = Base64UrlEncode(parameters.Exponent ?? Array.Empty<byte>());

        var spki = Rsa.ExportSubjectPublicKeyInfo();
        Kid = ComputeKid(spki);
    }

    /// <summary>
    /// Computes a deterministic <see cref="Kid"/> from the
    /// SubjectPublicKeyInfo (SPKI) bytes — first 8 bytes of SHA-256,
    /// base64url-encoded without padding. Public so call sites
    /// (audit emission, tests) can derive the same identifier without
    /// instantiating a key.
    /// </summary>
    public static string ComputeKid(ReadOnlySpan<byte> spki)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(spki, digest);
        return JwtIssuingService.Base64UrlEncode(digest[..8]);
    }

    /// <summary>Base64url-encodes bytes without padding — used by the
    /// JWKS publish path for the modulus + exponent fields.</summary>
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => JwtIssuingService.Base64UrlEncode(bytes);

    public void Dispose() => Rsa.Dispose();
}
