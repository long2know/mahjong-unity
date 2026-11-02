using System.Security.Cryptography;
using System.Text;
using Mahjong.Autotable.Api.Autotable;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// SC-2 / G19 — opaque per-viewer tile handle derivation. Asserts the handle is
/// deterministic, viewer/game/tile-scoped, unlinkable from client-known values,
/// and shaped as a JS-safe high-entropy base64url string.
/// </summary>
public class OpaqueTileHandleProviderTests
{
    private static byte[] Secret(byte seed = 0x11)
    {
        var b = new byte[OpaqueTileHandleProvider.MinimumSecretLengthBytes];
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(seed + i);
        return b;
    }

    private static OpaqueTileHandleProvider Provider(byte seed = 0x11) => new(Secret(seed));

    // ── determinism / reconnect stability ─────────────────────────────

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_IsDeterministic_ForSameInputs()
    {
        var a = Provider();
        var b = Provider(); // fresh instance, identical secret bytes

        var h1 = a.DeriveHandle("viewer-1", "game-1", 42);
        var h2 = a.DeriveHandle("viewer-1", "game-1", 42);
        var h3 = b.DeriveHandle("viewer-1", "game-1", 42);

        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3); // byte-identical across instances => reconnect stable
    }

    // ── viewer / game / tile scoping ──────────────────────────────────

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_DiffersByViewer()
    {
        var p = Provider();
        Assert.NotEqual(
            p.DeriveHandle("viewer-A", "game-1", 7),
            p.DeriveHandle("viewer-B", "game-1", 7));
    }

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_DiffersByGame()
    {
        var p = Provider();
        Assert.NotEqual(
            p.DeriveHandle("viewer-A", "game-1", 7),
            p.DeriveHandle("viewer-A", "game-2", 7));
    }

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_DiffersByTile()
    {
        var p = Provider();
        Assert.NotEqual(
            p.DeriveHandle("viewer-A", "game-1", 7),
            p.DeriveHandle("viewer-A", "game-1", 8));
    }

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_DiffersBySecret()
    {
        Assert.NotEqual(
            Provider(0x11).DeriveHandle("viewer-A", "game-1", 7),
            Provider(0x22).DeriveHandle("viewer-A", "game-1", 7));
    }

    // ── canonical length-prefixed encoding (no field-boundary collisions) ──

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_LengthPrefixing_PreventsFieldBoundaryCollision()
    {
        var p = Provider();
        // Without length-prefixing, ("ab","c") and ("a","bc") would concatenate
        // to the same bytes. They must produce different handles.
        Assert.NotEqual(
            p.DeriveHandle("ab", "c", 5),
            p.DeriveHandle("a", "bc", 5));
    }

    // ── charset / string shape / entropy length ───────────────────────

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_UsesOnlyBase64UrlCharset_NoPadding()
    {
        var p = Provider();
        var h = p.DeriveHandle("viewer-1", "game-1", 42);

        Assert.DoesNotContain('+', h);
        Assert.DoesNotContain('/', h);
        Assert.DoesNotContain('=', h);
        Assert.All(h, c =>
            Assert.True(
                (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') ||
                (c is >= '0' and <= '9') || c is '-' or '_',
                $"unexpected char '{c}'"));
    }

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_ReturnsNonNumericString()
    {
        var p = Provider();
        var h = p.DeriveHandle("viewer-1", "game-1", 42);

        Assert.False(string.IsNullOrEmpty(h));
        // Must NOT be a bare integer (would leak/mimic a physical id).
        Assert.False(int.TryParse(h, out _));
        Assert.False(long.TryParse(h, out _));
    }

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_HasAtLeast128BitsOfEntropy()
    {
        var p = Provider();
        var h = p.DeriveHandle("viewer-1", "game-1", 42);

        // base64url encodes 6 bits/char; 22 chars => 132 bits (>= 128).
        Assert.True(h.Length >= 22, $"handle too short for 128-bit entropy: {h.Length} chars");

        // Round-trips to full 32-byte (256-bit) HMAC output.
        var padded = h.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
        var raw = Convert.FromBase64String(padded);
        Assert.Equal(32, raw.Length);
    }

    // ── never a physical-id / order-derived value ─────────────────────

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_IsNever_ATileIdString_ForAnyTile()
    {
        var p = Provider();
        for (var tile = 0; tile < AutotableSlotMap.TotalTiles; tile++)
        {
            var h = p.DeriveHandle("viewer-1", "game-1", tile);
            Assert.NotEqual(tile.ToString(), h);
            Assert.NotEqual((tile / 4).ToString(), h); // typeIndex
        }
    }

    // ── 108-tile collision-free per viewer ────────────────────────────

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_IsCollisionFree_Across108Tiles_PerViewer()
    {
        var p = Provider();
        var set = new HashSet<string>();
        for (var tile = 0; tile < AutotableSlotMap.TotalTiles; tile++)
        {
            Assert.True(set.Add(p.DeriveHandle("viewer-1", "game-1", tile)),
                $"handle collision at tile {tile}");
        }
        Assert.Equal(AutotableSlotMap.TotalTiles, set.Count);
    }

    // ── cross-viewer unlinkability (no equal-by-tile handle) ──────────

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_TwoViewers_ShareNoHandle_ForAnyTile()
    {
        var p = Provider();
        var viewerA = new HashSet<string>();
        for (var tile = 0; tile < AutotableSlotMap.TotalTiles; tile++)
            viewerA.Add(p.DeriveHandle("viewer-A", "game-1", tile));

        for (var tile = 0; tile < AutotableSlotMap.TotalTiles; tile++)
        {
            var b = p.DeriveHandle("viewer-B", "game-1", tile);
            Assert.DoesNotContain(b, viewerA);
        }
    }

    // ── argument validation (tight, no silent fallback) ───────────────

    [Fact, Trait("Category", "SC2")]
    public void Constructor_Rejects_ShortSecret()
    {
        var shortKey = new byte[OpaqueTileHandleProvider.MinimumSecretLengthBytes - 1];
        Assert.Throws<ArgumentException>(() => new OpaqueTileHandleProvider(shortKey));
    }

    [Fact, Trait("Category", "SC2")]
    public void Constructor_Rejects_EmptySecret()
    {
        Assert.Throws<ArgumentException>(() => new OpaqueTileHandleProvider(ReadOnlySpan<byte>.Empty));
    }

    [Fact, Trait("Category", "SC2")]
    public void Constructor_Accepts_ExactMinimumSecret()
    {
        var key = RandomNumberGenerator.GetBytes(OpaqueTileHandleProvider.MinimumSecretLengthBytes);
        var p = new OpaqueTileHandleProvider(key);
        Assert.False(string.IsNullOrEmpty(p.DeriveHandle("v", "g", 0)));
    }

    [Theory, Trait("Category", "SC2")]
    [InlineData(null)]
    [InlineData("")]
    public void DeriveHandle_Rejects_MissingViewer(string? viewer)
    {
        var p = Provider();
        Assert.Throws<ArgumentException>(() => p.DeriveHandle(viewer!, "game-1", 0));
    }

    [Theory, Trait("Category", "SC2")]
    [InlineData(null)]
    [InlineData("")]
    public void DeriveHandle_Rejects_MissingGame(string? game)
    {
        var p = Provider();
        Assert.Throws<ArgumentException>(() => p.DeriveHandle("viewer-1", game!, 0));
    }

    [Theory, Trait("Category", "SC2")]
    [InlineData(-1)]
    [InlineData(AutotableSlotMap.TotalTiles)]
    [InlineData(1000)]
    public void DeriveHandle_Rejects_OutOfRangeTile(int tile)
    {
        var p = Provider();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.DeriveHandle("viewer-1", "game-1", tile));
    }

    // ── independent HMAC recomputation (secret is essential) ──────────

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_MatchesIndependentHmacRecomputation()
    {
        var secret = Secret();
        var p = new OpaqueTileHandleProvider(secret);
        var handle = p.DeriveHandle("viewer-1", "game-1", 42);

        var domain = Encoding.ASCII.GetBytes("mahjong-autotable/opaque-tile-handle/v1");

        // Key is HKDF-derived from the secret (secret is IKM only, never the MAC key).
        var hmacKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: secret,
            outputLength: 32,
            salt: (byte[]?)null,
            info: domain);

        // Reconstruct the canonical length-prefixed message independently.
        var viewer = Encoding.UTF8.GetBytes("viewer-1");
        var game = Encoding.UTF8.GetBytes("game-1");
        using var ms = new MemoryStream();
        ms.WriteByte(1); // version
        void WriteLp(byte[] v)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, v.Length);
            ms.Write(len);
            ms.Write(v);
        }
        WriteLp(domain);
        WriteLp(viewer);
        WriteLp(game);
        Span<byte> tid = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(tid, 42);
        ms.Write(tid);

        var mac = HMACSHA256.HashData(hmacKey, ms.ToArray());
        var expected = Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        Assert.Equal(expected, handle);
    }

    [Fact, Trait("Category", "SC2")]
    public void DeriveHandle_DoesNotUseRawSecretAsMacKey()
    {
        // Key-separation guard: HMAC keyed with the RAW secret must NOT reproduce
        // the handle — the provider keys with an HKDF projection of the secret.
        var secret = Secret();
        var p = new OpaqueTileHandleProvider(secret);
        var handle = p.DeriveHandle("viewer-1", "game-1", 42);

        var domain = Encoding.ASCII.GetBytes("mahjong-autotable/opaque-tile-handle/v1");
        var viewer = Encoding.UTF8.GetBytes("viewer-1");
        var game = Encoding.UTF8.GetBytes("game-1");
        using var ms = new MemoryStream();
        ms.WriteByte(1);
        void WriteLp(byte[] v)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, v.Length);
            ms.Write(len);
            ms.Write(v);
        }
        WriteLp(domain);
        WriteLp(viewer);
        WriteLp(game);
        Span<byte> tid = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(tid, 42);
        ms.Write(tid);

        var rawKeyed = HMACSHA256.HashData(secret, ms.ToArray());
        var rawKeyedHandle = Convert.ToBase64String(rawKeyed).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        Assert.NotEqual(rawKeyedHandle, handle);
    }
}
