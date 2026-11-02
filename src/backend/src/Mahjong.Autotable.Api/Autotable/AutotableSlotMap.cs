namespace Mahjong.Autotable.Api.Autotable;

/// <summary>
/// Translates Changsha tile positions to upstream pwmarcz/autotable slot names.
/// Upstream's renderer is driven entirely by these slot strings — a tile at
/// <c>hand.0@2</c> renders at seat 2's hand slot index 0; move it to
/// <c>discard.0.0@2</c> and the renderer animates the move.
///
/// Slot reference (see upstream <c>src/setup-slots.ts</c>):
/// <list type="bullet">
///   <item><c>hand.{i}@{s}</c> — 14 slots per seat (i ∈ [0,13])</item>
///   <item><c>wall.{c}.{l}@{s}</c> — 19 cols × 2 layers per seat (152 total upstream slots)</item>
///   <item><c>discard.{r}.{c}@{s}</c> — 3 rows × 6 cols radial trays per seat</item>
///   <item><c>meld.{m}.{t}@{s}</c> — 4 melds × 4 tiles per seat</item>
/// </list>
///
/// <para><b>Changsha wall split (Phase 5a Default #6 — locked):</b></para>
/// <para>108 tiles split 14/14/13/13 across the four walls.</para>
/// <list type="bullet">
///   <item>Seats 0, 1 → 14 stacks × 2 tiers = 28 tiles each</item>
///   <item>Seats 2, 3 → 13 stacks × 2 tiers = 26 tiles each</item>
///   <item>Total: 28 + 28 + 26 + 26 = <b>108</b></item>
/// </list>
///
/// <para>Within a seat, slots are populated col-major, layer-minor (col 0 layer 0,
/// col 0 layer 1, col 1 layer 0, …) so visual "stacks" match the canonical wall.</para>
///
/// <para><b>Upstream typeIndex:</b> <c>changshaTileId / 4</c> per spike §5.1.
/// Changsha uses tiles 0..107 (3 suits × 9 ranks × 4 copies); upstream's atlas
/// row 0 col 0 is 1-wan; suits man/pin/sou occupy typeIndex 0..26. Winds /
/// dragons / red-fives at 27..36 are unused in Changsha v1.</para>
/// </summary>
public static class AutotableSlotMap
{
    /// <summary>Total tiles in the Changsha deck.</summary>
    public const int TotalTiles = 108;

    /// <summary>Upstream stacks per seat for the canonical 14/14/13/13 Changsha wall.</summary>
    /// <param name="seat">0..3</param>
    /// <returns>14 for seats 0,1; 13 for seats 2,3.</returns>
    public static int WallStackCount(int seat)
    {
        if (seat is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "seat must be in [0,3].");
        return seat is 0 or 1 ? 14 : 13;
    }

    /// <summary>Total wall slot capacity for a seat (2 tiers per stack).</summary>
    public static int WallTileCapacity(int seat) => WallStackCount(seat) * 2;

    /// <summary>Slot name for a wall tile: <c>wall.{col}.{layer}@{seat}</c>.</summary>
    public static string WallSlot(int seat, int col, int layer)
    {
        if (seat is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "seat must be in [0,3].");
        if (col < 0 || col >= WallStackCount(seat))
            throw new ArgumentOutOfRangeException(nameof(col), col, $"col must be in [0,{WallStackCount(seat) - 1}] for seat {seat}.");
        if (layer is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "layer must be 0 or 1.");
        return $"wall.{col}.{layer}@{seat}";
    }

    /// <summary>Slot name for a concealed-hand tile: <c>hand.{handIdx}@{seat}</c>.</summary>
    public static string HandSlot(int seat, int handIdx)
    {
        if (seat is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "seat must be in [0,3].");
        if (handIdx is < 0 or > 13)
            throw new ArgumentOutOfRangeException(nameof(handIdx), handIdx, "handIdx must be in [0,13].");
        return $"hand.{handIdx}@{seat}";
    }

    /// <summary>Slot name for a per-seat radial discard tile: <c>discard.{row}.{col}@{seat}</c>.</summary>
    public static string DiscardSlot(int seat, int row, int col)
    {
        if (seat is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "seat must be in [0,3].");
        if (row is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(row), row, "row must be in [0,2].");
        if (col is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(col), col, "col must be in [0,5].");
        return $"discard.{row}.{col}@{seat}";
    }

    /// <summary>Slot name for a meld tile: <c>meld.{meldIdx}.{tileIdx}@{seat}</c>.</summary>
    public static string MeldSlot(int seat, int meldIdx, int tileIdx)
    {
        if (seat is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "seat must be in [0,3].");
        if (meldIdx is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(meldIdx), meldIdx, "meldIdx must be in [0,3].");
        if (tileIdx is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(tileIdx), tileIdx, "tileIdx must be in [0,3].");
        return $"meld.{meldIdx}.{tileIdx}@{seat}";
    }

    /// <summary>
    /// Added-Kong-only upper slot, over the middle tile of an existing Pung.
    /// Flat meld slots 0..3 retain their existing meanings for every other kind.
    /// </summary>
    public static string AddedKongSlot(int seat, int meldIdx)
    {
        _ = MeldSlot(seat, meldIdx, 0);
        return $"meld.{meldIdx}.4@{seat}";
    }

    /// <summary>
    /// Maps a Changsha tile id (0..107) to upstream's atlas <c>typeIndex</c>.
    /// Per spike §5.1: Changsha and upstream agree on suit / rank ordering.
    /// </summary>
    public static int UpstreamTypeIndex(int changshaTileId)
    {
        if (changshaTileId < 0 || changshaTileId >= TotalTiles)
            throw new ArgumentOutOfRangeException(nameof(changshaTileId), changshaTileId, $"tileId must be in [0,{TotalTiles - 1}].");
        return changshaTileId / 4;
    }

