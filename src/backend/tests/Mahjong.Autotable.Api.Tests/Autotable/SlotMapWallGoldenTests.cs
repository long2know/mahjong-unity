using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Golden invariants for the physical wall slot mapping (<see cref="AutotableSlotMap"/>) and
/// its break anchor (composed with <see cref="BreakPointService"/>).
///
/// <para><b>Declared canonical wall-size frame (F1):</b> seat-absolute
/// <c>{ seats 0,1 → 14 stacks (28 tiles); seats 2,3 → 13 stacks (26 tiles) }</c>. This is
/// fixed by the frontend bundle geometry (seats 2/3 only define 13 wall columns) and is the
/// single frame shared by the render ring, the dealer origin, and the engine break point.</para>
///
/// <para><b>Top-first convention (F2):</b> each 2-high stack is addressed top-before-bottom —
/// the lower flat index of a stack is layer 1 (exposed top), the higher is layer 0 (occluded
/// bottom). Even seat-local offset → layer 1.</para>
///
/// <para>These assertions are provable purely from the wall physics (top-first draw,
/// count-dice-sum-stacks-from-the-right break, 108-slot bijection). They intentionally do NOT
/// encode a hand-authored per-case anchor table — the authoritative dice-anchor oracle is
/// rules-owned and pinned in <see cref="ExpectedAnchorOracle"/> (test #4, all 44 rows verbatim).</para>
/// </summary>
public class SlotMapWallGoldenTests
{
    private static readonly BreakPointService Bp = new();

    /// <summary>All 44 reachable (dealer 0..3 × diceSum 2..12) roll outcomes.</summary>
    public static IEnumerable<object[]> DealerDiceCases()
    {
        for (var dealer = 0; dealer < 4; dealer++)
            for (var sum = 2; sum <= 12; sum++)
                yield return new object[] { dealer, sum };
    }

    /// <summary>Render-ring ordinal of the break frontier for a (dealer, diceSum): the exact
    /// composition the translator performs — <c>WallDealerOriginOrdinal(dealer) + TileIndex</c>.</summary>
    private static int BreakOrdinal(int dealer, int diceSum)
        => AutotableSlotMap.WallDealerOriginOrdinal(dealer) + Bp.ComputeBreakPoint(diceSum, dealer).TileIndex;

    // ── #1 Top-first / reachability ─────────────────────────────────────────────

    /// <summary>The break ordinal of a full wall must land on the exposed TOP (layer 1) of the
    /// frontier stack for every dealer × dice sum — the tile a player can actually hover/draw.</summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "F1-F2")]
    [MemberData(nameof(DealerDiceCases))]
    public void BreakFrontier_OfFullWall_IsTopLayer1(int dealer, int diceSum)
    {
        var (_, _, layer) = AutotableSlotMap.WallOrdinalToSlot(BreakOrdinal(dealer, diceSum));
        Assert.Equal(1, layer);
    }

    /// <summary>More generally: within every physical stack the FIRST (lower) ordinal maps to
    /// layer 1 (top) and the SECOND (higher) to layer 0 (bottom) — depletion is top-before-bottom.</summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "F2")]
    public void EveryStack_FirstOrdinalIsTop_SecondIsBottom()
    {
        // (seat, col) → the two ordinals that resolve into it, in ordinal order.
        var byStack = new Dictionary<(int Seat, int Col), List<(int Ordinal, int Layer)>>();
        for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
        {
            var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(ordinal);
            if (!byStack.TryGetValue((seat, col), out var list))
                byStack[(seat, col)] = list = new List<(int, int)>();
            list.Add((ordinal, layer));
        }

        foreach (var ((seat, col), tiles) in byStack)
        {
            Assert.Equal(2, tiles.Count);
            var ordered = tiles.OrderBy(t => t.Ordinal).ToList();
            Assert.Equal(1, ordered[0].Layer); // first (lower ordinal) = exposed top
            Assert.Equal(0, ordered[1].Layer); // second (higher ordinal) = occluded bottom
            Assert.Equal(ordered[0].Ordinal + 1, ordered[1].Ordinal); // top immediately precedes bottom
        }
    }

