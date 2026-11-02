using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Autotable.Api.Autotable;

/// <summary>
/// Derives an <b>opaque, per-viewer tile handle</b> from server-secret key
/// material (SC-2 / G19). The handle replaces the physically-derived tile
/// identifier (0..107, typeIndex, wall order, deal order) that a client could
/// otherwise use to fingerprint concealed tiles across the wire.
///
/// <para><b>Threat model.</b> The upstream renderer identifies every tile by a
/// stable string id. If that id leaks the physical <c>tileId</c> (or anything
/// order/type-derived), a spectator or opponent who inspects the WebSocket
/// traffic can correlate a concealed tile with a later reveal and reconstruct a
/// hidden hand. The handle must therefore be:</para>
/// <list type="bullet">
///   <item><b>Unlinkable</b> — no client-known value (tileId, seat, type, draw
///         order) can be recovered from or correlated to the handle without the
///         server secret.</item>
///   <item><b>Stable</b> — identical for the same
///         <c>(secret, viewer, game, tile)</c> so a viewer reconnecting mid-game
///         keeps seeing the same handle for the same physical tile (no visual
///         churn / re-shuffle on reconnect).</item>
///   <item><b>Viewer-scoped</b> — two different viewers get unrelated handles
///         for the <i>same</i> physical tile, so cross-referencing two clients'
///         traffic reveals nothing.</item>
/// </list>
///
/// <para><b>Construction.</b> <c>HMAC-SHA256(secret, message)</c> where the
/// message is a versioned, domain-separated, unambiguously length-prefixed
/// encoding of the tuple:</para>
/// <code>
///   version(1) ‖ len(domain) ‖ domain ‖ len(viewer) ‖ viewer ‖ len(game) ‖ game ‖ tileId(4, BE)
/// </code>
/// <para>Every variable-length field carries an explicit 4-byte big-endian
/// length prefix, so no two distinct tuples can produce the same byte stream
/// (canonical, injective encoding — prevents "viewer=ab,game=c" colliding with
/// "viewer=a,game=bc"). The domain label + version byte give message-level
/// domain separation from any other HMAC use of the same key.</para>
///
/// <para><b>Key separation (HKDF).</b> The supplied secret is treated only as
/// input key material: an HMAC key is derived internally with
/// <c>HKDF-Expand/Extract (SHA-256, info = domain label)</c>, and that derived
/// projection key — never the raw secret — is used as the MAC key. This gives
/// <i>key-level</i> domain separation, so the same master secret can back
/// unrelated subsystems without any of them sharing an effective MAC key. A raw
/// JWT/state signing key must therefore never be used directly as the MAC key;
/// pass it (or, preferably, a dedicated secret) only as HKDF input.</para>
///
/// <para>The full 256-bit MAC is emitted as unpadded base64url, giving 256 bits
/// of entropy (well above the required 128) in a JS-safe, URL-safe,
/// non-numeric string.</para>
///
/// <para><b>Integration seam (not wired here — caller's responsibility):</b></para>
/// <list type="bullet">
///   <item>Server secret: supply <b>server-held</b> input key material of at
///         least <see cref="MinimumSecretLengthBytes"/> bytes — a <b>dedicated
///         secret</b> for this purpose (preferred), or an existing master secret
///         fed strictly as HKDF input (this class never uses it as the MAC key
///         directly). Never a client-supplied or client-derivable value, and
///         never a raw JWT signing key used as-is for the MAC.</item>
///   <item>Durable viewer identity: pass the seated <c>PlayerId</c> (durable
///         across reconnects), not a per-connection/socket id. Anonymous
///         spectator policy (shared bucket vs. per-session) is decided by the
///         caller.</item>
/// </list>
///
/// <para>The provider is immutable and holds only the HKDF-derived projection
/// key — it retains <b>no per-connection or per-game mutable state</b>, so a
/// single instance is safe to share across all connections. Secrets and inputs
/// are never logged.</para>
/// </summary>
public sealed class OpaqueTileHandleProvider
{
    /// <summary>Minimum accepted server-secret length. 32 bytes = 256 bits,
    /// matching the HMAC-SHA256 block/output security level.</summary>
    public const int MinimumSecretLengthBytes = 32;

