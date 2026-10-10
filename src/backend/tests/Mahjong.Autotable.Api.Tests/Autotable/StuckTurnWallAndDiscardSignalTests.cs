using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Regression suite for the "stuck turn" fix (design contracts C-1/C-2/C-3).
///
/// <para><b>C-3 (wall mapping):</b> the remaining live wall must render as ONE physical
/// contiguous arc anchored at the <i>authoritative</i> break — the rules engine's flat
/// <see cref="BreakPointResult.TileIndex"/> measured from the dealer's wall origin —
/// for every dealer seat 0-3, with ordinary front draws consuming forward and Kong
/// replacements consuming the back. The superseded
/// <c>WallBreakOrdinal(wallSeatIndex, stackIndex)</c> re-derived the anchor from the
/// render's absolute wall sizes and drifted one stack off the true break for most
/// dealer-2 rolls (and some rolls at every dealer), so the parametrized mapping tests
/// below FAIL on ddc72e1 and PASS after the transform is corrected.</para>
///
/// <para><b>C-1/C-2 (discard-turn signal):</b> the backend must emit an authoritative
/// <c>turn["current"] = { seat, kind:"discard" }</c> whenever
/// <see cref="ChangshaPhase.AwaitingDiscard"/> holds — covering the dealer's initial 14,
/// an ordinary auto-draw, a Chow/Pung claim, and a Kong replacement, for human and bot
/// seats — and tombstone it (<c>null</c>) on phase exit. ddc72e1 emits no <c>turn</c>
/// collection at all, so those tests FAIL there and PASS after.</para>
///
/// <para>Tests reference only wire string literals and pre-existing public API so they
/// compile against ddc72e1 (proving genuine RED→GREEN across the fix commit).</para>
/// </summary>
public class StuckTurnWallAndDiscardSignalTests
{
    private sealed class FixedDiceService(int die1, int die2) : IDiceService
    {
        public DiceRoll Roll() => new(die1, die2);
    }

    private static int Norm(int ordinal) => ((ordinal % AutotableSlotMap.TotalTiles) + AutotableSlotMap.TotalTiles) % AutotableSlotMap.TotalTiles;

    /// <summary>Render-ring ordinal of the first slot of <paramref name="seat"/>'s wall.</summary>
    private static int SeatWallStart(int seat)
    {
        var start = 0;
        for (var s = 0; s < seat; s++) start += AutotableSlotMap.WallTileCapacity(s);
        return start;
    }

    private static (int seat, int col, int layer) ParseWallSlot(string slot)
    {
        // "wall.{col}.{layer}@{seat}"
        var seat = int.Parse(slot.Split('@')[1]);
        var parts = slot.Split('@')[0].Split('.');
        return (seat, int.Parse(parts[1]), int.Parse(parts[2]));
    }

    private static int WallSlotOrdinal(string slot)
    {
        var (seat, col, layer) = ParseWallSlot(slot);
        // F2 top-first: forward map is ordinal o -> layer 1-(o%2), so the inverse
        // recovering the ordinal from a physical (col, layer) is col*2 + (1 - layer).
        return SeatWallStart(seat) + col * 2 + (1 - layer);
    }