    /// <summary>
    /// <b>Reviewer gate — top-first reachability across the ENTIRE depletion.</b> Deplete the wall
    /// FRONT-FIRST from the real break anchor — <c>frontOrdinal(dealer, diceSum, k) =
    /// BreakOrdinal(dealer, diceSum) + k</c> (taken mod 108 exactly as the translator/production
    /// composes it) — and assert that at EVERY cumulative-drawn position <c>k ∈ [0,108)</c> the
    /// front tile is physically reachable, defined precisely as its <b>physical up-link slot being
    /// EMPTY in the current depletion state</b>:
    /// <list type="bullet">
    ///   <item>the front is a layer-1 TOP — nothing can sit physically above it; OR</item>
    ///   <item>the front is a layer-0 BOTTOM whose same-(seat,col) layer-1 TOP has ALREADY been
    ///   drawn — proven by comparing draw positions in this depletion: the top's cumulative
    ///   draw-index &lt; the bottom's (== <c>k</c>).</item>
    /// </list>
    /// This is intentionally NOT a <c>layer == 1</c> shortcut: a layer-0 bottom front is VALID once
    /// its top is gone, and must not be rejected. Iterating all 108 fronts subsumes the manual-deal
    /// ceremony phases (BreakPointMarked → PickupRound1..3 → SingleTilePickup → DealerExtra),
    /// including the single-tile parity-flip points around k≈48..52. Under the old bottom-first
    /// <c>o % 2</c> mapping the k=0 break tile (an even front) would resolve to an occluded BOTTOM
    /// whose TOP is only drawn one step LATER (k=1), so its up-link is still occupied and this test
    /// fails at k=0 — which is exactly the gate the reviewer requires.
    /// </summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "F2")]
    [MemberData(nameof(DealerDiceCases))]
    public void Wall_FrontIsAlwaysReachable_TopFirst_EveryDealerDiceSumAndDepletionPosition(int dealer, int diceSum)
    {
        var start = BreakOrdinal(dealer, diceSum);

        // Static slot → ordinal reverse index of the top-first bijection (break-independent).
        // Locates the physical TOP that sits directly above any BOTTOM front, computed from the
        // production mapping itself rather than re-deriving a (potentially divergent) formula.
        var slotToOrdinal = new Dictionary<(int Seat, int Col, int Layer), int>();
        for (var o = 0; o < AutotableSlotMap.TotalTiles; o++)
            slotToOrdinal[AutotableSlotMap.WallOrdinalToSlot(o)] = o;

        // Cumulative-drawn position of a wall ordinal in THIS depletion (how many fronts are pulled
        // before it becomes the front). Wrap handled the same way WallOrdinalToSlot does.
        int DrawIndexOf(int ordinal) =>
            (((ordinal - start) % AutotableSlotMap.TotalTiles) + AutotableSlotMap.TotalTiles) % AutotableSlotMap.TotalTiles;

        // k = cumulative tiles drawn; walk EVERY front position as the wall fully depletes.
        for (var k = 0; k < AutotableSlotMap.TotalTiles; k++)
        {
            var frontOrdinal = start + k; // WallOrdinalToSlot reduces mod 108 internally.
            var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(frontOrdinal);

            // Reachable ⇔ the physical up-link (slot directly above the front) is EMPTY right now.
            bool upLinkEmpty;
            if (layer == 1)
            {
                upLinkEmpty = true; // top tier: no tile can be physically above it.
            }
            else
            {
                // bottom tier: its up-link is the same-(seat,col) layer-1 TOP; it must already be
                // gone. With top-first mapping the top is drawn strictly before this bottom.
                var topOrdinal = slotToOrdinal[(seat, col, 1)];
                upLinkEmpty = DrawIndexOf(topOrdinal) < k; // k == DrawIndexOf(frontOrdinal)
            }

            Assert.True(
                upLinkEmpty,
                $"dealer {dealer}, diceSum {diceSum}, drawn k={k}: front ordinal {frontOrdinal} → " +
                $"occluded BOTTOM slot ({seat},{col},{layer}) whose layer-1 TOP is still in place — " +
                "not reachable. A layer-0 front is only valid once its top is consumed; the old " +
                "`o % 2` (bottom-first) mapping makes the even break front an occluded bottom here.");
        }
    }

    // ── #1b Ceremony-structured per-phase reachability (rules-owned pickup axis) ─

