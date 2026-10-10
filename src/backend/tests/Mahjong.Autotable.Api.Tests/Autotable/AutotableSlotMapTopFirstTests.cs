using Mahjong.Autotable.Api.Autotable;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// F2 — top-first intra-stack layer mapping for
/// <see cref="AutotableSlotMap.WallOrdinalToSlot"/>. The front (even) ordinal of
/// every stack must map to layer 1 (top) and the odd ordinal to layer 0
/// (bottom), while seat/column mapping and the seat-major perimeter walk are
/// preserved. This closes only the F2 primitive; physical perimeter / dice
/// anchor (F1/G4) is out of scope here.
/// </summary>
public class AutotableSlotMapTopFirstTests
{
    // ── even ordinal => layer 1 (top), odd ordinal => layer 0 (bottom) ──

    [Fact, Trait("Category", "F2")]
    public void WallOrdinalToSlot_EvenOrdinal_MapsToLayer1_OddToLayer0_EveryStack()
    {
        for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
        {
            var (_, _, layer) = AutotableSlotMap.WallOrdinalToSlot(ordinal);
            var expectedLayer = ordinal % 2 == 0 ? 1 : 0;
            Assert.Equal(expectedLayer, layer);
        }
    }

    [Fact, Trait("Category", "F2")]
    public void WallOrdinalToSlot_PairedOrdinals_ShareSeatAndCol_TopThenBottom()
    {
        // 2n and 2n+1 within the same seat must land on the same (seat,col),
        // differing only in layer: 2n -> top(1), 2n+1 -> bottom(0).
        var capacities = new[] { 28, 28, 26, 26 };
        var baseOrdinal = 0;
        foreach (var capacity in capacities)
        {
            for (var i = 0; i < capacity; i += 2)
            {
                var top = AutotableSlotMap.WallOrdinalToSlot(baseOrdinal + i);
                var bottom = AutotableSlotMap.WallOrdinalToSlot(baseOrdinal + i + 1);

                Assert.Equal(top.Seat, bottom.Seat);
                Assert.Equal(top.Col, bottom.Col);
                Assert.Equal(1, top.Layer);
                Assert.Equal(0, bottom.Layer);
                Assert.Equal(i / 2, top.Col);
            }
            baseOrdinal += capacity;
        }
    }

    // ── seat/column mapping preserved (F1 frame math untouched) ────────

    [Fact, Trait("Category", "F2")]
    public void WallOrdinalToSlot_SeatColMapping_Preserved()
    {
        // Seat-major walk with 28/28/26/26 capacities, col = intra/2.
        var expected = new (int seat, int col)[AutotableSlotMap.TotalTiles];
        var idx = 0;
        var caps = new[] { 28, 28, 26, 26 };
        for (var seat = 0; seat < 4; seat++)
            for (var intra = 0; intra < caps[seat]; intra++)
                expected[idx++] = (seat, intra / 2);

        for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
        {
            var (seat, col, _) = AutotableSlotMap.WallOrdinalToSlot(ordinal);
            Assert.Equal(expected[ordinal].seat, seat);
            Assert.Equal(expected[ordinal].col, col);
        }
    }

    // ── the first 53 front offsets follow top -> bottom ────────────────

    [Fact, Trait("Category", "F2")]
    public void WallOrdinalToSlot_First53FrontOffsets_FollowTopToBottom()
    {
        for (var ordinal = 0; ordinal < 53; ordinal++)
        {
            var (_, _, layer) = AutotableSlotMap.WallOrdinalToSlot(ordinal);
            // Front of each stack (even) is top(1); its immediate back (odd) is bottom(0).
            Assert.Equal(ordinal % 2 == 0 ? 1 : 0, layer);
        }
    }

    // ── wraparound / modulo remains correct ───────────────────────────

    [Fact, Trait("Category", "F2")]
    public void WallOrdinalToSlot_Wraparound_Preserved()
    {
        for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
        {
            var direct = AutotableSlotMap.WallOrdinalToSlot(ordinal);
            var plusFull = AutotableSlotMap.WallOrdinalToSlot(ordinal + AutotableSlotMap.TotalTiles);
            var minusFull = AutotableSlotMap.WallOrdinalToSlot(ordinal - AutotableSlotMap.TotalTiles);

            Assert.Equal(direct, plusFull);
            Assert.Equal(direct, minusFull);
        }
    }

    [Fact, Trait("Category", "F2")]
    public void WallOrdinalToSlot_NegativeOrdinal_TopFirstPreserved()
    {
        // -1 maps to the last perimeter slot (ordinal 107): odd -> bottom(0).
        var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(-1);
        Assert.Equal(3, seat);
        Assert.Equal(12, col);
        Assert.Equal(0, layer);
    }