    /// <summary>Physical tile ids are the Changsha deck [0, 108).</summary>
    private const int TileIdExclusiveUpperBound = AutotableSlotMap.TotalTiles;

    /// <summary>Scheme version; bump to rotate the derivation without key change.</summary>
    private const byte Version = 1;

    /// <summary>HKDF-derived projection-key length (bytes) — matches HMAC-SHA256.</summary>
    private const int DerivedKeyLength = 32;

    /// <summary>Domain-separation label mixed into both the HKDF <c>info</c>
    /// (key-level separation) and every MAC input (message-level separation).</summary>
    private static readonly byte[] DomainLabel =
        Encoding.ASCII.GetBytes("mahjong-autotable/opaque-tile-handle/v1");

    private readonly byte[] _hmacKey;

    /// <summary>
    /// Creates a provider whose MAC key is <b>HKDF-derived</b> from
    /// <paramref name="secretKeyMaterial"/> (used only as input key material).
    /// The secret must be at least <see cref="MinimumSecretLengthBytes"/> bytes;
    /// shorter material is rejected outright (no padding/stretching fallback).
    /// </summary>
    public OpaqueTileHandleProvider(ReadOnlySpan<byte> secretKeyMaterial)
    {
        if (secretKeyMaterial.Length < MinimumSecretLengthBytes)
        {
            // Do not echo the secret bytes/length pattern beyond the bound itself.
            throw new ArgumentException(
                $"Server secret key material must be at least {MinimumSecretLengthBytes} bytes.",
                nameof(secretKeyMaterial));
        }

        // Key-level domain separation: the raw secret is IKM only; the MAC key is
        // a dedicated HKDF projection bound to this domain via `info`.
        _hmacKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: secretKeyMaterial.ToArray(),
            outputLength: DerivedKeyLength,
            salt: null,
            info: DomainLabel);
    }

    /// <summary>
    /// Derives the opaque handle for a physical tile as seen by one viewer in
    /// one game. Deterministic: identical inputs (and secret) always yield the
    /// exact same string.
    /// </summary>
    /// <param name="viewerId">Durable viewer identity (e.g. seated PlayerId).
    /// Must be non-empty.</param>
    /// <param name="gameId">Game identifier. Must be non-empty.</param>
    /// <param name="tileId">Physical tile id in [0, 108).</param>
    /// <returns>Unpadded base64url string (256 bits of entropy).</returns>
    public string DeriveHandle(string viewerId, string gameId, int tileId)
    {
        if (string.IsNullOrEmpty(viewerId))
            throw new ArgumentException("viewerId must be non-empty.", nameof(viewerId));
        if (string.IsNullOrEmpty(gameId))
            throw new ArgumentException("gameId must be non-empty.", nameof(gameId));
        if (tileId < 0 || tileId >= TileIdExclusiveUpperBound)
            throw new ArgumentOutOfRangeException(
                nameof(tileId), tileId, $"tileId must be in [0,{TileIdExclusiveUpperBound - 1}].");

        var message = BuildMessage(viewerId, gameId, tileId);
        var mac = HMACSHA256.HashData(_hmacKey, message);
        return Base64UrlEncode(mac);
    }

    /// <summary>
    /// Canonical, injective, length-prefixed MAC input:
    /// <c>version ‖ len(domain) ‖ domain ‖ len(viewer) ‖ viewer ‖ len(game) ‖ game ‖ tileId(BE)</c>.
    /// </summary>
    private static byte[] BuildMessage(string viewerId, string gameId, int tileId)
    {
        var viewerBytes = Encoding.UTF8.GetBytes(viewerId);
        var gameBytes = Encoding.UTF8.GetBytes(gameId);

        var length =
            1                                   // version
            + 4 + DomainLabel.Length            // domain
            + 4 + viewerBytes.Length            // viewer
            + 4 + gameBytes.Length              // game
            + 4;                                // tileId

        var buffer = new byte[length];
        var span = buffer.AsSpan();
        var offset = 0;

        span[offset++] = Version;
        WriteLengthPrefixed(span, ref offset, DomainLabel);
        WriteLengthPrefixed(span, ref offset, viewerBytes);
        WriteLengthPrefixed(span, ref offset, gameBytes);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(offset, 4), tileId);
        offset += 4;

        // offset == length by construction.
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
}