    /// <summary>
    /// The 17 manual-deal ceremony pickup designations, each labelled with its ceremony phase and
    /// the <b>frame-independent</b> cumulative <c>frontDrawn</c> — the number of tiles already taken
    /// from the wall front at the START of that pickup. Fixed purely by the deal structure (see
    /// <see cref="Mahjong.Autotable.Api.Changsha.Dealing.ChangshaDealingCeremony"/>): 3 batch-of-4
    /// rounds (12 pickups, step 4 → 0..44), then 4 single-tile seat pickups (step 1 → 48..51), then
    /// 1 dealer-extra single tile (→ 52). It does not depend on the (dealer, diceSum) frame — only
    /// the break anchor those choose does.
    ///
    /// <para>The ODD single-tile values <c>F=49</c> and <c>F=51</c> are the load-bearing cases:
    /// there <c>Wall[F]</c> is a layer-0 BOTTOM, reachable ONLY because ordinal <c>F-1</c> (its
    /// stack-top) was removed on the immediately-preceding single-tile pickup. A regression from the
    /// top-first mapping to bottom-first <c>o % 2</c> fails exactly at these (and at F=0).</para>
    /// </summary>
    private static readonly (string Phase, int FrontDrawn)[] CeremonyPickups = BuildCeremonyPickups();

    /// <summary>
    /// Derives the 17 ceremony pickup designations (phase label + cumulative <c>frontDrawn</c>)
    /// directly from the rules-owned
    /// <see cref="Mahjong.Autotable.Api.Changsha.Dealing.ChangshaDealingCeremony"/> constants —
    /// <c>FinalRoundIndex</c> batch-of-<c>BatchPickupSize</c> rounds over <c>SeatCount</c> seats,
    /// then <c>SeatCount</c> single-tile seat pickups, then the dealer-extra — so this axis cannot
    /// drift from the production ceremony structure. Cumulative front steps: 0,4,…,44 (batch),
    /// then 48,49,50,51 (single), then 52 (dealer extra). F=49/51 are the load-bearing ODD bottoms.
    /// </summary>
    private static (string Phase, int FrontDrawn)[] BuildCeremonyPickups()
    {
        const int seats = Mahjong.Autotable.Api.Changsha.Dealing.ChangshaDealingCeremony.SeatCount;
        const int batch = Mahjong.Autotable.Api.Changsha.Dealing.ChangshaDealingCeremony.BatchPickupSize;
        const int single = Mahjong.Autotable.Api.Changsha.Dealing.ChangshaDealingCeremony.SinglePickupSize;
        const int batchRounds = Mahjong.Autotable.Api.Changsha.Dealing.ChangshaDealingCeremony.FinalRoundIndex;

        var pickups = new List<(string Phase, int FrontDrawn)>();
        var frontDrawn = 0;
        // PickupRounds 1..FinalRoundIndex — batchRounds × SeatCount, batch-of-BatchPickupSize each.
        for (var round = 1; round <= batchRounds; round++)
            for (var seat = 0; seat < seats; seat++)
            {
                pickups.Add(($"PickupRound{round} (batch{batch})", frontDrawn));
                frontDrawn += batch;
            }
        // SingleTilePickup — SeatCount single-tile seat pickups. Even F = fresh stack-tops; the ODD
        // F (49/51) are layer-0 BOTTOMS exposed only by the PRIOR pickup's top-draw.
        for (var seat = 0; seat < seats; seat++)
        {
            var parity = frontDrawn % 2 == 0 ? "even" : "ODD";
            pickups.Add(($"SingleTilePickup {parity} F={frontDrawn}", frontDrawn));
            frontDrawn += single;
        }
        // DealerExtra — the dealer's 14th tile (a fresh even top).
        pickups.Add(("DealerExtra", frontDrawn));
        return pickups.ToArray();
    }