    private static string SlotOf(object? value)
    {
        var json = JsonSerializer.Serialize(value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("slotName").GetString()!;
    }

    /// <summary>Maps every emitted wall tile id → its render-ring ordinal.</summary>
    private static Dictionary<int, int> WallTileOrdinals(IReadOnlyList<CollectionEntry> entries)
    {
        var map = new Dictionary<int, int>();
        foreach (var e in entries)
        {
            if (e.Kind != "things" || e.Key is not int tileId) continue;
            var slot = SlotOf(e.Value);
            if (!slot.StartsWith("wall.")) continue;
            map[tileId] = WallSlotOrdinal(slot);
        }
        return map;
    }

    /// <summary>Locate a singleton Changsha collection entry keyed "current"; returns whether it
    /// was present and its serialized value element (Null kind when tombstoned).</summary>
    private static (bool present, JsonElement value) FindSingleton(IReadOnlyList<CollectionEntry> entries, string kind)
    {
        var entry = entries.FirstOrDefault(e => e.Kind == kind && e.Key is string k && k == "current");
        if (entry is null) return (false, default);
        var json = JsonSerializer.Serialize(entry.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return (true, doc.RootElement.Clone());
    }

    private static ChangshaGameState DealtWithDealer(int dealer, int die1, int die2, int seed = 7)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed);
        state.DealerSeatIndex = dealer;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealer;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new FixedDiceService(die1, die2));
        ChangshaGameStateMachine.Deal(state);
        return state;
    }

    private static (int die1, int die2) DiceForSum(int sum)
    {
        var die1 = sum <= 7 ? 1 : sum - 6;
        return (die1, sum - die1);
    }

    // ── C-3: authoritative break-anchored wall mapping across every dealer ──

    /// <summary>
    /// For every dealer seat and every reachable dice sum, each remaining wall tile must be
    /// emitted at the render ordinal dictated by the rules engine's authoritative flat wall
    /// index — <c>dealerOrigin + BreakPoint.TileIndex + frontDrawn + listIndex</c> — so the
    /// live wall is one contiguous arc from the TRUE break. RED on ddc72e1 (dealer 2 drifts
    /// on 9/11 sums; dealers 0/1/3 drift on some sums).
    /// </summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "C-3")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Wall_RemainingTiles_MapToAuthoritativeBreakOrdinal_ForEveryDealerAndDiceSum(int dealer)
    {
        for (var sum = 2; sum <= 12; sum++)
        {
            var (d1, d2) = DiceForSum(sum);
            var state = DealtWithDealer(dealer, d1, d2);
            var bp = state.BreakPoint!.Value;
            var frontDrawn = AutotableSlotMap.TotalTiles - state.Wall.Count - state.WallBackDrawn;
            var origin = SeatWallStart(dealer);

            var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
            var ordinals = WallTileOrdinals(entries);

            for (var i = 0; i < state.Wall.Count; i++)
            {
                var tileId = state.Wall[i];
                Assert.True(ordinals.TryGetValue(tileId, out var actual),
                    $"dealer {dealer} sum {sum}: wall tile {tileId} (index {i}) was not emitted as a wall slot.");
                var expected = Norm(origin + bp.TileIndex + frontDrawn + i);
                Assert.True(expected == actual,
                    $"dealer {dealer} sum {sum}: wall tile at list index {i} (id {tileId}) rendered at ordinal {actual} " +
                    $"but the authoritative break (tileIndex {bp.TileIndex}, frontDrawn {frontDrawn}, origin {origin}) requires {expected}. " +
                    "The break anchor drifted from the rules-engine wall order.");
            }
        }
    }

    /// <summary>The remaining wall must occupy one contiguous run on the 108-slot ring for every
    /// dealer and dice sum (front-draw depletion never scatters the wall). Holds before and after
    /// the fix, but pins the arc invariant across all dealers.</summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "C-3")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Wall_RemainingTiles_FormSingleContiguousArc_ForEveryDealer(int dealer)
    {
        for (var sum = 2; sum <= 12; sum++)
        {
            var (d1, d2) = DiceForSum(sum);
            var state = DealtWithDealer(dealer, d1, d2);
            var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
            var occupied = new SortedSet<int>(WallTileOrdinals(entries).Values);

            Assert.Equal(state.Wall.Count, occupied.Count);
            var runs = occupied.Count(o => !occupied.Contains(Norm(o - 1)));
            Assert.True(runs == 1,
                $"dealer {dealer} sum {sum}: remaining wall formed {runs} arcs (expected 1 contiguous arc).");
        }
    }

    /// <summary>
    /// A Kong replacement draws from the BACK of the wall (state.WallBackDrawn &gt; 0). The
    /// remaining wall must still anchor at the authoritative break and stay one arc — front
    /// draws consumed the near (break) end, the kong replacement consumed the far end. Uses
    /// dealer 2 (worst-drift seat) so this is RED on ddc72e1.
    /// </summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "C-3")]
    public void Wall_KongReplacement_ConsumesBack_KeepsArcAnchoredAtBreak_Dealer2()
    {
        var state = DealtWithDealer(dealer: 2, die1: 3, die2: 4, seed: 11); // sum 7
        var dealer = state.DealerSeatIndex;

        // Model live play: several ordinary front draws (near/break end) plus two kong
        // replacements from the back (far end) — the rendering-relevant effect of
        // DrawFromFront/DrawFromBack — leaving a contiguous middle arc.
        for (var i = 0; i < 12; i++) state.Wall.RemoveAt(0);
        for (var i = 0; i < 2; i++) { state.Wall.RemoveAt(state.Wall.Count - 1); state.WallBackDrawn++; }

        var bp = state.BreakPoint!.Value;
        var frontDrawn = AutotableSlotMap.TotalTiles - state.Wall.Count - state.WallBackDrawn;
        var origin = SeatWallStart(dealer);

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var ordinals = WallTileOrdinals(entries);
        var occupied = new SortedSet<int>(ordinals.Values);

        for (var i = 0; i < state.Wall.Count; i++)
        {
            var tileId = state.Wall[i];
            Assert.True(ordinals.TryGetValue(tileId, out var actual), $"wall tile {tileId} not emitted.");
            Assert.Equal(Norm(origin + bp.TileIndex + frontDrawn + i), actual);
        }
        var runs = occupied.Count(o => !occupied.Contains(Norm(o - 1)));
        Assert.Equal(1, runs);
    }

    // ── C-1/C-2: authoritative discard-turn signal ──

    /// <summary>After an auto deal the game is AwaitingDiscard with the dealer owing the initial
    /// 14-tile discard; the backend must emit turn["current"] with activeSeat = dealer and
    /// awaitingDiscard = true. RED on ddc72e1 (no <c>turn</c> collection exists).</summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "C-1")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Turn_Emitted_OnDealerInitial14_CarriesDealerSeat_AndAwaitingDiscard(int dealer)
    {
        var state = DealtWithDealer(dealer, 3, 4);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(dealer, state.ActiveSeatIndex);

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var (present, value) = FindSingleton(entries, "turn");

        Assert.True(present, "No authoritative turn cue was emitted while AwaitingDiscard (C-1).");
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(dealer, value.GetProperty("activeSeat").GetInt32());
        Assert.True(value.GetProperty("awaitingDiscard").GetBoolean());
        Assert.Equal("AwaitingDiscard", value.GetProperty("phase").GetString());
    }

    /// <summary>The turn cue names whatever seat is active during AwaitingDiscard — the shared
    /// invariant behind every C-2 source (auto-draw, Chow/Pung, Kong replacement), for human and
    /// bot seats alike.</summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "C-2")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Turn_CarriesActiveSeat_ForEverySeat(int activeSeat)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(7);
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.ActiveSeatIndex = activeSeat;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var (present, value) = FindSingleton(entries, "turn");

        Assert.True(present, "AwaitingDiscard must emit a turn cue.");
        Assert.Equal(activeSeat, value.GetProperty("activeSeat").GetInt32());
        Assert.True(value.GetProperty("awaitingDiscard").GetBoolean());
    }

    /// <summary>C-2 normal auto-draw: after a seat draws its turn tile (DrawTile keeps the phase in
    /// AwaitingDiscard and the drawer active), the turn cue names the drawer.</summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "C-2")]
    public void Turn_Emitted_AfterAutoDraw_ForDrawingSeat()
    {
        var state = DealtWithDealer(dealer: 0, die1: 4, die2: 5); // sum 9

        // Model a subsequent turn: hand the turn to seat 2 with a rest hand and draw.
        state.ActiveSeatIndex = 2;
        state.Hands[2].ConcealedTiles = state.Hands[2].ConcealedTiles.Take(13).ToList();
        var before = state.Hands[2].ConcealedTiles.Count;
        ChangshaGameStateMachine.DrawTile(state);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(2, state.ActiveSeatIndex);
        Assert.Equal(before + 1, state.Hands[2].ConcealedTiles.Count);

        var (present, value) = FindSingleton(ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0), "turn");
        Assert.True(present);
        Assert.Equal(2, value.GetProperty("activeSeat").GetInt32());
        Assert.True(value.GetProperty("awaitingDiscard").GetBoolean());
    }

    /// <summary>C-2 Chow/Pung: after a Pung claim the claimer is active and owes a discard; the
    /// turn cue names the claiming seat.</summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "C-2")]
    public void Turn_Emitted_AfterPungClaim_ForClaimingSeat()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 0));
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 1));
        var t5 = Tid(Suit.Tong, 5, 2);
        state.Hands[dealer].ConcealedTiles.Add(t5);

        ChangshaGameStateMachine.Discard(state, dealer, t5);
        ChangshaGameStateMachine.ResolveClaim(state, 1, TableClaimType.Pung);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(1, state.ActiveSeatIndex);

        var (present, value) = FindSingleton(ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0), "turn");
        Assert.True(present);
        Assert.Equal(1, value.GetProperty("activeSeat").GetInt32());
        Assert.True(value.GetProperty("awaitingDiscard").GetBoolean());
    }

    /// <summary>C-2 Kong replacement: after a concealed kong + back-of-wall replacement draw the
    /// konging seat stays active and owes a discard; the turn cue names it. Uses a controlled
    /// disjoint state so the kong leaves a valid 11-tile concealed hand.</summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "C-2")]
    public void Turn_Emitted_AfterKongReplacement_ForKongingSeat()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(7);
        const int konger = 0;
        state.DealerSeatIndex = konger;
        state.ActiveSeatIndex = konger;
        state.Phase = ChangshaPhase.AwaitingDiscard;

        // Konger holds a concealed quad (Wan 9) + 10 disjoint fillers (Tong) = 14 tiles.
        var quad = new[] { Tid(Suit.Wan, 9, 0), Tid(Suit.Wan, 9, 1), Tid(Suit.Wan, 9, 2), Tid(Suit.Wan, 9, 3) };
        var fillers = new List<int>();
        for (var rank = 1; rank <= 5; rank++)
            for (var copy = 0; copy < 2; copy++)
                fillers.Add(Tid(Suit.Tong, rank, copy));
        state.Hands[konger].ConcealedTiles = quad.Concat(fillers).ToList();
        // Wall (disjoint Tiao tiles) so the replacement can draw from the back.
        state.Wall = new List<int> { Tid(Suit.Tiao, 1, 0), Tid(Suit.Tiao, 2, 0), Tid(Suit.Tiao, 3, 0), Tid(Suit.Tiao, 4, 0) };
        state.WallBackDrawn = 0;

        ChangshaGameStateMachine.DeclareConcealedKong(state, konger, Logical(Suit.Wan, 9));

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(konger, state.ActiveSeatIndex);
        Assert.True(state.WallBackDrawn > 0, "Kong replacement must draw from the back of the wall.");
        Assert.Single(state.Hands[konger].Melds);

        var (present, value) = FindSingleton(ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0), "turn");
        Assert.True(present);
        Assert.Equal(konger, value.GetProperty("activeSeat").GetInt32());
        Assert.True(value.GetProperty("awaitingDiscard").GetBoolean());
    }

    /// <summary>Tombstone discipline (C-1): on any non-AwaitingDiscard phase the turn cue must
    /// explicitly retract — activeSeat = null and awaitingDiscard = false — so a stale cue never
    /// survives phase exit and stale tile geometry can never masquerade as the active seat. The
    /// explicit null (not an omitted field / JS-null value) is what the frontend trusts over
    /// geometry. Covers pre-deal, a claim window (mid-hand), end-of-hand, and game-complete.</summary>
    [Theory, Trait("Category", "Phase5a"), Trait("Contract", "C-1")]
    [InlineData(ChangshaPhase.Seating)]
    [InlineData(ChangshaPhase.AwaitingClaim)]
    [InlineData(ChangshaPhase.EndHand)]
    [InlineData(ChangshaPhase.GameComplete)]
    public void Turn_Retracted_OnEveryNonAwaitingDiscardPhase(ChangshaPhase phase)
    {
        var state = DealtWithDealer(dealer: 0, die1: 4, die2: 5);
        state.Phase = phase;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var (present, value) = FindSingleton(entries, "turn");

        Assert.True(present, "The turn collection must be present (with the cue retracted) on phase exit (C-1).");
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("activeSeat").ValueKind);
        Assert.False(value.GetProperty("awaitingDiscard").GetBoolean());
    }

    /// <summary>The discard-turn cue is server-authoritative; it must never depend solely on tile
    /// geometry. Concretely: the emitted seat equals the engine's ActiveSeatIndex even when the
    /// hand-tile geometry is atypical (here the active seat holds a claimed meld + fewer concealed
    /// tiles). RED on ddc72e1 (no cue at all).</summary>
    [Fact, Trait("Category", "Phase5a"), Trait("Contract", "C-1")]
    public void Turn_IsAuthoritative_NotGeometryDerived_AfterMeld()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 0));
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Tong, 5, 1));
        var t5 = Tid(Suit.Tong, 5, 2);
        state.Hands[dealer].ConcealedTiles.Add(t5);
        ChangshaGameStateMachine.Discard(state, dealer, t5);
        ChangshaGameStateMachine.ResolveClaim(state, 1, TableClaimType.Pung);

        // Seat 1 now holds 11 concealed + a 3-tile meld — geometry alone (concealed count) would
        // read as a 11-tile "not my turn" hand, but the seat authoritatively owes a discard.
        Assert.True(state.Hands[1].ConcealedTiles.Count <= 13);
        Assert.Single(state.Hands[1].Melds);

        var (present, value) = FindSingleton(ChangshaToAutotableTranslator.Translate(state, viewerSeat: 1), "turn");
        Assert.True(present);
        Assert.Equal(state.ActiveSeatIndex, value.GetProperty("activeSeat").GetInt32());
        Assert.True(value.GetProperty("awaitingDiscard").GetBoolean());
    }
}
