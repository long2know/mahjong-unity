using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha.Acceptance;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// Phase J Wave 3 — <see cref="WinResult"/> bool-surface tests (Vasquez).
///
/// <para>Bishop's Phase J Wave 3 task adds explicit
/// <see cref="WinResult.IsSelfDraw"/> and <see cref="WinResult.IsKongReplacement"/>
/// top-level boolean properties on <see cref="WinResult"/>. They were previously
/// only derivable from <see cref="WinResult.Method"/> (enum) and
/// <see cref="WinResult.AllPatterns"/> (collection scan), which Wave 2's
/// <c>SelfDrawWinContextTests</c> already pinned via reflection-defensive
/// fallback. This suite is the *canonical-axis* counterpart: it talks to the
/// new top-level bools directly and complements the Wave 2 suite by closing
/// the blind spot Vasquez flagged in the Wave 2 memo — a future regression
/// that flips <see cref="WinResult.IsSelfDraw"/> independently of
/// <see cref="WinResult.Method"/> (e.g. wire-serialization mismatch, a bad
/// merge that leaves the bool default-false while the enum is still
/// <see cref="WinMethod.SelfDraw"/>) would slip past Wave 2's helper which
/// silently fell back to the <c>Method</c> derivation.</para>
///
/// <para><b>Wave-2/Wave-3 division of labor:</b>
/// <list type="bullet">
///   <item>Wave 2 — <c>SelfDrawWinContextTests</c> — defence-in-depth across
///     all three axes (method-axis + WinPattern.KongReplacementWin presence
///     in AllPatterns + reflection-defensive bool axis). Stays green even
///     when the bool surfaces are absent.</item>
///   <item>Wave 3 (this suite) — directly asserts the explicit bools. Fails
///     if Bishop's bool surfaces regress; passes if they ship intact. No
///     reflection — direct property access proves the canonical contract.</item>
/// </list>
/// </para>
/// </summary>
public class WinResultSurfaceTests
{
    // ────────────────────────────────────────────────────────────────────
    //  1. Self-draw Hu — IsSelfDraw == true (canonical axis, direct read)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-3")]
    public void SelfDrawHu_ChangshaHandResult_HasIsSelfDrawTrue()
    {
        // End-to-end through DeclareSelfDrawWin (the canonical self-draw entry
        // point). Assert the resulting WinResult.IsSelfDraw == true directly,
        // without falling through to a Method-based derivation. Wave 2 already
        // pins the same scenario via the Method enum + reflection probe; this
        // test is the explicit-axis enforcement that locks the new bool surface.
        var state = BuildHandAfterDeal(dealerSeat: 0);

        // Force a deterministic non-context winning shape: 4 chows + Tong-5 pair.
        // Suppress HeavenlyHand via a pre-existing discard (gate reads
        // DiscardPile.Count == 0). Keep the wall non-empty so LastTileFromWall
        // (海底捞月) does not also fire — keeps the surface under test single-axis.
        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));

        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = 1,
            TileId = Tid(Suit.Tiao, 9, 0),
            TurnNumber = 1
        });
        Assert.True(state.Wall.Count > 0, "Fixture broken — wall must hold tiles to suppress LastTileFromWall.");

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        var win = state.CurrentWin!;

        // Canonical Wave-3 axis: explicit bool, direct read.
        Assert.True(win.IsSelfDraw,
            "WinResult.IsSelfDraw must be true on the DeclareSelfDrawWin path " +
            "(Phase J Wave 3 — Bishop's bool surface).");

        // Sanity: the Method/SeatIndex contract is unchanged from pre-Wave-3
        // semantics — the bool is an *additional* axis, not a replacement.
        Assert.Equal(WinMethod.SelfDraw, win.Method);
        Assert.Equal(0, win.WinningSeatIndex);
        Assert.Equal(0, win.SourceSeatIndex);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Ron (discard) Hu — IsSelfDraw == false (canonical axis, direct read)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-3")]
    public void RonHu_ChangshaHandResult_HasIsSelfDrawFalse()
    {
        // End-to-end through ResolveHuClaim (via ResolveClaim with TableClaimType.Hu).
        // The winning tile arrives from a discard, so WinResult.IsSelfDraw must
        // be false. Direct top-level bool read; no reflection fallback.
        var state = BuildHandAfterDeal(dealerSeat: 0);
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 1));
        state.Wall.RemoveAll(t => t / 4 == Logical(Suit.Wan, 1));

        // Dealer (seat 0) holds 14 tiles incl. the to-be-discarded Wan-1; the
        // remaining 13 do NOT win on their own (so DeclareSelfDrawWin would
        // throw if invoked — defensive against the wrong code-path firing).
        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Wan, 1));

        // Seat 2 (NOT seat 1) holds the 13-tile waiting hand — avoids the
        // first-discard EarthlyHand window. Seats 1/3 cleared to remove claim
        // collisions. Inject a benign prior discard so the dealer's throw is
        // NOT the first discard of the hand (EarthlyHand gate).
        OverrideHand13(state, seatIndex: 2, AcceptanceFixture.ThirteenTileWaitingForWan1());
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();
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

        // Canonical Wave-3 axis: discard-hu must explicitly clear IsSelfDraw.
        Assert.False(win.IsSelfDraw,
            "WinResult.IsSelfDraw must be false on the ResolveHuClaim/Discard path " +
            "(Phase J Wave 3 — Bishop's bool surface).");

        // Sanity: Method/source seat contract unchanged.
        Assert.Equal(WinMethod.Discard, win.Method);
        Assert.Equal(2, win.WinningSeatIndex);
        Assert.Equal(0, win.SourceSeatIndex);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Kong-replacement self-draw — both bools true (杠上开花)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-3")]
    public void KongReplacementHu_ChangshaHandResult_BothBoolsTrue()
    {
        // Concealed kong + replacement draw + self-draw Hu (杠上开花). Both
        // explicit axes must be set: IsSelfDraw (the replacement IS a draw,
        // not a discard claim) AND IsKongReplacement (the most recent draw
        // was the kong replacement, which the state-machine records via
        // state.LastDrawWasKongReplacement → WinContext.IsKongReplacementWin
        // → WinResult.IsKongReplacement).
        var state = BuildKongReplacementWinScenario();

        ChangshaGameStateMachine.DeclareConcealedKong(state, seatIndex: 0,
            logicalTile: Logical(Suit.Tiao, 9));
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        var win = state.CurrentWin!;

        Assert.True(win.IsSelfDraw,
            "WinResult.IsSelfDraw must be true on the kong-replacement self-draw path.");
        Assert.True(win.IsKongReplacement,
            "WinResult.IsKongReplacement must be true on the kong-replacement self-draw path " +
            "(杠上开花 — Phase J Wave 3 explicit-axis contract).");

        // Sanity: AllPatterns retains WinPattern.KongReplacementWin for backward
        // compatibility with Phase H/I consumers that scan the pattern list.
        Assert.Contains(WinPattern.KongReplacementWin, win.AllPatterns);
        Assert.Equal(WinMethod.SelfDraw, win.Method);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Regular discard Hu — IsKongReplacement == false
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-3")]
    public void RegularDiscardHu_ChangshaHandResult_KongReplacementFalse()
    {
        // Ron Hu via a plain discard (no kong involvement on either side).
        // Asserts the IsKongReplacement axis is *explicitly* false — defends
        // against a regression where a stray kong-replacement flag bleeds
        // from a prior hand or from a parallel kong window into a regular
        // discard-hu WinResult. This is the negative counterpart to test 3.
        var state = BuildHandAfterDeal(dealerSeat: 0);
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 1));
        state.Wall.RemoveAll(t => t / 4 == Logical(Suit.Wan, 1));

        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Wan, 1));

        OverrideHand13(state, seatIndex: 2, AcceptanceFixture.ThirteenTileWaitingForWan1());
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();
        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = 1,
            TileId = Tid(Suit.Tiao, 9, 0),
            TurnNumber = 1
        });

        // Pre-condition: LastDrawWasKongReplacement must be false BEFORE the
        // discard so the regression we're guarding against (stale flag bleed)
        // is actually testable. The Discard path clears the flag, but we want
        // to also confirm IsKongReplacement on the resulting WinResult is the
        // freshly-computed false rather than relying on a clearing side-effect.
        Assert.False(state.LastDrawWasKongReplacement,
            "Fixture broken — LastDrawWasKongReplacement should be false in a clean post-deal hand.");

        ChangshaGameStateMachine.Discard(state, seatIndex: 0, tileId: Tid(Suit.Wan, 1, 0));
        Assert.NotNull(state.ClaimWindow);

        ChangshaGameStateMachine.ResolveClaim(state, claimingSeatIndex: 2, claimType: TableClaimType.Hu);

        Assert.NotNull(state.CurrentWin);
        var win = state.CurrentWin!;

        Assert.False(win.IsKongReplacement,
            "WinResult.IsKongReplacement must be explicitly false for a plain discard-hu " +
            "(no kong on either side — Phase J Wave 3 contract).");

        // Defence-in-depth: AllPatterns must NOT carry the kong-replacement
        // pattern either — the two surfaces are required to agree.
        Assert.DoesNotContain(WinPattern.KongReplacementWin, win.AllPatterns);
        Assert.False(win.IsSelfDraw, "Discard-hu must also clear IsSelfDraw — sanity tie-in.");
        Assert.Equal(WinMethod.Discard, win.Method);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Scenario builders (kept local — additive-only test policy, mirrors
    //  SelfDrawWinContextTests private helpers so the two suites remain
    //  independently maintainable).
    // ────────────────────────────────────────────────────────────────────

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
}