    /// <summary>
    /// <b>Rules-owned per-phase reachability oracle (Vasquez axis).</b> Instead of sweeping all 108
    /// depletion positions, this walks the manual-deal ceremony's <see cref="CeremonyPickups"/>
    /// sequence and asserts the take's FIRST removed tile is physically reachable at each of the 17
    /// pickup designations (PickupRound1..3 → SingleTilePickup → DealerExtra), for every dealer ×
    /// dice sum (44 cases → 748 assertions, all expecting EXPOSED).
    ///
    /// <para>For each pickup, <c>targetSlots[0] = slot(Wall[frontDrawn]) =
    /// WallOrdinalToSlot(BreakOrdinal(dealer, diceSum) + frontDrawn)</c> — the first tile the
    /// count-based take pops. Reachability reuses the EXACT up-link-empty predicate of
    /// <see cref="Wall_FrontIsAlwaysReachable_TopFirst_EveryDealerDiceSumAndDepletionPosition"/>:
    /// the trigger is exposed iff it is a layer-1 TOP, OR a layer-0 BOTTOM whose same-(seat,col)
    /// layer-1 TOP has already been drawn in this depletion (its cumulative draw-index &lt;
    /// <c>frontDrawn</c>). This is deliberately NOT a <c>layer == 1</c> shortcut — a layer-0 bottom
    /// is valid once its top is gone (that is precisely the F=49 / F=51 single-tile situation).</para>
    ///
    /// <para><b>Why the ∀-phase axis is required:</b> an initial-only (frontDrawn=0) assertion would
    /// trivially pass and hide a regression at the ODD single-tile pickups F=49 / F=51, where
    /// <c>Wall[F]</c> is a bottom exposed solely by the preceding pickup's top-draw. Sweeping every
    /// phase is what makes those parity-flip points observable; a revert to bottom-first
    /// <c>o % 2</c> fails at F=0 and at F=49/51. (The frame-dependent F1 dice-anchor sign-off is
    /// the separate, rules-owned <see cref="ExpectedAnchorOracle"/> golden (test #4, 44/44) — not
    /// fabricated here.)</para>
    /// </summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "F2")]
    [MemberData(nameof(DealerDiceCases))]
    public void Wall_CeremonyPickups_TargetSlot0_AlwaysReachable_VasquezPerPhaseOracle(int dealer, int diceSum)
    {
        var start = BreakOrdinal(dealer, diceSum);

        // Static slot → ordinal reverse index of the top-first bijection (break-independent) — locates
        // the physical TOP directly above any BOTTOM trigger from the production mapping itself,
        // rather than re-deriving a (potentially divergent) formula. Same construction as the sweep.
        var slotToOrdinal = new Dictionary<(int Seat, int Col, int Layer), int>();
        for (var o = 0; o < AutotableSlotMap.TotalTiles; o++)
            slotToOrdinal[AutotableSlotMap.WallOrdinalToSlot(o)] = o;

        // Cumulative-drawn position of a wall ordinal in THIS depletion — identical predicate helper
        // to Wall_FrontIsAlwaysReachable_TopFirst_… (how many fronts are pulled before it is front).
        int DrawIndexOf(int ordinal) =>
            (((ordinal - start) % AutotableSlotMap.TotalTiles) + AutotableSlotMap.TotalTiles) % AutotableSlotMap.TotalTiles;

        foreach (var (phase, frontDrawn) in CeremonyPickups)
        {
            // targetSlots[0] = slot(Wall[frontDrawn]): the FIRST tile the count-based take removes.
            var frontOrdinal = start + frontDrawn; // WallOrdinalToSlot reduces mod 108 internally.
            var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(frontOrdinal);

            // Trigger == first-taken invariant (trivially true by construction, asserted to document
            // it): the reachability below concerns the SAME slot the take pops first — the mapping of
            // Wall[frontDrawn] — re-derived through the public BreakOrdinal helper as a cross-check.
            Assert.Equal(
                AutotableSlotMap.WallOrdinalToSlot(BreakOrdinal(dealer, diceSum) + frontDrawn),
                (seat, col, layer));

            // Reachable ⇔ the physical up-link (slot directly above the trigger) is EMPTY right now:
            // layer-1 TOP (nothing above it), OR layer-0 BOTTOM whose layer-1 TOP is already drawn.
            bool upLinkEmpty;
            if (layer == 1)
            {
                upLinkEmpty = true; // top tier: no tile can be physically above it.
            }
            else
            {
                // bottom tier: its up-link is the same-(seat,col) layer-1 TOP; it must already be
                // gone. Top-first mapping draws that top strictly before this bottom (here, on the
                // immediately-preceding single-tile pickup for the odd F=49 / F=51 designations).
                var topOrdinal = slotToOrdinal[(seat, col, 1)];
                upLinkEmpty = DrawIndexOf(topOrdinal) < frontDrawn; // frontDrawn == DrawIndexOf(frontOrdinal)
            }

            Assert.True(
                upLinkEmpty,
                $"dealer {dealer}, diceSum {diceSum}, phase {phase}, frontDrawn={frontDrawn}: " +
                $"trigger ordinal {frontOrdinal} → occluded BOTTOM slot ({seat},{col},{layer}) whose " +
                "layer-1 TOP is still in place — the take's first tile is not reachable. The odd " +
                "single-tile pickups F=49/F=51 are exposed ONLY because their stack-top was drawn on " +
                "the prior pickup; the old bottom-first `o % 2` mapping fails here and at F=0.");
        }
    }