    // ── layer output stays within {0,1} for the whole ring ─────────────

    [Fact, Trait("Category", "F2")]
    public void WallOrdinalToSlot_Layer_AlwaysZeroOrOne()
    {
        for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
        {
            var (_, _, layer) = AutotableSlotMap.WallOrdinalToSlot(ordinal);
            Assert.InRange(layer, 0, 1);
        }
    }

    // ── PRIMITIVE-SUPPORT per-phase front depletion ───────────────────
    //
    // These exercise the production WallOrdinalToSlot against an in-memory
    // occupied-slot model of the rules engine's front-draw phases. They prove
    // the top-first PRIMITIVE keeps depletion physically coherent (a stack's
    // top/up-link is always consumed before its bottom). They are explicitly
    // NOT the final translator/targetSlots production-observed G4 golden — no
    // translator, protocol, WS, runtime, or F1 frame code is involved here.

    private static (int Seat, int Col, int Layer) Slot(int ordinal) =>
        AutotableSlotMap.WallOrdinalToSlot(ordinal);

    /// <summary>
    /// The rules engine's front-draw phase counts: batch-of-4 pickup rounds
    /// (0,4,…,44), the single-tile pickups (48,49,50,51,52) including the
    /// mandatory dealer-14th states 49 and 51. Enumerated against even break
    /// bases (0, a seat-boundary base, and a wrap-inducing base) so the
    /// top-first arc always starts on a stack top.
    /// </summary>
    private static readonly int[] FrontDrawnStates =
        { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 49, 50, 51, 52 };

    [Theory, Trait("Category", "F2")]
    [InlineData(0)]     // dealer origin
    [InlineData(28)]    // even seat-boundary base (seat 1 origin)
    [InlineData(106)]   // even base that forces perimeter wraparound
    public void WallOrdinalToSlot_PerPhaseFrontDepletion_TopFirst_PrimitiveSupport(int breakBase)
    {
        foreach (var n in FrontDrawnStates)
        {
            // Full wall occupied, then remove the front-drawn prefix [base, base+n).
            var occupied = new HashSet<(int, int, int)>();
            for (var o = 0; o < AutotableSlotMap.TotalTiles; o++) occupied.Add(Slot(o));
            Assert.Equal(AutotableSlotMap.TotalTiles, occupied.Count);

            var removed = 0;
            for (var i = 0; i < n; i++)
            {
                Assert.True(occupied.Remove(Slot(breakBase + i)),
                    $"double-draw at base={breakBase}, i={i}");
                removed++;
            }
            Assert.Equal(n, removed); // remove-phase count matches frontDrawn

            // The current front tile about to be drawn.
            var front = Slot(breakBase + n);
            if (front.Layer == 0)
            {
                // Bottom tile: top-first requires its up-link (the stack top) already gone.
                Assert.DoesNotContain((front.Seat, front.Col, 1), occupied);
            }
            else
            {
                // Top tile: it heads an intact stack and is still present.
                Assert.Contains(front, occupied);
            }

            // Global top-first invariant: for every stack, an occupied top implies
            // an occupied bottom (the top is always consumed before the bottom), so
            // no stack is ever left "floating" (bottom drawn while top remains).
            for (var seat = 0; seat < 4; seat++)
            {
                for (var col = 0; col < AutotableSlotMap.WallStackCount(seat); col++)
                {
                    var topPresent = occupied.Contains((seat, col, 1));
                    var bottomPresent = occupied.Contains((seat, col, 0));
                    if (topPresent)
                        Assert.True(bottomPresent,
                            $"floating stack seat={seat} col={col}: top present but bottom drawn");
                }
            }
        }
    }

    [Theory, Trait("Category", "F2")]
    [InlineData(49)]
    [InlineData(51)]
    public void WallOrdinalToSlot_MandatorySingleTilePickup_FrontIsBottom_UpLinkDrawnFirst_PrimitiveSupport(int frontDrawn)
    {
        // Mandatory dealer-14th single-tile pickups land on a stack BOTTOM whose
        // up-link (top) was the immediately preceding draw — the defining
        // top-first ordering. PRIMITIVE support, not the G4 translator golden.
        var front = Slot(frontDrawn);
        Assert.Equal(0, front.Layer); // odd ordinal -> bottom

        var upLink = Slot(frontDrawn - 1);
        Assert.Equal(front.Seat, upLink.Seat);
        Assert.Equal(front.Col, upLink.Col);
        Assert.Equal(1, upLink.Layer); // the up-link is the stack top
    }
}
