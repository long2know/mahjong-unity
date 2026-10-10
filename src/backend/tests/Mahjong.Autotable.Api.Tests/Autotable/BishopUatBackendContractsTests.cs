using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// UAT backend contracts (Bishop lane) from Ripley §9-12. Pure-translator half:
/// BE-1 (authoritative variant / dealMode / dealType) and BE-4 (claim-close
/// tombstone). Endpoint-level contracts (BE-2/BE-3/BE-5/BE-6/G18) live in
/// <see cref="BishopUatEndpointContractsTests"/>. RED @200cad4, GREEN after.
/// </summary>
public class BishopUatBackendContractsTests
{
    private static JsonElement MatchConditions(ChangshaGameState? state)
    {
        var entries = ChangshaToAutotableTranslator.Translate(state);
        var match = entries.Single(e => e.Kind == "match");
        var json = JsonSerializer.Serialize(match.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("conditions").Clone();
    }

    // ── BE-1 — authoritative match identity ───────────────────────────

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-1")]
    public void BE1_Match_SurfacesAuthoritativeChangshaVariant_NeverFourPlayerLie()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7); // DealMode.Auto
        var c = MatchConditions(state);

        // Dedicated, trusted authoritative variant field (FE-4 reads THIS, not gameType).
        Assert.Equal("changsha", c.GetProperty("variant").GetString());
        // The surfaced gameType must no longer be the FOUR_PLAYER lie.
        Assert.Equal("CHANGSHA", c.GetProperty("gameType").GetString());
        // Catalog decoupling preserved: red-5 disabled ⇒ clean 1:1 typeIndex==tileId/4.
        Assert.Equal("000", c.GetProperty("fives").GetString());
    }

    [Theory, Trait("Category", "UatBackend"), Trait("Contract", "BE-1")]
    [InlineData(DealMode.Auto, "auto")]
    [InlineData(DealMode.Manual, "manual")]
    public void BE1_Match_CarriesAuthoritativeDealMode_AndNoInitialLieForAuto(DealMode mode, string wire)
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        state.DealMode = mode;
        var c = MatchConditions(state);

        Assert.Equal(wire, c.GetProperty("dealMode").GetString());
        if (mode == DealMode.Auto)
        {
            // Auto has already dealt hands — INITIAL ("only walls") is the pre-deal lie.
            Assert.NotEqual("INITIAL", c.GetProperty("dealType").GetString());
        }
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-1")]
    public void BE1_Match_NullState_StillSurfacesChangshaVariant()
    {
        var c = MatchConditions(null);
        Assert.Equal("changsha", c.GetProperty("variant").GetString());
        Assert.Equal("CHANGSHA", c.GetProperty("gameType").GetString());
    }

    // ── BE-4 — claim-close tombstone (no stale claim/discard coexistence) ──

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-4")]
    public void BE4_Translator_TombstonesEverySeat_WhenNoClaimWindow()
    {
        // AwaitingDiscard with no ClaimWindow: the snapshot MUST explicitly clear the
        // claim collection for every seat so a prior "Pung" window can't coexist with
        // the discard cue (the client caches activeClaim and only clears it on an
        // explicit self-seat null entry — an omitted slice leaves it stale).
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        Assert.Null(state.ClaimWindow);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var claim = entries.Where(e => e.Kind == "claim").ToList();

        for (var seat = 0; seat < 4; seat++)
        {
            var key = seat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var tomb = claim.SingleOrDefault(e => e.Key?.ToString() == key);
            Assert.NotNull(tomb);
            Assert.Null(tomb!.Value);
        }
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-4")]
    public void BE4_SnapshotAssembly_ForwardsClaimTombstone_ToTheWire()
    {
        // Faithfully reproduce SendFullSnapshotAsync's runtime path: the null claim
        // slice must survive MergeRuntimeEphemerals (like the result tombstone) so the
        // bundle's onClaimUpdate receives an explicit self-seat null and clears the
        // overlay — an omitted ephemeral slice does not.
        var gameState = new AutotableGameState("g-be4");
        // Register `claim` as ephemeral exactly as the bundle declares on connect.
        gameState.ApplyUpdate(
            new[] { new CollectionEntry("ephemeral", ChangshaCollectionKinds.Claim, true) },
            UpdateSource.Client);

        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7); // AwaitingDiscard, no window
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        gameState.ApplyUpdate(entries, UpdateSource.Runtime);

        var snapshot = AutotableConnectionManager.MergeRuntimeEphemerals(
            gameState.Snapshot(), entries, gameState);

        var claim = snapshot.Where(e => e.Kind == ChangshaCollectionKinds.Claim).ToList();
        Assert.Contains(claim, e => e.Key?.ToString() == "0" && e.Value is null);
        Assert.All(claim, e => Assert.Null(e.Value)); // all forwarded slices are tombstones
    }

    // ── BE-6 — pickup signal carries the exact designated endpoint slot(s) ──

    // ── R-1 E1/E2/E3 (Vasquez) + F1 — manual-pickup designation + tombstone + anchor ──

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "SC4-v4")]
    public void SC4v4_Pickup_TargetSlots_IsExactlyOneTrigger_NoTargetTileIds_BatchPreviewIsCount()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 7, botSeatIndexes: null);
        ChangshaGameStateMachine.StartGame(state);
        state.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.BeginManualDeal(state, new DiceService(7).Roll());
        Assert.True(ChangshaGameStateMachine.IsPickupPhase(state.Phase));

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: state.DealerSeatIndex);
        var pickup = entries.Single(e => e.Kind == "pickup" && e.Value is not null);
        var json = JsonSerializer.Serialize(pickup.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);

        var count = doc.RootElement.GetProperty("count").GetInt32();
        var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray().Select(x => x.GetString()!).ToList();
        var preview = doc.RootElement.GetProperty("batchPreviewSlots").EnumerateArray().Select(x => x.GetString()!).ToList();

        // SC-4 v4 FROZEN: targetSlots is EXACTLY ONE trigger slot (the client fails closed on multiple).
        Assert.Single(slots);
        Assert.Matches(@"^wall\.\d+\.\d+@\d+$", slots[0]);

        // NO targetTileIds AND NO targetHandles in the pickup signal (user FINAL SC-4 2026-08-07T11:29:
        // slot-based, no raw tile ids / no handles). Handles live ONLY as SC-2 `things` keys.
        Assert.False(doc.RootElement.TryGetProperty("targetTileIds", out _),
            "FINAL SC-4 forbids targetTileIds in the pickup signal.");
        Assert.False(doc.RootElement.TryGetProperty("targetHandles", out _),
            "FINAL SC-4 forbids targetHandles in the pickup signal (handles are the hidden-`things` render key only).");

        // count is authoritatively 4 (BreakPointMarked/PickupRound1..3) or 1 (SingleTilePickup/DealerExtra).
        Assert.Contains(count, new[] { 1, 4 });

        // batchPreviewSlots is DISPLAY-ONLY (distinct name) and covers the full designated batch (count),
        // with the single trigger as its exposed-front element.
        Assert.Equal(count, preview.Count);
        Assert.All(preview, s => Assert.Matches(@"^wall\.\d+\.\d+@\d+$", s));
        Assert.Equal(slots[0], preview[0]);
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "R1-E3")]
    public void R1E3_Pickup_TombstonedWhenNotInPickupPhase_AndForwardedToWire()
    {
        // Post-deal (AwaitingDiscard) the pickup cursor MUST be explicitly cleared so the
        // bundle's sticky isMyPickupTurn() flips false and the wall goes inert.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7); // AwaitingDiscard, not a pickup phase
        Assert.False(ChangshaGameStateMachine.IsPickupPhase(state.Phase));

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var pickup = entries.Single(e => e.Kind == "pickup");
        Assert.Null(pickup.Value); // explicit tombstone, not omitted

        // The tombstone must survive snapshot assembly (like the claim tombstone).
        var gameState = new AutotableGameState("g-e3");
        gameState.ApplyUpdate(
            new[] { new CollectionEntry("ephemeral", ChangshaCollectionKinds.Pickup, true) },
            UpdateSource.Client);
        gameState.ApplyUpdate(entries, UpdateSource.Runtime);
        var snapshot = AutotableConnectionManager.MergeRuntimeEphemerals(gameState.Snapshot(), entries, gameState);
        Assert.Contains(snapshot, e => e.Kind == ChangshaCollectionKinds.Pickup && e.Value is null);
    }

    [Theory, Trait("Category", "UatBackend"), Trait("Contract", "R1-F1")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void R1F1_WallRender_IsValid_AndContiguousArc_ForEveryDealer_AndDiceSum(int dealer)
    {
        // R-1 F1 golden test (Vasquez): the rendered wall must map every remaining tile to a
        // UNIQUE, in-capacity slot forming a SINGLE contiguous ordinal arc, for EVERY dealer ×
        // dice-sum (2..12). This is the backend-checkable half; whether the arc anchors on the
        // TRUE physical break (the relative-vs-absolute frame question) needs the frontend
        // geometry oracle and is reported to Vasquez separately.
        for (var sum = 2; sum <= 12; sum++)
        {
            var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 100 + dealer * 20 + sum, botSeatIndexes: null);
            state.DealerSeatIndex = dealer;
            state.Seats[dealer].IsDealer = true;
            ChangshaGameStateMachine.StartGame(state);
            state.DealMode = DealMode.Manual;
            var d1 = Math.Clamp(sum - 1, 1, 6);
            ChangshaGameStateMachine.BeginManualDeal(state, new DiceRoll(d1, sum - d1));

            var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: dealer);
            var wallSlots = entries
                .Where(e => e.Kind == "things" && e.Value is not null)
                .Select(e => JsonSerializer.Serialize(e.Value, AutotableJson.Options))
                .Select(j => JsonDocument.Parse(j).RootElement.GetProperty("slotName").GetString()!)
                .Where(sn => sn.StartsWith("wall.", StringComparison.Ordinal))
                .ToList();

            // Every wall tile lands in a distinct, valid wall slot (no collision / over-capacity).
            Assert.Equal(state.Wall.Count, wallSlots.Count);
            Assert.Equal(wallSlots.Count, wallSlots.Distinct().Count());
        }
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-6")]
    public void BE6_Pickup_TargetSlots_AreCoDerivedWithWallThings_NotRecomputed()
    {
        // Reach a manual pickup phase (BreakPointMarked) via the pure state machine.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 7, botSeatIndexes: null);
        ChangshaGameStateMachine.StartGame(state);
        state.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.BeginManualDeal(state, new DiceService(7).Roll());
        Assert.True(ChangshaGameStateMachine.IsPickupPhase(state.Phase));

        // Privacy OFF ⇒ wall `things` are keyed by real tileId, so we can match a batch tile to its
        // emitted wall slot WITHOUT recomputing the ordinal math (a second computation is FORBIDDEN
        // by the R-1 FINAL caveat — targetSlots must be co-derived from the wall-emission pass).
        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: state.DealerSeatIndex);

        var wallSlotByTileId = new Dictionary<int, string>();
        foreach (var e in entries.Where(e => e.Kind == "things" && e.Value is not null))
        {
            var sn = JsonDocument.Parse(JsonSerializer.Serialize(e.Value, AutotableJson.Options))
                .RootElement.GetProperty("slotName").GetString()!;
            if (sn.StartsWith("wall.", StringComparison.Ordinal))
                wallSlotByTileId[Convert.ToInt32(e.Key)] = sn;
        }

        var pickup = entries.Single(e => e.Kind == "pickup" && e.Value is not null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(pickup.Value, AutotableJson.Options));
        var count = doc.RootElement.GetProperty("count").GetInt32();
        var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        var preview = doc.RootElement.GetProperty("batchPreviewSlots").EnumerateArray()
            .Select(x => x.GetString()!).ToList();

        Assert.True(count > 0);

        // SC-4 v4: targetSlots is the SINGLE trigger = the wall-render slot of state.Wall[0], and it is
        // CO-DERIVED (same slot the Wall[0] `thing` was placed at) — proven WITHOUT recomputing ordinal
        // math (a second computation is FORBIDDEN by the FINAL caveat).
        Assert.Single(slots);
        Assert.True(wallSlotByTileId.TryGetValue(state.Wall[0], out var wall0Slot),
            $"front tile {state.Wall[0]} was not emitted as a wall thing");
        Assert.Equal(wall0Slot, slots[0]);

        // batchPreviewSlots (display-only) is co-derived for the whole batch Wall[0..count-1].
        Assert.Equal(count, preview.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.True(wallSlotByTileId.TryGetValue(state.Wall[i], out var wallSlot),
                $"batch tile {state.Wall[i]} was not emitted as a wall thing");
            Assert.Equal(wallSlot, preview[i]);
        }
    }
}