    /// <summary>
    /// Enumerates every (seat, col, layer) wall slot in <b>balanced placement order</b>:
    /// columns ascending, all four seats per column, both layers per (seat, col)
    /// before advancing the column. Total = 108 tuples for the 14/14/13/13 split
    /// (seats 0,1 contribute through col 13; seats 2,3 only through col 12).
    ///
    /// <para><b>Why column-major-across-seats:</b> the translator packs the
    /// authoritative <c>state.Wall</c> (108 pre-deal, 55 immediately after deal)
    /// into the first N slots yielded here. A seat-major order packs the entire
    /// post-deal remainder into seats 0 and 1, leaving seats 2 and 3 with
    /// physically empty walls — visually catastrophic (Stephen 2026-05-29
    /// "dealing seems very whacky"). Column-major-across-seats ships ~13-14
    /// tiles to every seat after deal, preserving the canonical 2-high stack
    /// silhouette on all four sides of the table without requiring the
    /// translator to thread break-point physics through the wire format.</para>
    /// </summary>
    public static IEnumerable<(int Seat, int Col, int Layer)> EnumerateWallSlotsInOrder()
    {
        // Largest seat-wall depth is 14 (seats 0,1); seats 2,3 cap at 13.
        // Stepping by column outer + seat inner gives the balanced fill.
        const int MaxStacks = 14;
        for (var col = 0; col < MaxStacks; col++)
        {
            for (var seat = 0; seat < 4; seat++)
            {
                if (col >= WallStackCount(seat)) continue;
                for (var layer = 0; layer < 2; layer++)
                {
                    yield return (seat, col, layer);
                }
            }
        }
    }

    /// <summary>
    /// Maps a <b>perimeter ordinal</b> in [0, 108) to a stable physical wall
    /// slot, walking <b>seat-major</b> (seat 0's whole wall, then seat 1, 2, 3),
    /// columns ascending. Consecutive ordinals are physically adjacent, so a
    /// contiguous draw-order run occupies a contiguous physical arc — the
    /// invariant that makes the live wall deplete from the break point around
    /// the perimeter instead of scattering evenly across all four seats
    /// (issue #152).
    ///
    /// <para><b>F2 top-first intra-stack layering:</b> within a stack the
    /// <b>front (even) ordinal</b> — the tile drawn/broken first — maps to
    /// <b>layer 1 (top)</b> and the odd ordinal to <b>layer 0 (bottom)</b>.
    /// Physical Changsha play breaks the top tile of a stack before the one
    /// beneath it, so ordinal <c>2n → layer 1</c> and <c>2n+1 → layer 0</c>
    /// keeps the render's depletion order matching the physical draw order.
    /// Seat and column mapping are unchanged.</para>
    ///
    /// <para>Per-seat capacity is <c>WallStackCount(seat) * 2</c>
    /// (28/28/26/26), so the returned <c>col</c> is always within
    /// <c>[0, WallStackCount(seat) - 1]</c> — never an over-limit emit.</para>
    /// </summary>
    public static (int Seat, int Col, int Layer) WallOrdinalToSlot(int ordinal)
    {
        var o = ((ordinal % TotalTiles) + TotalTiles) % TotalTiles;
        for (var seat = 0; seat < 4; seat++)
        {
            var capacity = WallTileCapacity(seat);
            if (o < capacity) return (seat, o / 2, 1 - (o % 2));
            o -= capacity;
        }
        // Unreachable: o is reduced modulo TotalTiles and the seat capacities sum to TotalTiles.
        throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "ordinal must map within the 108-slot perimeter.");
    }

    /// <summary>
    /// Render-ring ordinal of the START (col 0, layer 0) of a seat's physical wall —
    /// the sum of the render wall capacities of every lower-indexed seat. Because the
    /// four physical walls are laid out seat-major (seat 0's whole wall, then 1, 2, 3)
    /// this is the physical origin of that seat's wall.
    ///
    /// <para><b>Why this is the correct break anchor (issue: stuck-turn wall mapping /
    /// C-3):</b> the rules engine
    /// (<see cref="Mahjong.Autotable.Api.Changsha.BreakPointService"/>) models the wall
    /// as one flat list whose index 0 is the first tile of the <b>dealer's</b> wall,
    /// running counter-clockwise; <see cref="Mahjong.Autotable.Api.Changsha.BreakPointResult.TileIndex"/>
    /// is that authoritative flat index of the break tile. Mapping an engine flat index
    /// <c>k</c> to the render ring as <c>WallDealerOriginOrdinal(dealer) + k</c>
    /// (mod <see cref="TotalTiles"/>) reproduces the true draw order as one contiguous
    /// physical arc for <b>every</b> dealer seat 0–3.</para>
    ///
    /// <para>The superseded <c>WallBreakOrdinal(wallSeatIndex, stackIndex)</c> re-derived
    /// the anchor from the render's absolute 14/14/13/13 cumulative sizes plus the engine's
    /// dealer-relative 14/13/14/13 stack index and clamped the result. Those two frames only
    /// agree when the walls between the dealer and the break wall happen to have equal sizes,
    /// so the anchor drifted by one stack for most dealer-2 rolls (and some rolls at every
    /// other dealer) — a real cross-dealer defect. Anchoring on the authoritative flat
    /// <c>TileIndex</c> removes the frame confusion and the dead clamp entirely.</para>
    /// </summary>
    public static int WallDealerOriginOrdinal(int dealerSeatIndex)
    {
        var seat = ((dealerSeatIndex % 4) + 4) % 4;
        var start = 0;
        for (var s = 0; s < seat; s++) start += WallTileCapacity(s);
        return start;
    }
}