    // ── #2 Single-tile trigger == take consistency ──────────────────────────────

    /// <summary>Simulate consecutive count=1 draws walking the frontier ordinal forward. The
    /// front tile must always be reachable: layer 1 while its stack is full (even seat-local
    /// offset) and layer 0 only after its layer-1 partner (the immediately-preceding ordinal)
    /// has been drawn. We never designate a layer-0 tile while its layer-1 partner is still ahead.</summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "F2")]
    [MemberData(nameof(DealerDiceCases))]
    public void ConsecutiveSingleDraws_FrontTileIsAlwaysReachable(int dealer, int diceSum)
    {
        var start = BreakOrdinal(dealer, diceSum);
        // Walk the whole ring once from the break — every single-draw frontier position.
        for (var step = 0; step < AutotableSlotMap.TotalTiles; step++)
        {
            var ordinal = start + step;
            var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(ordinal);

            // Seat-local offset parity is the physical "is this the top of a full stack?" bit.
            var seatLocal = SeatLocalOffset(ordinal);
            if (seatLocal % 2 == 0)
            {
                // Start of a stack → its top (layer 1) is the reachable tile.
                Assert.Equal(1, layer);
            }
            else
            {
                // Second tile of a stack → layer 0, reachable only because its layer-1 partner
                // (the previous ordinal, same seat+col) was already consumed.
                Assert.Equal(0, layer);
                var (pSeat, pCol, pLayer) = AutotableSlotMap.WallOrdinalToSlot(ordinal - 1);
                Assert.Equal((seat, col), (pSeat, pCol));
                Assert.Equal(1, pLayer);
            }
        }
    }

    // ── #3 Contiguity / bijection ───────────────────────────────────────────────

    /// <summary>The 108 ordinals map bijectively onto the 108 physical slots: no duplicate, no
    /// gap, never throwing, every (seat, col) within its wall carrying both layers, and every
    /// emitted col within the seat's stack count.</summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "F1-F2")]
    public void Ordinals_MapBijectivelyOnto108PhysicalSlots()
    {
        var seen = new HashSet<(int Seat, int Col, int Layer)>();
        for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
        {
            var slot = AutotableSlotMap.WallOrdinalToSlot(ordinal); // must never throw in [0,108)
            Assert.InRange(slot.Seat, 0, 3);
            Assert.InRange(slot.Col, 0, AutotableSlotMap.WallStackCount(slot.Seat) - 1);
            Assert.InRange(slot.Layer, 0, 1);
            Assert.True(seen.Add(slot), $"ordinal {ordinal} collided on physical slot {slot}.");
        }

        Assert.Equal(AutotableSlotMap.TotalTiles, seen.Count);

        // Coverage: every physical (seat, col, layer) is hit exactly once.
        for (var seat = 0; seat < 4; seat++)
            for (var col = 0; col < AutotableSlotMap.WallStackCount(seat); col++)
            {
                Assert.Contains((seat, col, 0), seen);
                Assert.Contains((seat, col, 1), seen);
            }
    }

    // ── #4 One-frame consistency (F1) ───────────────────────────────────────────

    /// <summary>
    /// The break anchor derived from the single canonical frame is internally consistent for all
    /// 44 dealer × dice-sum cases: it never throws, lands on the frontier TOP (layer 1), and the
    /// render placement agrees with the engine's own break report — resolved
    /// <c>(seat, col) == (BreakPoint.WallIndex, BreakPoint.StackIndex)</c>. The resolved column
    /// also satisfies the §2 physics of counting <c>diceSum</c> stacks from the RIGHT end of the
    /// broken wall (<c>col == WallStackCount(seat) - diceSum</c>). These hold ONLY when the engine
    /// and the render share the one seat-absolute frame (they diverged under the old mixed frame).
    /// </summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "F1")]
    [MemberData(nameof(DealerDiceCases))]
    public void BreakAnchor_IsInternallyConsistent_AcrossOneFrame(int dealer, int diceSum)
    {
        var report = Bp.ComputeBreakPoint(diceSum, dealer);

        var ordinal = AutotableSlotMap.WallDealerOriginOrdinal(dealer) + report.TileIndex;
        var ex = Record.Exception(() => AutotableSlotMap.WallOrdinalToSlot(ordinal));
        Assert.Null(ex); // never throws — the composition stays on the 108-slot ring

        var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(ordinal);

        Assert.Equal(1, layer);                              // frontier top (reachable)
        Assert.Equal(report.WallIndex, seat);                // render seat == engine's broken wall
        Assert.Equal(report.StackIndex, col);                // render col  == engine's break stack
        Assert.Equal(AutotableSlotMap.WallStackCount(seat) - diceSum, col); // count from right end
    }

