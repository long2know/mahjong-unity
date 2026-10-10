using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// CAT-PHASE5A: Translator unit tests. The translator is a pure function over
/// <see cref="ChangshaGameState"/>; these tests assert the shape and contents of
/// the snapshot emitted to the upstream autotable bundle.
/// </summary>
public class AutotableTranslatorTests
{
    // ── upstream typeIndex mapping ────────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void UpstreamTypeIndex_Matches_TileIdDivBy4_ForAllTiles()
    {
        for (var tileId = 0; tileId < 108; tileId++)
        {
            Assert.Equal(tileId / 4, AutotableSlotMap.UpstreamTypeIndex(tileId));
        }
    }

    [Fact, Trait("Category", "Phase5a")]
    public void UpstreamTypeIndex_Throws_OutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AutotableSlotMap.UpstreamTypeIndex(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AutotableSlotMap.UpstreamTypeIndex(108));
    }

    // ── wall slot generation ──────────────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void WallStackCount_Is_14_14_13_13()
    {
        Assert.Equal(14, AutotableSlotMap.WallStackCount(0));
        Assert.Equal(14, AutotableSlotMap.WallStackCount(1));
        Assert.Equal(13, AutotableSlotMap.WallStackCount(2));
        Assert.Equal(13, AutotableSlotMap.WallStackCount(3));
    }

    [Fact, Trait("Category", "Phase5a")]
    public void EnumerateWallSlotsInOrder_Yields_108_Unique_Slots()
    {
        var slots = AutotableSlotMap.EnumerateWallSlotsInOrder()
            .Select(t => AutotableSlotMap.WallSlot(t.Seat, t.Col, t.Layer))
            .ToList();

        Assert.Equal(108, slots.Count);
        Assert.Equal(108, slots.Distinct().Count());

        var bySeat = AutotableSlotMap.EnumerateWallSlotsInOrder().GroupBy(t => t.Seat).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(28, bySeat[0]);
        Assert.Equal(28, bySeat[1]);
        Assert.Equal(26, bySeat[2]);
        Assert.Equal(26, bySeat[3]);
    }

    [Fact, Trait("Category", "Phase5a")]
    public void WallSlot_Rejects_OutOfRange_Cols_PerSeat()
    {
        // Seat 0 has 14 cols → col 13 is valid, col 14 is not.
        Assert.Equal("wall.13.0@0", AutotableSlotMap.WallSlot(0, 13, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AutotableSlotMap.WallSlot(0, 14, 0));

        // Seat 2 has 13 cols → col 12 is valid, col 13 is not.
        Assert.Equal("wall.12.1@2", AutotableSlotMap.WallSlot(2, 12, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AutotableSlotMap.WallSlot(2, 13, 0));
    }

    // ── JOINED snapshot shape ─────────────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_Contains_108_Things()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var things = entries.Where(e => e.Kind == "things").ToList();
        Assert.Equal(108, things.Count);
    }

    // ── #123 follow-up (Hicks P0): things carry an EXPLICIT claimedBy:null ─────────

    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "C-1")]
    public void Things_EmitExplicitNull_ClaimedBy_And_ShiftSlotName_MatchingC1Contract()
    {
        // The C-1 ThingInfo wire type is `claimedBy: number|null` / `shiftSlotName: string|null`
        // (present), and the relay path already emits explicit null (upstream describeThing +
        // verbatim JsonElement clone). Pre-fix the changsha translator emitted an anonymous object
        // whose null fields were dropped by the shared WhenWritingNull serializer, so ~108 tiles
        // OMITTED claimedBy — inconsistent with the type + the relay path. Serialize each entry
        // EXACTLY as the runtime broadcast does (AutotableGameState.CloneValue → AutotableJson.Options)
        // and assert the fields are present with a null value.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var things = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things")
            .ToList();
        Assert.Equal(108, things.Count);

        foreach (var e in things)
        {
            var root = SerializeThing(e.Value!);
            Assert.True(root.TryGetProperty("claimedBy", out var claimedBy),
                "every things entry must carry an explicit claimedBy (C-1: number|null) — never omit it.");
            Assert.Equal(JsonValueKind.Null, claimedBy.ValueKind);
            Assert.True(root.TryGetProperty("shiftSlotName", out var shiftSlotName),
                "every things entry must carry an explicit shiftSlotName (C-1: string|null).");
            Assert.Equal(JsonValueKind.Null, shiftSlotName.ValueKind);
        }
    }

    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "C-1")]
    public void Things_ExplicitClaimedByNull_PreservesSeat0_ViewerPrivacy()
    {
        // Preserve seat 0: the explicit-null normalization is orthogonal to viewer privacy. The
        // viewer's own hand tiles (hand.*@0) stay face-up (HandRotFaceUp=1) with claimedBy:null,
        // while a foreign seat's hand tiles (hand.*@1) stay face-down (HandRotFaceDown=2) — also
        // with an explicit claimedBy:null.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var things = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things")
            .Select(e => SerializeThing(e.Value!))
            .ToList();

        var seat0Hand = things.Where(r => IsHandSlot(r, seat: 0)).ToList();
        var seat1Hand = things.Where(r => IsHandSlot(r, seat: 1)).ToList();
        Assert.NotEmpty(seat0Hand);
        Assert.NotEmpty(seat1Hand);

        foreach (var r in seat0Hand)
        {
            Assert.Equal(1, r.GetProperty("rotationIndex").GetInt32()); // HandRotFaceUp (viewer's own)
            Assert.Equal(JsonValueKind.Null, r.GetProperty("claimedBy").ValueKind);
        }
        foreach (var r in seat1Hand)
        {
            Assert.Equal(2, r.GetProperty("rotationIndex").GetInt32()); // HandRotFaceDown (foreign)
            Assert.Equal(JsonValueKind.Null, r.GetProperty("claimedBy").ValueKind);
        }
    }

    private static JsonElement SerializeThing(object value)
    {
        // Mirrors AutotableGameState.CloneValue: an anonymous/typed value is serialized via
        // AutotableJson.Options (WhenWritingNull) into the stored JsonElement that reaches the wire.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, AutotableJson.Options));
        return doc.RootElement.Clone();
    }

    private static bool IsHandSlot(JsonElement thing, int seat)
    {
        var slot = thing.GetProperty("slotName").GetString() ?? string.Empty;
        return slot.StartsWith("hand.", StringComparison.Ordinal)
            && slot.EndsWith("@" + seat, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_Contains_4_Seats_And_4_Nicks()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        Assert.Equal(4, entries.Count(e => e.Kind == "seats"));
        Assert.Equal(4, entries.Count(e => e.Kind == "nicks"));
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_Contains_1_Match_Entry()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var matchEntries = entries.Where(e => e.Kind == "match").ToList();
        Assert.Single(matchEntries);
        Assert.Equal(0L, Convert.ToInt64(matchEntries[0].Key));
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_Contains_1_Dice_Entry_With_2_Dice()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var dice = entries.Where(e => e.Kind == "dice").ToList();
        Assert.Single(dice);

        // Round-trip through JSON to verify the dice value is a 2-element array
        // and state is "rolled" (per upstream DiceInfo shape).
        var json = JsonSerializer.Serialize(dice[0].Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        var diceArr = doc.RootElement.GetProperty("dice");
        Assert.Equal(JsonValueKind.Array, diceArr.ValueKind);
        Assert.Equal(2, diceArr.GetArrayLength());
        Assert.Equal("rolled", doc.RootElement.GetProperty("state").GetString());
    }

    // ── slot name correctness ─────────────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_WallSlots_AreUnique_AndUseCanonicalNames()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var slotNames = entries
            .Where(e => e.Kind == "things")
            .Select(e => ExtractSlotName(e.Value!))
            .ToList();

        Assert.Equal(slotNames.Count, slotNames.Distinct().Count());
        var wallSlots = slotNames.Where(s => s.StartsWith("wall.")).ToList();

        // Each wall slot must be a valid 14/14/13/13 layout name.
        foreach (var s in wallSlots)
        {
            // Format: wall.{col}.{layer}@{seat}
            var parts = s.Split('.');
            Assert.Equal(3, parts.Length);
            var seat = int.Parse(parts[2].Split('@')[1]);
            var col = int.Parse(parts[1]);
            var layer = int.Parse(parts[2].Split('@')[0]);
            Assert.InRange(col, 0, AutotableSlotMap.WallStackCount(seat) - 1);
            Assert.InRange(layer, 0, 1);
        }
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_HandSlots_Sized13Or14PerSeat()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var slotNames = entries
            .Where(e => e.Kind == "things")
            .Select(e => ExtractSlotName(e.Value!))
            .ToList();

        var bySeat = new Dictionary<int, int>();
        foreach (var s in slotNames.Where(n => n.StartsWith("hand.")))
        {
            var seat = int.Parse(s.Split('@')[1]);
            bySeat[seat] = bySeat.GetValueOrDefault(seat) + 1;
        }

        Assert.Equal(14, bySeat[state.DealerSeatIndex]);
        for (var i = 0; i < 4; i++)
            if (i != state.DealerSeatIndex)
                Assert.Equal(13, bySeat[i]);
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_TotalWallThings_Equal_55()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var wallCount = entries
            .Where(e => e.Kind == "things")
            .Count(e => ExtractSlotName(e.Value!).StartsWith("wall."));
        Assert.Equal(55, wallCount);
    }

    // ── #152 — post-deal wall depletes contiguously from the break point ──
    //
    // Superseded the Phase-5a "distribute across all four seats / balanced"
    // contract (which pinned the column-major spread Stephen later flagged as
    // "four disconnected half-walls", issue #152). The authoritative engine
    // draws 53 tiles contiguously from the dice break point, so the 55
    // remaining tiles form ONE physical arc: AT MOST TWO seat-walls can be
    // partially consumed — the other two must be fully FULL or fully EMPTY,
    // and every seat's occupied columns are contiguous (no interior gaps).

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_WallTiles_DepleteContiguouslyFromBreakPoint()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var perSeatWallCounts = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0, [3] = 0 };
        var perSeatCols = new Dictionary<int, SortedSet<int>>
        {
            [0] = [], [1] = [], [2] = [], [3] = [],
        };
        foreach (var e in entries.Where(e => e.Kind == "things"))
        {
            var slot = ExtractSlotName(e.Value!);
            if (!slot.StartsWith("wall.")) continue;
            var seat = int.Parse(slot.Split('@')[1]);
            var col = int.Parse(slot.Split('@')[0].Split('.')[1]);
            perSeatWallCounts[seat]++;
            perSeatCols[seat].Add(col);
        }

        // Contiguity signature: >= 2 seats fully full or fully empty.
        var fullOrEmpty = 0;
        for (var seat = 0; seat < 4; seat++)
        {
            var cap = AutotableSlotMap.WallTileCapacity(seat);
            if (perSeatWallCounts[seat] == 0 || perSeatWallCounts[seat] == cap) fullOrEmpty++;
        }
        Assert.True(fullOrEmpty >= 2,
            $"Wall did not deplete contiguously — expected >= 2 seats fully full/empty, got {fullOrEmpty} " +
            $"(per-seat counts {string.Join(",", perSeatWallCounts.Select(kv => $"{kv.Key}:{kv.Value}"))}).");

        // No interior gaps within any seat's occupied columns.
        for (var seat = 0; seat < 4; seat++)
        {
            var cols = perSeatCols[seat].ToList();
            for (var i = 1; i < cols.Count; i++)
            {
                Assert.True(cols[i] - cols[i - 1] == 1,
                    $"seat {seat} wall has an interior column gap between {cols[i - 1]} and {cols[i]}.");
            }
        }
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_WallTiles_StackedTwoHighExceptArcBoundaries()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        // Every OCCUPIED wall stack must render 2-high (layer 0 AND layer 1),
        // except at most the two boundaries of the drawn arc where a single
        // stack may be half-consumed. Count single-layer occupied stacks.
        var byStackLayers = new Dictionary<(int seat, int col), HashSet<int>>();
        foreach (var e in entries.Where(e => e.Kind == "things"))
        {
            var slot = ExtractSlotName(e.Value!);
            if (!slot.StartsWith("wall.")) continue;
            var beforeAt = slot.Split('@')[0];
            var parts = beforeAt.Split('.');
            var col = int.Parse(parts[1]);
            var layer = int.Parse(parts[2]);
            var seat = int.Parse(slot.Split('@')[1]);
            var key = (seat, col);
            (byStackLayers.TryGetValue(key, out var set) ? set : byStackLayers[key] = []).Add(layer);
        }

        var halfStacks = byStackLayers.Count(kv => kv.Value.Count == 1);
        Assert.True(halfStacks <= 2,
            $"Expected at most 2 half (single-layer) wall stacks at the arc boundaries, got {halfStacks} — " +
            "wall is rendering as flat single-row strips instead of 2-high stacks.");

        // And there must be genuine 2-high stacks (not a wholly flat wall).
        Assert.Contains(byStackLayers, kv => kv.Value.Count == 2);
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterStartGame_NoPhantomDiscards_BeforeAnyDiscardEvent()
    {
        // The dealing ceremony alone must not produce any discard.X.Y@N slots.
        // A phantom discard would surface as a stray tile in the radial tray.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var discardSlots = entries
            .Where(e => e.Kind == "things")
            .Select(e => ExtractSlotName(e.Value!))
            .Where(s => s.StartsWith("discard."))
            .ToList();

        Assert.Empty(discardSlots);
    }

    // ── Frost 2026-06-01 — wall fence-post regression ──────────────────
    //
    // Hicks's setup-slots.ts split CHANGSHA walls into per-seat sizes
    // (seats 0,1 → row(14); seats 2,3 → row(13)), so the only valid wall
    // slot keys for seats 2,3 are wall.{0..12}.{0,1}@{2,3}.  Anything
    // referencing col >= 13 for seat 2 or 3 (or col >= 14 for seat 0 or 1)
    // is an out-of-bounds emit and surfaces as a frontend
    // <c>throw `slot not found: wall.13.0@2`</c> page error (the
    // pageerror name="slot not found" + message="wall.13.0@2" pair from
    // <c>setup.ts:256</c>).  This test pins the contract that the
    // translator NEVER emits such a slot — covering BOTH the synthesized
    // 108-tile pre-WS wall (Seating + RollingDice) AND the post-deal
    // 55-tile authoritative wall path.

    [Theory, Trait("Category", "Phase5a")]
    [InlineData(ChangshaPhase.Seating)]
    [InlineData(ChangshaPhase.RollingDice)]
    public void Snapshot_PreDeal_NeverEmits_OverLimitWallSlots(ChangshaPhase phase)
    {
        // Pre-deal synthesized wall path — state.Wall is empty, no hands,
        // no melds, no discards. Translator synthesizes 108 face-down tiles.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 7);
        state.Phase = phase;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        AssertNoOverLimitWallSlots(entries);
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_PostDeal_NeverEmits_OverLimitWallSlots()
    {
        // Post-deal authoritative wall path — state.Wall has 55 tiles
        // (108 dealt - 53 to hands). Translator uses EnumerateWallSlotsInOrder.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        AssertNoOverLimitWallSlots(entries);
    }

    [Theory, Trait("Category", "Phase5a")]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(23)]
    [InlineData(31)]
    public void Snapshot_NeverEmits_OverLimitWallSlots_AcrossSeeds(int seed)
    {
        // Regression sweep — multiple seeds in case the shuffled wall
        // order ever influences slot emission (it shouldn't, but pin it).
        var state = ChangshaTestHelpers.NewGameDealtTo(seed);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        AssertNoOverLimitWallSlots(entries);
    }

    [Fact, Trait("Category", "Phase5a")]
    public void EnumerateWallSlotsInOrder_NeverYields_OverLimitTuples()
    {
        // Belt-and-suspenders against the raw enumeration. The iterator
        // is the only producer of (seat, col, layer) tuples; if it ever
        // yields col >= WallStackCount(seat) the WallSlot validator will
        // throw at emit time (caught by the snapshot tests above), but
        // pinning the iterator directly prevents the bug from regressing
        // into runtime exceptions that look like backend errors when the
        // real cause would be a translator-internal off-by-one.
        foreach (var (seat, col, layer) in AutotableSlotMap.EnumerateWallSlotsInOrder())
        {
            Assert.InRange(seat, 0, 3);
            Assert.InRange(col, 0, AutotableSlotMap.WallStackCount(seat) - 1);
            Assert.InRange(layer, 0, 1);
        }
    }

    private static void AssertNoOverLimitWallSlots(IReadOnlyList<CollectionEntry> entries)
    {
        var wallSlots = entries
            .Where(e => e.Kind == "things")
            .Select(e => ExtractSlotName(e.Value!))
            .Where(s => s.StartsWith("wall."))
            .ToList();

        // Cover both the per-seat upper bounds the task brief calls out:
        //   • seats 2,3 — col must be in [0,12]; wall.{13|14|…}@{2|3} is illegal
        //   • seats 0,1 — col must be in [0,13]; wall.{14|15|…}@{0|1} is illegal
        var seat23OverLimit = new System.Text.RegularExpressions.Regex(
            @"^wall\.(?:1[3-9]|[2-9]\d|\d{3,})\.[01]@[23]$");
        var seat01OverLimit = new System.Text.RegularExpressions.Regex(
            @"^wall\.(?:1[4-9]|[2-9]\d|\d{3,})\.[01]@[01]$");

        foreach (var slot in wallSlots)
        {
            Assert.False(seat23OverLimit.IsMatch(slot),
                $"Backend emitted out-of-bounds wall slot for seat 2/3: {slot} " +
                $"(seats 2,3 only have cols 0..12 per Hicks's row(13) setup-slots.ts layout).");
            Assert.False(seat01OverLimit.IsMatch(slot),
                $"Backend emitted out-of-bounds wall slot for seat 0/1: {slot} " +
                $"(seats 0,1 only have cols 0..13 per Hicks's row(14) setup-slots.ts layout).");
        }

        // Stronger invariant — every wall slot must parse cleanly and the
        // (seat, col) tuple must satisfy WallSlot's range guard.
        foreach (var slot in wallSlots)
        {
            // wall.{col}.{layer}@{seat}
            var beforeAt = slot.Split('@')[0];
            var afterAt = slot.Split('@')[1];
            var parts = beforeAt.Split('.');
            Assert.Equal(3, parts.Length);
            var col = int.Parse(parts[1]);
            var layer = int.Parse(parts[2]);
            var seat = int.Parse(afterAt);

            Assert.InRange(seat, 0, 3);
            Assert.InRange(layer, 0, 1);
            Assert.InRange(col, 0, AutotableSlotMap.WallStackCount(seat) - 1);
        }
    }

    // ── discard slot mapping ──────────────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void DiscardSlot_Names_FollowDocumentedShape()
    {
        Assert.Equal("discard.0.0@0", AutotableSlotMap.DiscardSlot(0, 0, 0));
        Assert.Equal("discard.2.5@3", AutotableSlotMap.DiscardSlot(3, 2, 5));
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_AfterDiscard_TileMovesFromHandToDiscard()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var dealer = state.DealerSeatIndex;
        var tile = state.Hands[dealer].ConcealedTiles.First();
        ChangshaGameStateMachine.Discard(state, dealer, tile);

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: dealer);
        var slotByTile = entries
            .Where(e => e.Kind == "things")
            .ToDictionary(e => Convert.ToInt32(e.Key), e => ExtractSlotName(e.Value!));

        // Discarded tile must now sit in seat-N's discard.0.0 slot.
        Assert.Equal(AutotableSlotMap.DiscardSlot(dealer, 0, 0), slotByTile[tile]);
        // No hand slot still references the discarded tile.
        Assert.DoesNotContain(slotByTile.Values, s => s == AutotableSlotMap.HandSlot(dealer, 0) && slotByTile.ContainsKey(tile) == false);
    }

    // ── meld slot mapping ─────────────────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_WithPungMeld_Produces_3_MeldEntries_At_Meld0()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var seat = (state.DealerSeatIndex + 1) % 4;
        // Inject a synthetic pung meld on seat N — 3 tiles of 1-tong.
        state.Hands[seat].Melds.Add(new Meld
        {
            Kind = MeldKind.Pung,
            TileIds = new List<int>
            {
                ChangshaTestHelpers.Tid(Suit.Tong, 1, 0),
                ChangshaTestHelpers.Tid(Suit.Tong, 1, 1),
                ChangshaTestHelpers.Tid(Suit.Tong, 1, 2)
            },
            ClaimedFromSeatIndex = state.DealerSeatIndex
        });
        // Also remove these tiles from anywhere else they exist, to keep totals at 108.
        foreach (var id in state.Hands[seat].Melds[^1].TileIds)
        {
            foreach (var h in state.Hands)
                h.ConcealedTiles.Remove(id);
            state.Wall.Remove(id);
        }

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var meldEntries = entries
            .Where(e => e.Kind == "things")
            .Where(e => ExtractSlotName(e.Value!).StartsWith($"meld.0."))
            .Where(e => ExtractSlotName(e.Value!).EndsWith($"@{seat}"))
            .ToList();

        Assert.Equal(3, meldEntries.Count);
        foreach (var e in meldEntries)
            Assert.Equal(0, ExtractRotationIndex(e.Value!)); // FACE_UP
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Snapshot_WithConcealedKong_Produces_4_FaceDown_MeldEntries()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var seat = (state.DealerSeatIndex + 1) % 4;
        // Inject a concealed kong (meld index 0) — 4 tiles of 1-wan.
        state.Hands[seat].Melds.Add(new Meld
        {
            Kind = MeldKind.ConcealedKong,
            TileIds = new List<int>
            {
                ChangshaTestHelpers.Tid(Suit.Wan, 1, 0),
                ChangshaTestHelpers.Tid(Suit.Wan, 1, 1),
                ChangshaTestHelpers.Tid(Suit.Wan, 1, 2),
                ChangshaTestHelpers.Tid(Suit.Wan, 1, 3)
            }
        });
        foreach (var id in state.Hands[seat].Melds[^1].TileIds)
        {
            foreach (var h in state.Hands)
                h.ConcealedTiles.Remove(id);
            state.Wall.Remove(id);
        }

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var meldEntries = entries
            .Where(e => e.Kind == "things")
            .Where(e => ExtractSlotName(e.Value!).StartsWith($"meld.0."))
            .Where(e => ExtractSlotName(e.Value!).EndsWith($"@{seat}"))
            .ToList();

        Assert.Equal(4, meldEntries.Count);
        // Concealed kong → rotationIndex 2 (FACE_DOWN per upstream meld slot rotations).
        foreach (var e in meldEntries)
            Assert.Equal(2, ExtractRotationIndex(e.Value!));
    }

    // ── always-available pattern ──────────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void Translate_WithNullState_ReturnsOnlyMatchEntry()
    {
        var entries = ChangshaToAutotableTranslator.Translate(null);
        Assert.Single(entries);
        Assert.Equal("match", entries[0].Kind);
    }

    [Fact, Trait("Category", "Phase5a")]
    public void Translate_MatchEntry_ForcesFives000_ForCleanTypeIndexMapping()
    {
        var entries = ChangshaToAutotableTranslator.Translate(null);
        var match = entries.Single(e => e.Kind == "match");
        var json = JsonSerializer.Serialize(match.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("000", doc.RootElement.GetProperty("conditions").GetProperty("fives").GetString());
        // BE-1 (Ripley §9.1): the surfaced variant is now the honest Changsha value,
        // decoupled from the fives='000' catalog assertion above.
        Assert.Equal("CHANGSHA", doc.RootElement.GetProperty("conditions").GetProperty("gameType").GetString());
    }

    // ── viewer-seat hand visibility ───────────────────────────────────

    [Fact, Trait("Category", "Phase5a")]
    public void HandTiles_AreFaceUp_ForViewerSeat_AndFaceDown_ForOthers()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        foreach (var e in entries.Where(e => e.Kind == "things"))
        {
            var slot = ExtractSlotName(e.Value!);
            if (!slot.StartsWith("hand.")) continue;
            var seat = int.Parse(slot.Split('@')[1]);
            var rot = ExtractRotationIndex(e.Value!);
            if (seat == 0)
                Assert.Equal(1, rot); // FACE_UP
            else
                Assert.Equal(2, rot); // FACE_DOWN
        }
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static string ExtractSlotName(object value)
    {
        var json = JsonSerializer.Serialize(value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("slotName").GetString()!;
    }

    private static int ExtractRotationIndex(object value)
    {
        var json = JsonSerializer.Serialize(value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("rotationIndex").GetInt32();
    }

    // ── claim window deadline plumbing (Frost 2026-05-29) ─────────────
    //
    // When the runtime opens a claim window the state machine stamps
    // <c>ChangshaClaimWindow.OpenedAtUnixMs</c>.  The translator must surface
    // an absolute deadline = OpenedAtUnixMs + claimWindowTimeoutMs so the
    // autotable overlay / side-panel countdown renders meaningfully instead
    // of treating <c>deadline=0</c> as "already expired" and auto-passing.
    // Falls back to 0 when the caller doesn't supply a timeout (back-compat).

    [Fact, Trait("Category", "Phase5a")]
    public void ClaimEntry_EmitsAbsoluteDeadline_WhenTimeoutPassed()
    {
        var state = new ChangshaGameState
        {
            Phase = ChangshaPhase.AwaitingClaim,
            ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = 0,
                DiscardTileId = 12,
                OpenedAtUnixMs = 1_700_000_000_000L,
                Opportunities = [
                    new ChangshaClaimOpportunity
                    {
                        SeatIndex = 1,
                        ClaimType = Tables.TableClaimType.Pung,
                        Priority = 1
                    }
                ]
            }
        };
        for (var i = 0; i < 4; i++)
        {
            state.Seats.Add(new ChangshaSeatState { SeatIndex = i });
            state.Hands.Add(new ChangshaHandState { SeatIndex = i });
        }

        var entries = ChangshaToAutotableTranslator.Translate(
            state, viewerSeat: 1, claimWindowTimeoutMs: 5000);

        var claim = entries.Single(e => e.Kind == "claim" && e.Value is not null);
        var json = JsonSerializer.Serialize(claim.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        // deadline = 1_700_000_000_000 + 5000 = 1_700_000_005_000
        Assert.Equal(1_700_000_005_000L, doc.RootElement.GetProperty("deadline").GetInt64());
    }

    [Fact, Trait("Category", "Phase5a")]
    public void ClaimEntry_EmitsZeroDeadline_WhenTimeoutNotPassed()
    {
        var state = new ChangshaGameState
        {
            Phase = ChangshaPhase.AwaitingClaim,
            ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = 0,
                DiscardTileId = 12,
                OpenedAtUnixMs = 1_700_000_000_000L,
                Opportunities = [
                    new ChangshaClaimOpportunity
                    {
                        SeatIndex = 1,
                        ClaimType = Tables.TableClaimType.Pung,
                        Priority = 1
                    }
                ]
            }
        };
        for (var i = 0; i < 4; i++)
        {
            state.Seats.Add(new ChangshaSeatState { SeatIndex = i });
            state.Hands.Add(new ChangshaHandState { SeatIndex = i });
        }

        // Caller didn't supply a timeout — preserve the legacy contract
        // (0 = "no client timer; server enforces").
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 1);

        var claim = entries.Single(e => e.Kind == "claim" && e.Value is not null);
        var json = JsonSerializer.Serialize(claim.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0L, doc.RootElement.GetProperty("deadline").GetInt64());
    }

    [Fact, Trait("Category", "Phase5a")]
    public void ClaimEntry_EmitsZeroDeadline_WhenOpenedAtZero_EvenWithTimeout()
    {
        // Rehydrated state from before OpenedAtUnixMs existed — translator
        // must NOT compute a bogus deadline = 0 + timeout, since that's a
        // 1970 epoch value that fails the "is this still open" check.
        var state = new ChangshaGameState
        {
            Phase = ChangshaPhase.AwaitingClaim,
            ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = 0,
                DiscardTileId = 12,
                OpenedAtUnixMs = 0L, // rehydrated / legacy
                Opportunities = [
                    new ChangshaClaimOpportunity
                    {
                        SeatIndex = 1,
                        ClaimType = Tables.TableClaimType.Pung,
                        Priority = 1
                    }
                ]
            }
        };
        for (var i = 0; i < 4; i++)
        {
            state.Seats.Add(new ChangshaSeatState { SeatIndex = i });
            state.Hands.Add(new ChangshaHandState { SeatIndex = i });
        }

        var entries = ChangshaToAutotableTranslator.Translate(
            state, viewerSeat: 1, claimWindowTimeoutMs: 5000);

        var claim = entries.Single(e => e.Kind == "claim" && e.Value is not null);
        var json = JsonSerializer.Serialize(claim.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0L, doc.RootElement.GetProperty("deadline").GetInt64());
    }

    [Fact, Trait("Category", "Phase5a")]
    public void ClaimEntry_EmitsOnePerEligibleSeat_KeyedBySeatIndex()
    {
        // Per Changsha spec §3.3 every eligible seat (not just the priority
        // winner) gets its own claim entry, keyed by that seat's index.  The
        // frontend overlay matches `key === String(selfSeat)` to decide
        // whether to surface the window; missing entries = no overlay.
        var state = new ChangshaGameState
        {
            Phase = ChangshaPhase.AwaitingClaim,
            ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = 0,
                DiscardTileId = 12,
                OpenedAtUnixMs = 1_700_000_000_000L,
                Opportunities = [
                    new ChangshaClaimOpportunity
                    {
                        SeatIndex = 1, ClaimType = Tables.TableClaimType.Chow, Priority = 1
                    },
                    new ChangshaClaimOpportunity
                    {
                        SeatIndex = 2, ClaimType = Tables.TableClaimType.Pung, Priority = 2
                    },
                    new ChangshaClaimOpportunity
                    {
                        SeatIndex = 3, ClaimType = Tables.TableClaimType.Hu, Priority = 3
                    }
                ]
            }
        };
        for (var i = 0; i < 4; i++)
        {
            state.Seats.Add(new ChangshaSeatState { SeatIndex = i });
            state.Hands.Add(new ChangshaHandState { SeatIndex = i });
        }

        var entries = ChangshaToAutotableTranslator.Translate(
            state, viewerSeat: 1, claimWindowTimeoutMs: 5000);

        var claims = entries.Where(e => e.Kind == "claim" && e.Value is not null).ToList();
        Assert.Equal(3, claims.Count);
        // BE-4 (Ripley §9.1) — seat 0 has no opportunity, so it must receive an explicit
        // null tombstone in the same snapshot (no stale claim can linger for that seat).
        Assert.Contains(entries, e => e.Kind == "claim" && e.Key.Equals("0") && e.Value is null);
        // Frost 2026-05-29 — keys are stringified to match the frontend
        // Collection's Map<string, V> storage convention (game-ui.ts writes
        // `client.claim.set(String(selfSeat), …)` locally; the server must
        // use the same key shape so updates merge into a single entry).
        Assert.Contains(claims, c => c.Key.Equals("1"));
        Assert.Contains(claims, c => c.Key.Equals("2"));
        Assert.Contains(claims, c => c.Key.Equals("3"));
        // Discarder (seat 0) never gets a claim entry.
        Assert.DoesNotContain(claims, c => c.Key.Equals("0"));
    }
}
