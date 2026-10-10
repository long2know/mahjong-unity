using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase J Wave 2 — self-draw WinContext propagation tests (Vasquez).
///
/// <para>Bishop's Phase J Wave 2 task audits <see cref="WinContext"/> propagation
/// through both <see cref="ChangshaGameStateMachine.DeclareSelfDrawWin"/> and
/// <see cref="ChangshaGameStateMachine.ResolveClaim"/>. These tests pin the
/// observable per-Hu-method contract that callers (scoring, UI banners, replay)
/// rely on: the win record's method-axis flags reflect HOW the winning tile
/// arrived, distinct from WHICH structural pattern the hand satisfies.</para>
///
/// <para><b>Contract surfaces probed</b> (reflection-defensive — the test
/// resolves whichever surface Bishop ships):
/// <list type="bullet">
///   <item>Self-draw Hu: the winning record's "IsSelfDraw" axis is true. If
///     Bishop exposes an explicit <c>IsSelfDraw</c> property on
///     <see cref="WinResult"/> or <see cref="WinContext"/>, the test consults
///     it directly; otherwise it falls back to the canonical
///     <c>Method == WinMethod.SelfDraw</c> predicate, which is the
///     pre-Wave-2 source of truth.</item>
///   <item>Ron (discard) Hu: the "IsSelfDraw" axis is false. Method is
///     <see cref="WinMethod.Discard"/>; the source seat is whoever threw
///     the winning tile, not the winner.</item>
///   <item>Kong-replacement self-draw (杠上开花): BOTH IsSelfDraw=true AND
///     the kong-replacement flag is set. The kong-replacement signal is
///     observable today through <see cref="WinPattern.KongReplacementWin"/>
///     in <see cref="WinResult.AllPatterns"/>; if Bishop also surfaces
///     <c>IsKongReplacement</c> as a top-level flag this test consults it
///     in addition to the AllPatterns check.</item>
/// </list>
/// </para>
///
/// <para><b>Why no live-bot path.</b> The wave brief mentions "Hard bot draws
/// the winning tile" — using the Hard strategy through the bot harness would
/// add seed-flake risk for what is fundamentally a state-machine contract test.
/// We follow the same pattern as <see cref="SpecialContextWinsTests"/>:
/// construct a deterministic hand atop a dealt fixture, drive the
/// state-machine API directly, and assert the resulting
/// <see cref="ChangshaGameState.CurrentWin"/> shape.</para>
/// </summary>
public class SelfDrawWinContextTests
{
    // ────────────────────────────────────────────────────────────────────
    //  1. Self-draw Hu — IsSelfDraw axis = true
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-2")]
    public void SelfDrawHu_SetsIsSelfDrawTrue_InWinContext()
    {
        // Phase J Wave 2 §3 (self-draw propagation): the active seat draws the
        // winning tile and declares Hu. The win record must record this as a
        // SELF-DRAW — Method=SelfDraw and any explicit IsSelfDraw axis = true.
        var state = BuildHandAfterDeal(dealerSeat: 0);

        // Force the dealer-active hand into a deterministic non-context, non-stacked
        // 14-tile winning shape: 4 chows + Tong-5 pair (258 ✓). Wall is non-empty so
        // LastTileFromWall does NOT fire (would inflate AllPatterns and confuse the
        // single-axis test focus). DiscardPile is non-empty so HeavenlyHand does NOT
        // fire either — we explicitly clear the deal-state context noise by injecting
        // a sacrificial prior discard.
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].Melds.Clear();
        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));

        // Suppress HeavenlyHand by injecting a benign prior discard from a non-dealer.
        // The gate in DeclareSelfDrawWin reads `state.DiscardPile.Count == 0`, so any
        // non-empty pile defeats it.
        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = 1,
            TileId = Tid(Suit.Tiao, 9, 0),
            TurnNumber = 1
        });

        // Wall must be non-empty so LastTileFromWall stays false.
        Assert.True(state.Wall.Count > 0, "Fixture broken — wall must hold tiles to suppress LastTileFromWall.");

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        var win = state.CurrentWin!;

        // Method-axis: canonical surface. SelfDraw is the contract.
        Assert.Equal(WinMethod.SelfDraw, win.Method);
        Assert.Equal(0, win.WinningSeatIndex);
        Assert.Equal(0, win.SourceSeatIndex); // self-draw: winner == source

        // IsSelfDraw axis (reflection-defensive): Bishop may surface this as an
        // explicit bool on WinResult or WinContext. If so, must be true; if not,
        // the Method check above is the contract.
        AssertIsSelfDrawAxis(win, expected: true);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Ron (discard) Hu — IsSelfDraw axis = false
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-2")]
    public void RonHu_SetsIsSelfDrawFalse_InWinContext()
    {
        // Phase J Wave 2 §3 (ron-hu propagation): non-dealer claims Hu on
        // another seat's discard. Method=Discard, SourceSeatIndex=discarder,
        // IsSelfDraw axis = false. This is the negative companion to test 1.
        var state = BuildHandAfterDeal(dealerSeat: 0);
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 1));
        state.Wall.RemoveAll(t => t / 4 == Logical(Suit.Wan, 1));

        // Dealer (seat 0) holds 14 tiles with Wan-1 as the discard tile. The
        // remaining 13 do NOT form a winning shape (so Method=SelfDraw doesn't
        // fire ahead of the claim).
        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Wan, 1));

        // Seat 2 (NOT seat 1 — that would trigger EarthlyHand on a first-discard
        // claim, which inflates AllPatterns and distracts from the axis under
        // test). We pad the discard pile with a prior throwaway so DiscardPile.Count
        // > 1 → EarthlyHand is suppressed AND the seat-2 wait holds.
        // Seat 1 and seat 3 cleared to avoid claim-window collisions.
        OverrideHand13(state, seatIndex: 2, AcceptanceFixture.ThirteenTileWaitingForWan1());
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        // Inject a benign prior discard so DiscardPile.Count is 1 BEFORE dealer's
        // throw — after the discard there will be 2 in the pile, which the
        // EarthlyHand gate (DiscardPile.Count == 1 inside the resolver — read
        // before RemoveLastDiscard) does NOT see as the "first discard".
        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = 1,
            TileId = Tid(Suit.Tiao, 9, 0),
            TurnNumber = 1
        });

        ChangshaGameStateMachine.Discard(state, seatIndex: 0, tileId: Tid(Suit.Wan, 1, 0));
        Assert.NotNull(state.ClaimWindow);

        ChangshaGameStateMachine.ResolveClaim(state, claimingSeatIndex: 2, claimType: TableClaimType.Hu);

        Assert.NotNull(state.CurrentWin);
        var win = state.CurrentWin!;

        // Method-axis: Discard, not SelfDraw.
        Assert.Equal(WinMethod.Discard, win.Method);
        Assert.Equal(2, win.WinningSeatIndex);
        Assert.Equal(0, win.SourceSeatIndex); // dealer discarded the winning tile
        Assert.False(win.IsRobbedKong, "Plain discard-hu must not flag as RobbingKong.");

        // IsSelfDraw axis: explicitly false (regardless of which surface Bishop
        // chose to expose it on).
        AssertIsSelfDrawAxis(win, expected: false);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Kong-replacement self-draw — both IsSelfDraw AND IsKongReplacement
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-2")]
    public void KongReplacementDraw_HuViaGangShangKaiHua_FlagsBothSelfDrawAndKongReplacement()
    {
        // Phase J Wave 2 §3 (杠上开花 propagation): the active seat declares a
        // concealed kong, the replacement-tile draw completes the hand, they
        // declare self-draw Hu. The win record must surface BOTH axes:
        //   - IsSelfDraw: true (the replacement IS a draw, not a discard claim)
        //   - IsKongReplacement: true (the most recent draw was the kong replacement)
        //
        // Today the kong-replacement signal lives in
        // WinResult.AllPatterns.Contains(WinPattern.KongReplacementWin); if
        // Bishop also lifts an explicit IsKongReplacement bool, the test
        // consults both. Either path keeps the test green under both pre-fix
        // and post-fix worlds.
        var state = BuildKongReplacementWinScenario();

        ChangshaGameStateMachine.DeclareConcealedKong(state, seatIndex: 0,
            logicalTile: Logical(Suit.Tiao, 9));
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        var win = state.CurrentWin!;

        // Self-draw axis: true.
        Assert.Equal(WinMethod.SelfDraw, win.Method);
        AssertIsSelfDrawAxis(win, expected: true);

        // Kong-replacement axis: true. Observable via AllPatterns today; if
        // Bishop lifts an explicit IsKongReplacement bool the helper consults it.
        AssertIsKongReplacementAxis(win, expected: true);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Scenario builders
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive CreateGame → StartGame → RollDice → Deal and clear the post-deal
    /// state so each test can deterministically override seats. Mirrors
    /// <see cref="SpecialContextWinsTests"/> BuildHandAfterDeal.
    /// </summary>
    private static ChangshaGameState BuildHandAfterDeal(int dealerSeat = 0)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 42,
            botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        state.ActiveSeatIndex = dealerSeat;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.MissedWinSeats.Clear();
        state.DiscardPile.Clear();
        state.TurnNumber = 1;
        return state;
    }

    /// <summary>
    /// Build the kong-replacement self-draw fixture (mirrors
    /// <see cref="SpecialContextWinsTests"/>.BuildKongReplacementScenario for the
    /// winning case). Seat 0 holds 14 tiles: 4×Tiao-9 (kong source) + 10 fillers
    /// that complete to a winning shape once the kong is declared and Tong-5
    /// is drawn off the back of the wall.
    /// </summary>
    private static ChangshaGameState BuildKongReplacementWinScenario()
    {
        var state = BuildHandAfterDeal(dealerSeat: 0);

        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tiao, 9));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 5));
        state.Wall.RemoveAll(t => t / 4 == Logical(Suit.Tiao, 9));
        state.Wall.RemoveAll(t => t / 4 == Logical(Suit.Tong, 5));

        var seat0Tiles = new List<int>
        {
            Tid(Suit.Tiao, 9, 0), Tid(Suit.Tiao, 9, 1),
            Tid(Suit.Tiao, 9, 2), Tid(Suit.Tiao, 9, 3),
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0),
            Tid(Suit.Tong, 5, 0),
        };
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].Melds.Clear();
        state.Hands[0].ConcealedTiles.AddRange(seat0Tiles);

        for (var i = 1; i < 4; i++)
        {
            state.Hands[i].ConcealedTiles.Clear();
            state.Hands[i].Melds.Clear();
        }

        var replacementTile = Tid(Suit.Tong, 5, 1);
        foreach (var h in state.Hands)
            h.ConcealedTiles.RemoveAll(t => t == replacementTile);
        state.Wall.RemoveAll(t => t == replacementTile);
        state.Wall.Add(replacementTile);
        state.WallBackIndex = state.Wall.Count - 1;

        return state;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Hand override helpers (kept local to avoid coupling to SpecialContextWinsTests
    //  private helpers — additive-only test policy)
    // ────────────────────────────────────────────────────────────────────

    private static void OverrideConcealedWith14(ChangshaGameState state, int seatIndex,
        params (Suit suit, int rank)[] tiles)
    {
        var copies = new Dictionary<int, int>();
        var tileIds = new List<int>(tiles.Length);
        foreach (var (s, r) in tiles)
        {
            var logical = Logical(s, r);
            copies.TryGetValue(logical, out var copy);
            tileIds.Add(Tid(s, r, copy));
            copies[logical] = copy + 1;
        }
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    private static void OverrideHand13(ChangshaGameState state, int seatIndex, IEnumerable<int> tileIds)
    {
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    // ────────────────────────────────────────────────────────────────────
    //  Reflection-defensive axis probes
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assert the "IsSelfDraw" axis on the win record matches the expected value.
    /// Probes (in order):
    ///   1. An explicit <c>IsSelfDraw</c> bool on <see cref="WinResult"/>.
    ///   2. Falls back to the canonical <see cref="WinResult.Method"/> ==
    ///      <see cref="WinMethod.SelfDraw"/> predicate.
    /// </summary>
    private static void AssertIsSelfDrawAxis(WinResult win, bool expected)
    {
        var prop = typeof(WinResult).GetProperty("IsSelfDraw",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null && prop.PropertyType == typeof(bool))
        {
            var actual = (bool)(prop.GetValue(win) ?? false);
            Assert.Equal(expected, actual);
            return;
        }
        // Pre-fix fallback: derive from Method. The Method-axis check is also
        // exercised in the caller as a "headline" assertion, so this fallback
        // is a defence-in-depth.
        var derivedSelfDraw = win.Method == WinMethod.SelfDraw;
        Assert.True(derivedSelfDraw == expected,
            $"IsSelfDraw axis mismatch (derived from Method): expected={expected}, " +
            $"actual={derivedSelfDraw} (Method={win.Method}). " +
            $"Bishop owes the explicit WinResult.IsSelfDraw bool OR the Method propagation " +
            $"path must be correct.");
    }

    /// <summary>
    /// Assert the "IsKongReplacement" axis on the win record matches the
    /// expected value. Probes (in order):
    ///   1. An explicit <c>IsKongReplacement</c> bool on <see cref="WinResult"/>.
    ///   2. Falls back to the canonical
    ///      <c>AllPatterns.Contains(WinPattern.KongReplacementWin)</c> check.
    /// </summary>
    private static void AssertIsKongReplacementAxis(WinResult win, bool expected)
    {
        var prop = typeof(WinResult).GetProperty("IsKongReplacement",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null && prop.PropertyType == typeof(bool))
        {
            var actual = (bool)(prop.GetValue(win) ?? false);
            Assert.Equal(expected, actual);
            return;
        }
        // Pre-fix fallback: derive from AllPatterns. KongReplacementWin lives in
        // WinPattern as a contextual Big Win pattern (Phase I Wave 1 contract).
        var allPatternNames = Enum.GetNames(typeof(WinPattern));
        if (!allPatternNames.Contains("KongReplacementWin"))
        {
            throw new InvalidOperationException(
                "Neither WinResult.IsKongReplacement nor WinPattern.KongReplacementWin exists. " +
                "Bishop owes one of these surfaces for the 杠上开花 propagation contract.");
        }
        var kongReplPattern = (WinPattern)Enum.Parse(typeof(WinPattern), "KongReplacementWin");
        var derivedFromPatterns = win.AllPatterns.Contains(kongReplPattern);
        Assert.True(derivedFromPatterns == expected,
            $"IsKongReplacement axis mismatch (derived from AllPatterns): expected={expected}, " +
            $"actual={derivedFromPatterns} (AllPatterns=[{string.Join(",", win.AllPatterns)}]). " +
            "If this fires, the WinContext.IsKongReplacementWin flag is not propagating from the " +
            "state machine into the detector's AllPatterns list.");
    }
}