    /// <summary>
    /// Rules-owned dice-anchor oracle (Vasquez F1 SIGN-OFF, verbatim from
    /// <c>.squad/decisions/inbox/vasquez-f1-anchor-oracle-and-f2-signoff.md</c>). Keys are
    /// <c>(dealer, diceSum)</c>; values are the expected physical <c>(seat, col, layer)</c> of the
    /// break frontier under the declared seat-absolute <c>{0,1→14, 2,3→13}</c> frame
    /// (<c>B=(dealer+diceSum-1)%4</c>, <c>col=Stacks[B]-diceSum</c>, layer 1 = top). Independently
    /// rules-derived (NOT read back from the implementation), so
    /// <see cref="BreakAnchor_MatchesRulesOwnedOracle_WhenProvided"/> is a non-vacuous 44-case golden.
    /// </summary>
    public static readonly IReadOnlyDictionary<(int Dealer, int DiceSum), (int Seat, int Col, int Layer)>
        ExpectedAnchorOracle = new Dictionary<(int Dealer, int DiceSum), (int Seat, int Col, int Layer)>
        {
            [(0,2)]=(1,12,1), [(0,3)]=(2,10,1), [(0,4)]=(3,9,1), [(0,5)]=(0,9,1), [(0,6)]=(1,8,1), [(0,7)]=(2,6,1), [(0,8)]=(3,5,1), [(0,9)]=(0,5,1), [(0,10)]=(1,4,1), [(0,11)]=(2,2,1), [(0,12)]=(3,1,1),
            [(1,2)]=(2,11,1), [(1,3)]=(3,10,1), [(1,4)]=(0,10,1), [(1,5)]=(1,9,1), [(1,6)]=(2,7,1), [(1,7)]=(3,6,1), [(1,8)]=(0,6,1), [(1,9)]=(1,5,1), [(1,10)]=(2,3,1), [(1,11)]=(3,2,1), [(1,12)]=(0,2,1),
            [(2,2)]=(3,11,1), [(2,3)]=(0,11,1), [(2,4)]=(1,10,1), [(2,5)]=(2,8,1), [(2,6)]=(3,7,1), [(2,7)]=(0,7,1), [(2,8)]=(1,6,1), [(2,9)]=(2,4,1), [(2,10)]=(3,3,1), [(2,11)]=(0,3,1), [(2,12)]=(1,2,1),
            [(3,2)]=(0,12,1), [(3,3)]=(1,11,1), [(3,4)]=(2,9,1), [(3,5)]=(3,8,1), [(3,6)]=(0,8,1), [(3,7)]=(1,7,1), [(3,8)]=(2,5,1), [(3,9)]=(3,4,1), [(3,10)]=(0,4,1), [(3,11)]=(1,3,1), [(3,12)]=(2,1,1),
        };

    /// <summary>Validates the composed break anchor against the rules-owned oracle. Non-vacuous:
    /// <see cref="ExpectedAnchorOracle"/> is populated with all 44 Vasquez-signed triples.</summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "F1")]
    public void BreakAnchor_MatchesRulesOwnedOracle_WhenProvided()
    {
        Assert.Equal(44, ExpectedAnchorOracle.Count);
        Assert.All(ExpectedAnchorOracle, kv =>
        {
            var ((dealer, diceSum), expected) = (kv.Key, kv.Value);
            var actual = AutotableSlotMap.WallOrdinalToSlot(BreakOrdinal(dealer, diceSum));
            Assert.Equal(expected, actual);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Offset of an ordinal within its owning seat's wall block (0-based), mirroring the
    /// seat-major reduction inside <see cref="AutotableSlotMap.WallOrdinalToSlot"/>.</summary>
    private static int SeatLocalOffset(int ordinal)
    {
        var o = ((ordinal % AutotableSlotMap.TotalTiles) + AutotableSlotMap.TotalTiles) % AutotableSlotMap.TotalTiles;
        for (var seat = 0; seat < 4; seat++)
        {
            var capacity = AutotableSlotMap.WallTileCapacity(seat);
            if (o < capacity) return o;
            o -= capacity;
        }
        throw new InvalidOperationException("unreachable");
    }
}
