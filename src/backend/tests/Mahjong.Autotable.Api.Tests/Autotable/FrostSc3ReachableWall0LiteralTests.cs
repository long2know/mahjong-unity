using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// FINAL SC-3 (Frost gate + Vasquez F2 17-state emission literals, 2026-08-07) — the ANTI-CIRCULAR
/// literal-bound emission oracle that SC-4 (<see cref="BishopUatPickupTargetSlotsTests"/>) cannot
/// provide. SC-4 asserts <c>targetSlots[0] == the CO-EMITTED wall-thing slot of Wall[0]</c>: both
/// sides flow from <c>ComputeWallFrontSlots</c>, so a shared ordinal bug corrupts both identically and
/// still passes (necessary, not sufficient). This suite instead binds the EMITTED
/// <c>pickup.targetSlots[0]</c> — read from a real <see cref="ChangshaToAutotableTranslator.Translate"/>
/// over the real manual-ceremony state — to Vasquez's HARDCODED literal string keyed by the front-draw
/// index <c>fd = 108 - Wall.Count - WallBackDrawn</c>. The RHS is NEVER a
/// <c>WallSlot(WallOrdinalToSlot(...))</c> / <c>ComputeWallFrontSlots</c> recompute; it is the literal
/// from the table. This is deal-PINNED to dealer 0 / dice sum 5 (BreakPoint.TileIndex == 18); any other
/// (dealer, sum, TileIndex) shifts the literals — the pin assertion below fails loudly so the state is
/// surfaced rather than silently transcribed.
///
/// Baseline: RED on the @200cad4 pre-F2 slot map (bottom-first <c>o % 2</c> ⇒ every emitted slot differs
/// from the literal, and <c>targetSlots</c> is absent entirely); GREEN on the F2-fixed image (top-first
/// <c>1 - (o % 2)</c>) INCLUDING the fd49/fd51 consumed-top rows.
/// </summary>
public class FrostSc3ReachableWall0LiteralTests
{
    private const int TotalTiles = 108;

    // ── Vasquez F2 17-state emission literals (dealer0 / sum5, BreakPoint.TileIndex==18) ──
    // VERBATIM from .squad/decisions/inbox/vasquez-f2-17state-emission-literals.md — do NOT regenerate,
    // do NOT recompute via WallOrdinalToSlot. RHS is the hardcoded oracle; LHS is the real emission.
    private static readonly IReadOnlyDictionary<int, string> ExpectedReachableWall0 = new Dictionary<int, string>
    {
        [0] = "wall.9.1@0", [4] = "wall.11.1@0", [8] = "wall.13.1@0",
        [12] = "wall.1.1@1", [16] = "wall.3.1@1", [20] = "wall.5.1@1", [24] = "wall.7.1@1",
        [28] = "wall.9.1@1", [32] = "wall.11.1@1", [36] = "wall.13.1@1",
        [40] = "wall.1.1@2", [44] = "wall.3.1@2",
        [48] = "wall.5.1@2", [49] = "wall.5.0@2", [50] = "wall.6.1@2", [51] = "wall.6.0@2",
        [52] = "wall.7.1@2",
    };

    // (b) fd49/fd51 — the CONSUMED-top sibling that must be ABSENT from wall things (up-link-empty
    // catcher: its top was drawn, so the frontier fell through to the layer-0 bottom).
    private static readonly IReadOnlyDictionary<int, string> ExpectedAbsentSibling = new Dictionary<int, string>
    {
        [49] = "wall.5.1@2", [51] = "wall.6.1@2",
    };

    // (b) fd48/fd50 — the occluded-bottom sibling that must be PRESENT in wall things (opaque, undrawn)
    // AND != targetSlots[0]. NOT absent — inverting this false-REDs the correct top-first emission.
    private static readonly IReadOnlyDictionary<int, string> ExpectedOccludedPresent = new Dictionary<int, string>
    {
        [48] = "wall.5.0@2", [50] = "wall.6.0@2",
    };

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

    /// <summary>Set of emitted wall <c>things</c> slot names (privacy off ⇒ key == tileId).</summary>
    private static HashSet<string> WallThingSlotNames(IEnumerable<CollectionEntry> entries)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries.Where(e => e.Kind == "things" && e.Value is not null))
        {
            var sn = JsonDocument.Parse(JsonSerializer.Serialize(e.Value, AutotableJson.Options))
                .RootElement.GetProperty("slotName").GetString()!;
            if (sn.StartsWith("wall.", StringComparison.Ordinal))
                set.Add(sn);
        }
        return set;
    }

    private static string EmittedTargetSlot0(IEnumerable<CollectionEntry> entries)
    {
        var pickup = entries.Single(e => e.Kind == "pickup" && e.Value is not null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(pickup.Value, AutotableJson.Options));
        var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        Assert.NotEmpty(slots); // single trigger slot; SC-4 pins length == 1
        return slots[0];
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "SC3")]
    public void ReachableWall0_EmissionLiterals_Dealer0Sum5_AntiCircular()
    {
        var state = ManualDealAt(dealer: 0, sum: 5, seed: 42);

        // Deal PIN — the literals are pinned to BreakPoint.TileIndex == 18. If this ever changes,
        // surface the real state to Vasquez rather than transcribing the table blindly.
        Assert.NotNull(state.BreakPoint);
        Assert.Equal(18, state.BreakPoint!.Value.TileIndex);

        var fdAsserted = new List<int>();
        var guard = 0;
        while (ChangshaGameStateMachine.IsPickupPhase(state.Phase))
        {
            var picker = state.PickupSeatIndex ?? state.DealerSeatIndex;
            var fd = TotalTiles - state.Wall.Count - state.WallBackDrawn; // real state, never hardcoded

            if (ExpectedReachableWall0.TryGetValue(fd, out var expected))
            {
                var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: picker);
                var target0 = EmittedTargetSlot0(entries);
                var wallSlots = WallThingSlotNames(entries);

                // #1 — PRESENT reachable frontier == literal oracle (top-first). Anti-circular: RHS is
                // the hardcoded string, LHS is the real emission.
                Assert.Equal(expected, target0);

                // #(b) fd49/fd51 — consumed top is gone from the wall things.
                if (ExpectedAbsentSibling.TryGetValue(fd, out var drawnTop))
                {
                    Assert.NotEqual(drawnTop, target0);
                    Assert.DoesNotContain(drawnTop, wallSlots);
                }

                // #(b) fd48/fd50 — occluded bottom is still present (opaque) but NOT the frontier.
                if (ExpectedOccludedPresent.TryGetValue(fd, out var occludedBottom))
                {
                    Assert.NotEqual(occludedBottom, target0);
                    Assert.Contains(occludedBottom, wallSlots);
                }

                fdAsserted.Add(fd);
            }

            var take = ChangshaGameStateMachine.ExpectedPickupCount(state.Phase);
            ChangshaGameStateMachine.TakeTilesFromWall(state, picker, take);

            if (++guard > 32) break; // safety — the ceremony is 18 picks
        }

        // Non-vacuity: the walk actually reached and asserted every one of Vasquez's 17 literal states,
        // including the fd49/fd51 consumed-top rows that distinguish top-first from bottom-first.
        Assert.Equal(ExpectedReachableWall0.Keys.OrderBy(k => k), fdAsserted.OrderBy(k => k));
        Assert.Equal(17, fdAsserted.Count);
        Assert.Contains(49, fdAsserted);
        Assert.Contains(51, fdAsserted);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
    }
}
