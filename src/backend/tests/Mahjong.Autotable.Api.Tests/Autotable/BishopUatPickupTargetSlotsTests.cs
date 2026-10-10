using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// FINAL SC-4 (user 2026-08-07T11:29 + Ripley canonical lock 11:23) — the manual-pickup match key is
/// <c>pickup.targetSlots</c>, EXACTLY length 1 = the co-derived render slot of <c>state.Wall[0]</c>,
/// co-emitted in the SAME translator wall-<c>things</c> pass; <c>count</c> is 4/1; NO raw tile ids and
/// NO opaque handles in the pickup signal; the full batch is display-only <c>batchPreviewSlots</c>; and
/// the pickup cursor is explicitly tombstoned when the ceremony ends. This suite proves the field for
/// EVERY pickup phase × dealer × dice sum AND that it survives the endpoint projection pipeline to the
/// wire snapshot (Ralph's "backend emits no targetSlots" gate — RED on a build without the field).
/// </summary>
public class BishopUatPickupTargetSlotsTests
{
    private static DiceRoll RollForSum(int sum)
    {
        var d1 = Math.Clamp(sum - 1, 1, 6);
        return new DiceRoll(d1, sum - d1);
    }

    private static ChangshaGameState ManualDealAt(int dealer, int sum, int seed)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: seed, botSeatIndexes: null);
        state.DealerSeatIndex = dealer;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealer;
        ChangshaGameStateMachine.StartGame(state);
        state.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.BeginManualDeal(state, RollForSum(sum));
        return state;
    }

    /// <summary>Maps every wall <c>things</c> entry (privacy off ⇒ key == tileId) to its slotName.</summary>
    private static Dictionary<int, string> WallThingSlots(IEnumerable<CollectionEntry> entries)
    {
        var map = new Dictionary<int, string>();
        foreach (var e in entries.Where(e => e.Kind == "things" && e.Value is not null))
        {
            var sn = JsonDocument.Parse(JsonSerializer.Serialize(e.Value, AutotableJson.Options))
                .RootElement.GetProperty("slotName").GetString()!;
            if (sn.StartsWith("wall.", StringComparison.Ordinal))
                map[Convert.ToInt32(e.Key)] = sn;
        }
        return map;
    }

    private static void AssertPickupWireConformsToFinalSc4(ChangshaGameState state)
    {
        var picker = state.PickupSeatIndex ?? state.DealerSeatIndex;
        var expectedCount = ChangshaGameStateMachine.ExpectedPickupCount(state.Phase);
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: picker);

        var pickup = entries.Single(e => e.Kind == "pickup" && e.Value is not null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(pickup.Value, AutotableJson.Options));

        // count is authoritatively 4 or 1.
        var count = doc.RootElement.GetProperty("count").GetInt32();
        Assert.Equal(expectedCount, count);
        Assert.Contains(count, new[] { 1, 4 });

        // targetSlots = EXACTLY ONE trigger slot = the co-derived render slot of state.Wall[0].
        var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Single(slots);
        var wallSlots = WallThingSlots(entries);
        Assert.True(wallSlots.TryGetValue(state.Wall[0], out var wall0Slot),
            $"front tile {state.Wall[0]} was not emitted as a wall thing");
        Assert.Equal(wall0Slot, slots[0]); // co-derived, not recomputed

        // No raw tile ids, no opaque handles in the pickup signal.
        Assert.False(doc.RootElement.TryGetProperty("targetTileIds", out _),
            $"FINAL SC-4 forbids targetTileIds (phase {state.Phase}).");
        Assert.False(doc.RootElement.TryGetProperty("targetHandles", out _),
            $"FINAL SC-4 forbids targetHandles (phase {state.Phase}).");

        // Full batch is display-only under a distinct name, co-derived from the same pass.
        var preview = doc.RootElement.GetProperty("batchPreviewSlots").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(count, preview.Count);
        Assert.Equal(slots[0], preview[0]);
        for (var i = 0; i < count; i++)
        {
            Assert.True(wallSlots.TryGetValue(state.Wall[i], out var wsi));
            Assert.Equal(wsi, preview[i]);
        }
    }

    [Theory, Trait("Category", "UatBackend"), Trait("Contract", "SC4-final")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TargetSlots_IsReachableExposedTopEnd_EveryPickupPhase_AllDice(int dealer)
    {
        // Blocker B (Bishop rev2) — the SINGLE pickup trigger slot must be the physically
        // REACHABLE exposed end (= the next drawable tile, state.Wall[0]) under the top-first
        // F2 map, for EVERY dealer × dice sum across the whole ceremony. Two co-derived
        // invariants:
        //   (a) At the BREAK itself (BreakPointMarked, nothing drawn yet) the exposed end is
        //       always a stack TOP — layer 1 (`wall.{col}.1@{seat}`). This is non-vacuous: it
        //       fails closed if the break anchor ever produced an ODD render ordinal (which
        //       maps to layer 0 = a BURIED tile), i.e. it pins "break lands on a stack top
        //       across the dealer/dice seed sweep". (Mid-stack single-tile picks legitimately
        //       expose layer 0 next, so layer 1 is asserted only at the break.)
        //   (b) At EVERY phase the trigger equals the render slot of Wall[0] (co-derived,
        //       never a second ordinal computation).
        for (var sum = 2; sum <= 12; sum++)
        {
            var state = ManualDealAt(dealer, sum, seed: 900 + dealer * 20 + sum);
            var guard = 0;
            while (ChangshaGameStateMachine.IsPickupPhase(state.Phase))
            {
                var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: state.DealerSeatIndex);
                var pickup = entries.Single(e => e.Kind == "pickup" && e.Value is not null);
                var doc = JsonDocument.Parse(JsonSerializer.Serialize(pickup.Value, AutotableJson.Options));
                var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray()
                    .Select(x => x.GetString()!).ToList();
                var trigger = Assert.Single(slots);

                // (b) co-derived with the next drawable tile Wall[0].
                var wallSlots = WallThingSlots(entries);
                Assert.True(wallSlots.TryGetValue(state.Wall[0], out var wall0Slot));
                Assert.Equal(wall0Slot, trigger);

                // (a) the break itself must be a stack TOP (layer 1). Slot shape is
                // `wall.{col}.{layer}@{seat}`.
                if (state.Phase == ChangshaPhase.BreakPointMarked)
                {
                    var layer = trigger.Split('.')[2].Split('@')[0];
                    Assert.True(layer == "1",
                        $"break-point pickup targetSlots[0]='{trigger}' is not a stack top (layer 1) "
                        + $"reachable exposed end (dealer={dealer}, sum={sum}).");
                }

                var picker = state.PickupSeatIndex ?? state.DealerSeatIndex;
                ChangshaGameStateMachine.TakeTilesFromWall(state, picker, ChangshaGameStateMachine.ExpectedPickupCount(state.Phase));
                if (++guard > 32) break;
            }
            Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        }
    }

    [Theory, Trait("Category", "UatBackend"), Trait("Contract", "SC4-final")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TargetSlots_LengthOne_CoDerived_EveryPickupPhase_AllDice(int dealer)
    {
        // Walk the ENTIRE manual ceremony (BreakPointMarked → PickupRound1..3 → SingleTilePickup →
        // DealerExtra) for every dice sum 2..12, asserting the wire pickup at EACH phase.
        for (var sum = 2; sum <= 12; sum++)
        {
            var state = ManualDealAt(dealer, sum, seed: 500 + dealer * 20 + sum);
            var phasesSeen = new List<ChangshaPhase>();

            var guard = 0;
            while (ChangshaGameStateMachine.IsPickupPhase(state.Phase))
            {
                phasesSeen.Add(state.Phase);
                AssertPickupWireConformsToFinalSc4(state);

                var picker = state.PickupSeatIndex ?? state.DealerSeatIndex;
                var take = ChangshaGameStateMachine.ExpectedPickupCount(state.Phase);
                ChangshaGameStateMachine.TakeTilesFromWall(state, picker, take);

                if (++guard > 32) break; // safety — the ceremony is 18 picks
            }

            // The full ceremony visited every pickup phase and finished at AwaitingDiscard.
            Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
            Assert.Contains(ChangshaPhase.BreakPointMarked, phasesSeen);
            Assert.Contains(ChangshaPhase.PickupRound1, phasesSeen);
            Assert.Contains(ChangshaPhase.PickupRound2, phasesSeen);
            Assert.Contains(ChangshaPhase.PickupRound3, phasesSeen);
            Assert.Contains(ChangshaPhase.SingleTilePickup, phasesSeen);
            Assert.Contains(ChangshaPhase.DealerExtra, phasesSeen);
        }
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "SC4-final")]
    public void TargetSlots_ReachesProjectedSnapshot_EphemeralPath()
    {
        // Ralph's exact concern: does `targetSlots` reach the PROJECTED wire snapshot (not just the raw
        // translator output)? Replicate the endpoint's ephemeral projection: register pickup ephemeral,
        // ApplyUpdate(Runtime), then MergeRuntimeEphemerals re-attaches it. Assert targetSlots survives.
        var state = ManualDealAt(dealer: 0, sum: 7, seed: 7);
        var translatorEntries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var gameState = new AutotableGameState("g-proj-ephemeral");
        gameState.ApplyUpdate(
            new[] { new CollectionEntry("ephemeral", ChangshaCollectionKinds.Pickup, true) },
            UpdateSource.Client);
        gameState.ApplyUpdate(translatorEntries, UpdateSource.Runtime);
        var snapshot = AutotableConnectionManager.MergeRuntimeEphemerals(
            gameState.Snapshot(), translatorEntries, gameState);

        AssertProjectedPickupHasSingleTargetSlot(snapshot);
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "SC4-final")]
    public void TargetSlots_ReachesProjectedSnapshot_StoredPath()
    {
        // If pickup is NOT registered ephemeral, ApplyUpdate STORES it (serialized to JsonElement). The
        // stored snapshot must still carry targetSlots (explicit [JsonPropertyName] survives the round-trip).
        var state = ManualDealAt(dealer: 2, sum: 9, seed: 99);
        var translatorEntries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 2);

        var gameState = new AutotableGameState("g-proj-stored");
        gameState.ApplyUpdate(translatorEntries, UpdateSource.Runtime);
        AssertProjectedPickupHasSingleTargetSlot(gameState.Snapshot());
    }

    private static void AssertProjectedPickupHasSingleTargetSlot(IReadOnlyList<CollectionEntry> snapshot)
    {
        var pickup = snapshot.Single(e => e.Kind == ChangshaCollectionKinds.Pickup && e.Value is not null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(pickup.Value, AutotableJson.Options));

        var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Single(slots);
        Assert.Matches(@"^wall\.\d+\.\d+@\d+$", slots[0]);
        Assert.Contains(doc.RootElement.GetProperty("count").GetInt32(), new[] { 1, 4 });
        Assert.False(doc.RootElement.TryGetProperty("targetTileIds", out _));
        Assert.False(doc.RootElement.TryGetProperty("targetHandles", out _));
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "SC4-final")]
    public void Pickup_Tombstoned_ReachesProjectedSnapshot_WhenCeremonyEnds()
    {
        // Drive the full ceremony to AwaitingDiscard, then the translator must emit an explicit pickup
        // tombstone (null) that survives the ephemeral merge — so the client clears isMyPickupTurn().
        var state = ManualDealAt(dealer: 1, sum: 5, seed: 55);
        var guard = 0;
        while (ChangshaGameStateMachine.IsPickupPhase(state.Phase))
        {
            ChangshaGameStateMachine.TakeTilesFromWall(
                state, state.PickupSeatIndex ?? state.DealerSeatIndex,
                ChangshaGameStateMachine.ExpectedPickupCount(state.Phase));
            if (++guard > 32) break;
        }
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);

        var translatorEntries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 1);
        var gameState = new AutotableGameState("g-tomb");
        gameState.ApplyUpdate(
            new[] { new CollectionEntry("ephemeral", ChangshaCollectionKinds.Pickup, true) },
            UpdateSource.Client);
        gameState.ApplyUpdate(translatorEntries, UpdateSource.Runtime);
        var snapshot = AutotableConnectionManager.MergeRuntimeEphemerals(
            gameState.Snapshot(), translatorEntries, gameState);

        Assert.Contains(snapshot, e => e.Kind == ChangshaCollectionKinds.Pickup && e.Value is null);
    }

    /// <summary>
    /// F2 (user 2026-08-07T11:43) — the AUTHORITATIVE-TAKE half owned by this lane: the click on
    /// <c>targetSlots[0]</c> (= <c>Wall[0]</c>'s co-derived reachable slot) drives a count-based take that
    /// consumes EXACTLY the front batch — a 4-batch removes the front four tiles (physically two full
    /// stacks under the slotmap top-first map), a 1-tile removes exactly the clicked top (<c>Wall[0]</c>).
    /// Layer/anchor-agnostic ⇒ GREEN on this lane's pre-F2 base and still GREEN once the slotmap top-first
    /// <c>WallOrdinalToSlot</c> lands. The physical "top layer 1 / two-stack / top→bottom" assertions are
    /// the slotmap lane's <c>SlotMapWallGoldenTests</c> (F1 anchor + F2 top-first), which this composes with.
    /// </summary>
    [Theory, Trait("Category", "UatBackend"), Trait("Contract", "F2")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void F2_Take_ConsumesExactlyFrontBatch_ClickedTopIsTargetSlot0_AllDice(int dealer)
    {
        for (var sum = 2; sum <= 12; sum++)
        {
            var state = ManualDealAt(dealer, sum, seed: 700 + dealer * 20 + sum);
            var guard = 0;
            while (ChangshaGameStateMachine.IsPickupPhase(state.Phase))
            {
                var picker = state.PickupSeatIndex ?? state.DealerSeatIndex;
                var count = ChangshaGameStateMachine.ExpectedPickupCount(state.Phase);
                var frontBatch = state.Wall.Take(count).ToList();
                var clickedTop = state.Wall[0];

                // The click target = targetSlots[0] = Wall[0]'s co-derived projected slot (the reachable
                // TOP once the slotmap top-first map lands; the wall-thing for Wall[0] carries that slot).
                var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: picker);
                using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(
                    entries.Single(e => e.Kind == "pickup" && e.Value is not null).Value, AutotableJson.Options)))
                {
                    var slot0 = doc.RootElement.GetProperty("targetSlots").EnumerateArray().Single().GetString();
                    var wallSlots = WallThingSlots(entries);
                    Assert.Equal(wallSlots[clickedTop], slot0);
                }

                var before = state.Wall.Count;
                ChangshaGameStateMachine.TakeTilesFromWall(state, picker, count);

                // Count-based server take removed EXACTLY the front `count` tiles (4 = two stacks; 1 = the
                // clicked top). The client had zero tile/slot authority — it only clicked targetSlots[0].
                Assert.Equal(before - count, state.Wall.Count);
                Assert.Contains(clickedTop, frontBatch);
                foreach (var t in frontBatch) Assert.DoesNotContain(t, state.Wall);
                if (count == 1) Assert.Equal(clickedTop, frontBatch.Single()); // 1-tile == the clicked top

                if (++guard > 32) break;
            }
            Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        }
    }

    // ── Vasquez rev2 — tombstone (outside pickup) vs len-1 (inside pickup) ──────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "SC4-final")]
    public void Pickup_IsNullTombstone_OutsidePickupPhases_And_Len1_Inside()
    {
        // Vasquez rev2 — the live "pickup.targetSlots length 0" was a `pickup.current=null`
        // TOMBSTONE emitted OUTSIDE any pickup phase (a manual game parked in RollingDice
        // because a bot dealer never rolled; also AwaitingDiscard and every auto snapshot) —
        // NOT an empty targetSlots inside a pickup phase. This regression pins the distinction:
        // outside a pickup phase the single `pickup` entry carries a NULL value; inside a pickup
        // phase it is present with EXACTLY ONE targetSlot and no target ids/handles.

        // (a) RollingDice (manual, pre-roll — the state a bot-dealer game was stuck in): tombstone.
        var (rolling, _) = ChangshaGameStateMachine.CreateGame(seed: 77, botSeatIndexes: null);
        rolling.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.StartGame(rolling);
        Assert.Equal(ChangshaPhase.RollingDice, rolling.Phase);
        AssertPickupTombstone(rolling);

        // (b) A pickup phase: present, exactly one target, no ids/handles.
        var pickup = ManualDealAt(dealer: 0, sum: 7, seed: 77);
        Assert.True(ChangshaGameStateMachine.IsPickupPhase(pickup.Phase));
        AssertPickupPresentSingleTarget(pickup);

        // (c) AwaitingDiscard (deal complete): tombstone again.
        var done = ManualDealAt(dealer: 0, sum: 7, seed: 77);
        var guard = 0;
        while (ChangshaGameStateMachine.IsPickupPhase(done.Phase) && guard++ < 32)
        {
            var picker = done.PickupSeatIndex ?? done.DealerSeatIndex;
            ChangshaGameStateMachine.TakeTilesFromWall(done, picker, ChangshaGameStateMachine.ExpectedPickupCount(done.Phase));
        }
        Assert.Equal(ChangshaPhase.AwaitingDiscard, done.Phase);
        AssertPickupTombstone(done);
    }

    private static void AssertPickupTombstone(ChangshaGameState state)
    {
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: state.DealerSeatIndex).ToList();
        var entry = Assert.Single(entries.Where(e => e.Kind == "pickup"));
        Assert.Equal((object)"current", entry.Key);
        Assert.Null(entry.Value); // explicit null tombstone — NOT an empty-targetSlots payload
    }

    private static void AssertPickupPresentSingleTarget(ChangshaGameState state)
    {
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: state.DealerSeatIndex).ToList();
        var entry = Assert.Single(entries.Where(e => e.Kind == "pickup" && e.Value is not null));
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(entry.Value, AutotableJson.Options));
        var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Single(slots);
        Assert.False(doc.RootElement.TryGetProperty("targetTileIds", out _));
        Assert.False(doc.RootElement.TryGetProperty("targetHandles", out _));
    }
}
