using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Stephen 2026-05-27 face-down-walls + manual-dealing-ceremony
/// directive (<c>.squad/decisions/inbox/copilot-directive-2026-05-27T2127Z-face-down-walls.md</c>).
///
/// <para><b>Problem:</b> the autotable bundle was rendering tile FACES at game start
/// (visible from the wall positions or animating into hand positions) instead of
/// canonical 4 face-down walls. Per Changsha rules (MahjongPros §"Dealing the
/// Hand" / Baidu §"摸牌顺序"): tiles MUST start face-down in 4 walls, dealer rolls
/// 2d6, players counter-clockwise take 4 tiles each × 3 rounds → 1 tile each round
/// → dealer takes 1 extra (14/13/13/13).</para>
///
/// <para>Hicks shipped the frontend half (4d9e3ce — restricted the bundle's
/// privacy-fallback rotation coercion to <c>hand</c> slots only; set local
/// <c>DealType.INITIAL</c> when <c>?dealMode=manual</c>). This file is Bishop's
/// backend half — the translator MUST emit authoritative face-down wall things
/// for ALL 108 tiles in pre-deal phases so the server snapshot is the single
/// source of truth, and the state machine MUST drive the pickup ceremony per
/// the Changsha rules.</para>
///
/// <para><b>This file pins:</b>
/// <list type="number">
///   <item><b>Translator (face-down walls):</b> at <c>Seating</c> and manual-mode
///   <c>RollingDice</c> the translator emits all 108 tiles in canonical 4-wall slots
///   face-down (synthetic placement, since the authoritative wall isn't shuffled
///   yet). Hands, melds, discards must be empty in the wire snapshot.</item>
///   <item><b>State machine (pickup ceremony):</b> RollDice → BreakPointMarked →
///   3 × PickupRound (4 tiles each, CCW from dealer) → SingleTilePickup (1 tile each)
///   → DealerExtra (1 tile to dealer) → AwaitingDiscard, final hands 14/13/13/13.</item>
///   <item><b>Auto-mode parity:</b> <c>DealMode.Auto</c> keeps the legacy fast-deal
///   path (no pickup phases reached, hands populated directly).</item>
/// </list>
/// </para>
///
/// <para><b>Cross-references:</b>
/// <list type="bullet">
///   <item><see cref="ChangshaToAutotableTranslator"/> (Bishop's lane)</item>
///   <item><see cref="ChangshaGameStateMachine.BeginManualDeal"/> +
///   <see cref="ChangshaGameStateMachine.TakeTilesFromWall"/></item>
///   <item><see cref="AutotableSlotMap"/> (14/14/13/13 canonical wall split)</item>
/// </list>
/// </para>
/// </summary>
public class ManualDealCeremonyTests
{
    // ── Translator: face-down wall emission ──────────────────────────

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Seating_Emits_108_FaceDown_Wall_Things_NoHandSlots_NoDiscards()
    {
        // Fresh CreateGame → state.Phase = Seating, state.Wall = [], all hands empty.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 17);

        var things = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things").ToList();

        Assert.Equal(108, things.Count);

        var distinctIds = things.Select(e => Convert.ToInt32(e.Key)).Distinct().ToList();
        Assert.Equal(108, distinctIds.Count);
        Assert.Equal(0, distinctIds.Min());
        Assert.Equal(107, distinctIds.Max());

        foreach (var entry in things)
        {
            var slot = ExtractSlotName(entry.Value!);
            Assert.StartsWith("wall.", slot);
            Assert.Equal(0, ExtractRotationIndex(entry.Value!));  // WallRotFaceDown
        }

        Assert.DoesNotContain(things, e => ExtractSlotName(e.Value!).StartsWith("hand."));
        Assert.DoesNotContain(things, e => ExtractSlotName(e.Value!).StartsWith("discard."));
        Assert.DoesNotContain(things, e => ExtractSlotName(e.Value!).StartsWith("meld."));
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void RollingDice_Manual_BeforeRoll_StillRenders_108_FaceDown_Walls()
    {
        // After StartGame in Manual mode, state sits at RollingDice with wall empty
        // (BeginManualDeal hasn't fired yet). The translator MUST still authoritatively
        // place 108 face-down wall tiles so the bundle's local "HANDS" deal animation
        // is overridden by the snapshot.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 17);
        state.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.StartGame(state);
        Assert.Equal(ChangshaPhase.RollingDice, state.Phase);
        Assert.Empty(state.Wall);
        Assert.All(state.Hands, h => Assert.Empty(h.ConcealedTiles));

        var things = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things").ToList();
        Assert.Equal(108, things.Count);
        Assert.All(things, e => Assert.Equal(0, ExtractRotationIndex(e.Value!)));
        Assert.All(things, e => Assert.StartsWith("wall.", ExtractSlotName(e.Value!)));
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void SyntheticWall_Uses_Canonical_14_14_13_13_Slot_Layout()
    {
        // Synthetic-wall placement must match the canonical Changsha 14/14/13/13
        // split: seats 0+1 get 28 tiles each, seats 2+3 get 26 each, total 108.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 99);

        var things = ChangshaToAutotableTranslator.Translate(state, viewerSeat: null)
            .Where(e => e.Kind == "things").ToList();

        var bySeat = new int[4];
        foreach (var e in things)
        {
            var slot = ExtractSlotName(e.Value!);
            Assert.StartsWith("wall.", slot);
            var seat = int.Parse(slot.Split('@')[1]);
            bySeat[seat]++;
        }

        Assert.Equal(28, bySeat[0]);
        Assert.Equal(28, bySeat[1]);
        Assert.Equal(26, bySeat[2]);
        Assert.Equal(26, bySeat[3]);
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void BreakPointMarked_RendersFullFaceDownWall_AfterBeginManualDeal()
    {
        // After the dealer rolls and BeginManualDeal materializes the shuffled
        // 108-tile wall (rotated to the break point), every tile is still rendered
        // face-down in a canonical wall slot — no hand-slot tiles yet.
        var state = ArrangeManualBreakPointMarked(seed: 21);

        var things = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things").ToList();
        Assert.Equal(108, things.Count);
        Assert.All(things, e => Assert.StartsWith("wall.", ExtractSlotName(e.Value!)));
        Assert.All(things, e => Assert.Equal(0, ExtractRotationIndex(e.Value!)));
    }

    // ── State machine: pickup ceremony progression ────────────────────

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void RollDice_Transitions_RollingDice_To_BreakPointMarked_WithBreakPointSet()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 5);
        state.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.StartGame(state);

        ChangshaGameStateMachine.BeginManualDeal(state, new DiceRoll(3, 4));

        Assert.Equal(ChangshaPhase.BreakPointMarked, state.Phase);
        Assert.NotNull(state.BreakPoint);
        Assert.Equal(108, state.Wall.Count);
        Assert.All(state.Hands, h => Assert.Empty(h.ConcealedTiles));
        Assert.Equal(state.DealerSeatIndex, state.PickupSeatIndex);
        Assert.Equal(7, state.LastDiceRoll!.Value.Sum);
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_Round1_FirstTake_AdvancesCursor_ToNextCcwSeat()
    {
        // The first 4-tile pickup is taken by the dealer (seat 0). After advance,
        // the cursor MUST rotate to seat 1 (per AdvancePickupCursor: PickupSeatIndex
        // = (DealerSeatIndex + PickupRoundIndex) % 4). Phase moves from
        // BreakPointMarked into PickupRound1 (proper) for the remaining 3 seats.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        Assert.Equal(0, state.PickupSeatIndex);

        ChangshaGameStateMachine.TakeTilesFromWall(state, seatIndex: 0, requestedCount: 4);

        Assert.Equal(ChangshaPhase.PickupRound1, state.Phase);
        Assert.Equal(1, state.PickupSeatIndex);
        Assert.Equal(4, state.Hands[0].ConcealedTiles.Count);
        Assert.Empty(state.Hands[1].ConcealedTiles);
        Assert.Equal(108 - 4, state.Wall.Count);
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_Round1_Complete_Transitions_To_PickupRound2_DealerNext()
    {
        // After all 4 seats have taken 4 tiles in round 1, the phase advances to
        // PickupRound2 and the cursor returns to the dealer for round 2.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        DriveFullRound(state, count: 4);

        Assert.Equal(ChangshaPhase.PickupRound2, state.Phase);
        Assert.Equal(state.DealerSeatIndex, state.PickupSeatIndex);
        Assert.All(state.Hands, h => Assert.Equal(4, h.ConcealedTiles.Count));
        Assert.Equal(108 - 16, state.Wall.Count);
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_Round3_Complete_Transitions_To_SingleTilePickup()
    {
        // Drive 3 × 4 = 12 picks per seat. Phase MUST advance to SingleTilePickup
        // with cursor back at dealer and expected pickup count == 1.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        DriveFullRound(state, count: 4);  // round 1 done → PickupRound2
        DriveFullRound(state, count: 4);  // round 2 done → PickupRound3
        DriveFullRound(state, count: 4);  // round 3 done → SingleTilePickup

        Assert.Equal(ChangshaPhase.SingleTilePickup, state.Phase);
        Assert.All(state.Hands, h => Assert.Equal(12, h.ConcealedTiles.Count));
        Assert.Equal(1, ChangshaGameStateMachine.ExpectedPickupCount(state.Phase));
        Assert.Equal(state.DealerSeatIndex, state.PickupSeatIndex);
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_SingleTileRound_Complete_Transitions_To_DealerExtra()
    {
        // After 4 × 1 = 4 single-tile picks, every seat has 13. The next pickup is
        // exclusively the dealer's 14th tile.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        DriveFullRound(state, count: 4);
        DriveFullRound(state, count: 4);
        DriveFullRound(state, count: 4);
        DriveFullRound(state, count: 1);

        Assert.Equal(ChangshaPhase.DealerExtra, state.Phase);
        Assert.All(state.Hands, h => Assert.Equal(13, h.ConcealedTiles.Count));
        Assert.Equal(state.DealerSeatIndex, state.PickupSeatIndex);
        Assert.Equal(1, ChangshaGameStateMachine.ExpectedPickupCount(state.Phase));
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_DealerExtra_Complete_Transitions_To_AwaitingDiscard_14_13_13_13()
    {
        // Dealer takes the 14th tile. State machine lands in AwaitingDiscard with
        // canonical 14/13/13/13 hands, dealer must discard next.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        DriveFullRound(state, count: 4);
        DriveFullRound(state, count: 4);
        DriveFullRound(state, count: 4);
        DriveFullRound(state, count: 1);
        ChangshaGameStateMachine.TakeTilesFromWall(state, state.DealerSeatIndex, requestedCount: 1);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(14, state.Hands[state.DealerSeatIndex].ConcealedTiles.Count);
        for (var i = 0; i < 4; i++)
        {
            if (i == state.DealerSeatIndex) continue;
            Assert.Equal(13, state.Hands[i].ConcealedTiles.Count);
        }
        Assert.Equal(state.DealerSeatIndex, state.ActiveSeatIndex);
        Assert.Null(state.PickupSeatIndex);
        // Total tiles dealt = 14 + 13 + 13 + 13 = 53; wall has 108 - 53 = 55 left.
        Assert.Equal(55, state.Wall.Count);
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_WrongSeat_Throws_InvalidOperationException()
    {
        // Seat-turn validation: only the active pickup seat may take tiles.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        Assert.Equal(0, state.PickupSeatIndex);

        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.TakeTilesFromWall(state, seatIndex: 2, requestedCount: 4));
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_WrongCount_Throws_InvalidOperationException()
    {
        // Phase-correct count validation: BreakPointMarked / PickupRound* expect 4.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.TakeTilesFromWall(state, seatIndex: 0, requestedCount: 1));
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void Pickup_MidCeremony_Translator_RendersWallShrinking_HandsGrowing()
    {
        // After two seats complete round 1, the translator must show 100 face-down
        // wall tiles + 8 hand tiles (face-down for foreign seats, face-up for
        // viewer seat 0). No hand or discard tile may be rendered face-up for
        // non-viewer seats.
        var state = ArrangeManualBreakPointMarked(seed: 31);
        ChangshaGameStateMachine.TakeTilesFromWall(state, 0, 4);
        ChangshaGameStateMachine.TakeTilesFromWall(state, 1, 4);

        var things = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things").ToList();

        var wallCount = things.Count(e => ExtractSlotName(e.Value!).StartsWith("wall."));
        var handCount = things.Count(e => ExtractSlotName(e.Value!).StartsWith("hand."));
        Assert.Equal(100, wallCount);
        Assert.Equal(8, handCount);

        // Wall tiles all face-down.
        foreach (var e in things.Where(e => ExtractSlotName(e.Value!).StartsWith("wall.")))
            Assert.Equal(0, ExtractRotationIndex(e.Value!));

        // Seat 0 (viewer) hand tiles face-up; seat 1 hand tiles face-down at translator layer.
        foreach (var e in things.Where(e => ExtractSlotName(e.Value!).EndsWith("@0") && ExtractSlotName(e.Value!).StartsWith("hand.")))
            Assert.Equal(1, ExtractRotationIndex(e.Value!));  // HandRotFaceUp
        foreach (var e in things.Where(e => ExtractSlotName(e.Value!).EndsWith("@1") && ExtractSlotName(e.Value!).StartsWith("hand.")))
            Assert.Equal(2, ExtractRotationIndex(e.Value!));  // HandRotFaceDown
    }

    // ── Auto-mode parity ──────────────────────────────────────────────

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void AutoMode_FastDealPath_LandsAtAwaitingDiscard_With_14_13_13_13_NoPickupPhases()
    {
        // Legacy auto-deal path must NOT regress: StartGame + RollDice + Deal lands
        // straight at AwaitingDiscard with canonical hands, never hitting any of
        // the pickup phases.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 41);
        Assert.Equal(DealMode.Auto, state.DealMode);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(41));
        ChangshaGameStateMachine.Deal(state);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(14, state.Hands[state.DealerSeatIndex].ConcealedTiles.Count);
        for (var i = 0; i < 4; i++)
        {
            if (i == state.DealerSeatIndex) continue;
            Assert.Equal(13, state.Hands[i].ConcealedTiles.Count);
        }
        Assert.Null(state.PickupSeatIndex);
        // 108 - (14 + 13×3) = 55 in wall.
        Assert.Equal(55, state.Wall.Count);

        // Translator path matches: no synthetic-wall override; the actual 55-tile
        // wall is rendered.
        var wallCount = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things")
            .Count(e => ExtractSlotName(e.Value!).StartsWith("wall."));
        Assert.Equal(55, wallCount);
    }

    [Fact, Trait("Category", "ManualDealCeremony")]
    public void EndHand_WithEmptyWall_DoesNotSynthesizeFaceDownWall()
    {
        // The synthetic-wall fallback must gate strictly on Seating + RollingDice.
        // Other phases with an empty wall (WallExhausted, EndHand, etc.) must NOT
        // erroneously add 108 phantom wall tiles. Compose a synthetic EndHand
        // state with empty wall but non-empty hands to prove the gate fires.
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 67);
        state.Phase = ChangshaPhase.EndHand;
        state.Wall = new List<int>();  // exhausted

        var wallCount = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0)
            .Where(e => e.Kind == "things")
            .Count(e => ExtractSlotName(e.Value!).StartsWith("wall."));
        Assert.Equal(0, wallCount);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static ChangshaGameState ArrangeManualBreakPointMarked(int seed)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed);
        state.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.BeginManualDeal(state, new DiceRoll(2, 5));
        return state;
    }

    private static void DriveFullRound(ChangshaGameState state, int count)
    {
        for (var n = 0; n < 4; n++)
        {
            var seat = state.PickupSeatIndex
                ?? throw new InvalidOperationException("PickupSeatIndex is null mid-round.");
            ChangshaGameStateMachine.TakeTilesFromWall(state, seat, count);
        }
    }

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
}
