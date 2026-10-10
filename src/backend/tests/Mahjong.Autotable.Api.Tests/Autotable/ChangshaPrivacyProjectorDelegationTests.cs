using System.Security.Cryptography;
using System.Text;
using Mahjong.Autotable.Api.Autotable;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// SC-2 / G19 — delegation + cryptographic-domain contract for
/// <see cref="ChangshaPrivacyProjector"/>. Verifies the projector carries NO
/// crypto of its own: every opaque handle is produced by the approved HKDF
/// <see cref="OpaqueTileHandleProvider"/> (key separation + domain-separated,
/// length-prefixed encoding), preserving deterministic reconnect stability and
/// viewer/game/tile separation. These guard against a regression back to the
/// retired interim raw-HMAC (no HKDF, \u001f-delimited, 96-bit truncation).
/// </summary>
public class ChangshaPrivacyProjectorDelegationTests
{
    private const string HandlePrefix = "h_";
    private static byte[] Secret() =>
        Encoding.UTF8.GetBytes("bishop-sc2-test-secret-32bytes!!"); // exactly 32 bytes

    // ── delegation: handle == prefix + provider.DeriveHandle(...) ─────

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void Handle_DelegatesTo_ApprovedHkdfProvider_ForEveryTile()
    {
        var secret = Secret();
        var proj = ChangshaPrivacyProjector.Create(secret, "game-1", "player-A")!;
        var provider = new OpaqueTileHandleProvider(secret);

        for (var tile = 0; tile < 108; tile++)
        {
            var expected = HandlePrefix + provider.DeriveHandle("player-A", "game-1", tile);
            Assert.Equal(expected, proj.Handle(tile));
        }
    }

    // ── the retired raw-HMAC interim scheme must NOT reproduce a handle ─

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void Handle_IsNot_RetiredInterimRawHmac()
    {
        var secret = Secret();
        var proj = ChangshaPrivacyProjector.Create(secret, "game-1", "player-A")!;

        // Reconstruct the retired interim derivation exactly:
        //   prefix = "game-1\u001fplayer-A\u001f"; msg = prefix || int32LE(tileId);
        //   mac = HMACSHA256(secret, msg); handle = "h_" + base64url(mac[0..12]).
        var prefix = Encoding.UTF8.GetBytes("game-1\u001fplayer-A\u001f");
        for (var tile = 0; tile < 108; tile++)
        {
            var msg = new byte[prefix.Length + 4];
            prefix.CopyTo(msg, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(prefix.Length), tile);
            var mac = HMACSHA256.HashData(secret, msg);
            var interim = HandlePrefix + Convert.ToBase64String(mac, 0, 12)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            Assert.NotEqual(interim, proj.Handle(tile));
        }
    }

    // ── deterministic reconnect stability ─────────────────────────────

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void Handle_IsReconnectStable_AcrossFreshInstances_SameIdentity()
    {
        var secret = Secret();
        var first = ChangshaPrivacyProjector.Create(secret, "game-1", "player-A")!;
        var again = ChangshaPrivacyProjector.Create(secret, "game-1", "player-A")!; // "reconnect"

        for (var tile = 0; tile < 108; tile++)
            Assert.Equal(first.Handle(tile), again.Handle(tile));
    }

    // ── viewer / game / tile separation ───────────────────────────────

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void Handle_Separates_ByViewer_Game_And_Tile()
    {
        var secret = Secret();
        var a = ChangshaPrivacyProjector.Create(secret, "game-1", "player-A")!;
        var b = ChangshaPrivacyProjector.Create(secret, "game-1", "player-B")!;
        var g2 = ChangshaPrivacyProjector.Create(secret, "game-2", "player-A")!;

        for (var tile = 0; tile < 108; tile++)
        {
            Assert.NotEqual(a.Handle(tile), b.Handle(tile));   // per-viewer
            Assert.NotEqual(a.Handle(tile), g2.Handle(tile));  // per-game
            if (tile < 107)
                Assert.NotEqual(a.Handle(tile), a.Handle(tile + 1)); // per-tile
        }
    }

    // ── HKDF key separation surfaces through the projector ────────────

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void Handle_ReflectsHkdfKeySeparation_NotRawSecretKeyedHmac()
    {
        var secret = Secret();
        var proj = ChangshaPrivacyProjector.Create(secret, "game-1", "player-A")!;

        // Handle keyed with the RAW secret (HKDF bypassed) must not appear, even
        // over the provider's own canonical length-prefixed message.
        var domain = Encoding.ASCII.GetBytes("mahjong-autotable/opaque-tile-handle/v1");
        var viewer = Encoding.UTF8.GetBytes("player-A");
        var game = Encoding.UTF8.GetBytes("game-1");
        for (var tile = 0; tile < 108; tile++)
        {
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
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(tid, tile);
            ms.Write(tid);

            var rawKeyed = HMACSHA256.HashData(secret, ms.ToArray());
            var rawHandle = HandlePrefix + Convert.ToBase64String(rawKeyed)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            Assert.NotEqual(rawHandle, proj.Handle(tile));
        }
    }

    // ── Create graceful-null guard (no throw on WS handshake path) ─────

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void Create_ReturnsNull_ForAbsentOrTooShortSecret_AndBuilds_ForMinimum()
    {
        Assert.Null(ChangshaPrivacyProjector.Create((byte[]?)null, "g", "p"));
        Assert.Null(ChangshaPrivacyProjector.Create(Array.Empty<byte>(), "g", "p"));
        Assert.Null(ChangshaPrivacyProjector.Create(
            new byte[OpaqueTileHandleProvider.MinimumSecretLengthBytes - 1], "g", "p"));
        Assert.Null(ChangshaPrivacyProjector.Create(Secret(), null, "p"));
        // SC-2 fail-closed (A): an empty viewer id must NOT return null (that would drop hidden
        // tiles back to real ids). Privacy is available (valid secret + gameId) → mint-or-opaque.
        var emptyViewer = ChangshaPrivacyProjector.Create(Secret(), "g", "");
        Assert.NotNull(emptyViewer);
        Assert.StartsWith(HandlePrefix, emptyViewer!.Handle(0));

        var ok = ChangshaPrivacyProjector.Create(
            new byte[OpaqueTileHandleProvider.MinimumSecretLengthBytes], "g", "p");
        Assert.NotNull(ok);
        Assert.StartsWith(HandlePrefix, ok!.Handle(0));
    }
}
