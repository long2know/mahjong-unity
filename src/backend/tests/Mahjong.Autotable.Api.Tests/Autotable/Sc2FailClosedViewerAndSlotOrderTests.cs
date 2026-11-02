using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// SC-2 hard gates (Frost exact-state handoff, BINDING):
/// <list type="bullet">
///   <item><b>(A) Fail CLOSED on viewer identity.</b> An unidentified viewer (raw/cookie-less WS,
///   spectator, not-yet-reclaimed reconnect) must never drop hidden tiles back to real ids.
///   <see cref="ChangshaPrivacyProjector.Create(byte[], string, string)"/> mints a fresh ephemeral
///   viewer scope on an empty viewer id — mint-or-opaque, never null→real — so walls stay opaque.</item>
///   <item><b>(B) Slot-canonical emission order.</b> Hidden <c>things</c> are emitted in physical
///   slot order (wall list index / hand index), NEVER sorted by real tileId. Two projections with
///   identical occupied slots but different tile identities keep the SAME hidden-entry positional
///   order; only the opaque keys differ.</item>
/// </list>
/// </summary>
public class Sc2FailClosedViewerAndSlotOrderTests
{
    private static readonly byte[] Secret =
        System.Text.Encoding.UTF8.GetBytes("bishop-sc2-test-secret-32bytes!!");

    private static string SlotOf(CollectionEntry e)
    {
        var json = JsonSerializer.Serialize(e.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("slotName").GetString() ?? "";
    }

    private static bool IsWall(CollectionEntry e) =>
        SlotOf(e).StartsWith("wall.", StringComparison.Ordinal);

    private static bool IsHidden(CollectionEntry e, int? viewerSeat)
    {
        var slot = SlotOf(e);
        if (slot.StartsWith("wall.", StringComparison.Ordinal)) return true;               // all wall tiles hidden
        if (!slot.StartsWith("hand.", StringComparison.Ordinal)) return false;             // discards/melds public
        // A foreign concealed hand is hidden; the viewer's own hand is visible.
        return !(viewerSeat is int vs && slot.EndsWith("@" + vs, StringComparison.Ordinal));
    }

    // ── (A) projector-level: an empty viewer id mints an opaque scope, never null→real ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void A_Create_WithEmptyViewer_MintsOpaqueProjector_NeverNull()
    {
        var proj = ChangshaPrivacyProjector.Create(Secret, "game-1", "");
        Assert.NotNull(proj);
        for (var t = 0; t < 108; t++)
        {
            var key = proj!.Key(t, hidden: true);
            var h = Assert.IsType<string>(key);
            Assert.StartsWith("h_", h);
            Assert.False(int.TryParse(h, out _)); // not a real 0..107 id
        }

        // Contrast: privacy genuinely disabled (no secret / no game) is still the only null path.
        Assert.Null(ChangshaPrivacyProjector.Create((byte[]?)null, "game-1", ""));
        Assert.Null(ChangshaPrivacyProjector.Create(Secret, "", ""));
    }

    // ── (A) end-to-end: a cookie-less viewer (no player id, spectator seat) still gets opaque walls ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void A_CookielessViewer_StillGetsOpaqueWalls_NotRealTileIds()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);

        // Cookie-less / unidentified spectator: empty viewer id, no seat. Projector is created from
        // the SAME empty id the endpoint would carry — the mint happens inside Create (fail-closed).
        var privacy = ChangshaPrivacyProjector.Create(Secret, state.GameId, "");
        Assert.NotNull(privacy);

        var things = ChangshaToAutotableTranslator
            .Translate(state, viewerSeat: null, viewerPlayerId: "", privacy: privacy)
            .Where(e => e.Kind == "things").ToList();

        var wall = things.Where(IsWall).ToList();
        Assert.NotEmpty(wall);
        foreach (var e in wall)
        {
            var h = Assert.IsType<string>(e.Key);   // opaque handle, never a raw int
            Assert.StartsWith("h_", h);
            Assert.False(int.TryParse(h, out _));
        }

        // No hidden entry leaked a real numeric id for this unidentified viewer.
        Assert.DoesNotContain(things.Where(e => IsHidden(e, null)), e => e.Key is int);
    }

    // ── (B) hidden emission order is slot-canonical, not tileId-sorted ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void B_HiddenThings_EmissionOrder_IsSlotCanonical_NotTileIdSorted()
    {
        const int viewer = 0;

        var baseState = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var shifted = ChangshaTestHelpers.NewGameDealtTo(seed: 7); // identical layout to baseState
        ShiftEveryTileIdentity(shifted);                           // same slots, different identities

        List<(string Slot, string Key)> HiddenSeq(ChangshaGameState s)
            => ChangshaToAutotableTranslator
                .Translate(s, viewerSeat: viewer, viewerPlayerId: "p0",
                    privacy: ChangshaPrivacyProjector.Create(Secret, s.GameId, "p0"))
                .Where(e => e.Kind == "things" && IsHidden(e, viewer))
                .Select(e => (SlotOf(e), (string)e.Key))
                .ToList();

        var a = HiddenSeq(baseState);
        var b = HiddenSeq(shifted);

        Assert.NotEmpty(a);
        Assert.Equal(a.Count, b.Count);

        // Positional order is identical — driven purely by slot, independent of tile identity.
        Assert.Equal(a.Select(x => x.Slot), b.Select(x => x.Slot));

        // Only the opaque keys differ (they track the changed identities); order did not re-sort.
        Assert.NotEqual(
            string.Join(",", a.Select(x => x.Key)),
            string.Join(",", b.Select(x => x.Key)));
        for (var i = 0; i < a.Count; i++)
            Assert.NotEqual(a[i].Key, b[i].Key);
    }

    /// <summary>
    /// Applies the bijection tileId → (tileId + 1) mod 108 to every tile in the state (all hands +
    /// wall). Because it is a global bijection over the full 108-tile set, uniqueness is preserved
    /// while every physical slot keeps its position but holds a different tile identity.
    /// </summary>
    private static void ShiftEveryTileIdentity(ChangshaGameState state)
    {
        static int Shift(int t) => (t + 1) % 108;
        foreach (var hand in state.Hands)
        {
            for (var i = 0; i < hand.ConcealedTiles.Count; i++)
                hand.ConcealedTiles[i] = Shift(hand.ConcealedTiles[i]);
            foreach (var meld in hand.Melds)
                for (var i = 0; i < meld.TileIds.Count; i++)
                    meld.TileIds[i] = Shift(meld.TileIds[i]);
        }
        for (var i = 0; i < state.Wall.Count; i++)
            state.Wall[i] = Shift(state.Wall[i]);
    }
}
