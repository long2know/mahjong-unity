using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase I Wave 1 — Special Context Wins (天和 / 地和 / 海底捞月 / 河底捞鱼 / 杠上开花).
///
/// Five new contextual Big Win patterns whose detection depends on the surrounding
/// game-state context, not just the tile composition of the winning hand. Per
/// Vasquez's Changsha spec §4.3 (Phase I+ scope per Ripley's Phase H §3 close-out
/// memo) these patterns stack with the existing Wave-2 Big Wins via
/// <see cref="WinDetectionResult.AllPatterns"/>.
///
/// <list type="table">
///   <listheader><term>Pattern</term><term>Chinese</term><term>Trigger</term></listheader>
///   <item>
///     <term><c>HeavenlyHand</c></term><term>天和</term>
///     <term>Dealer wins on initial 14-tile deal — <c>TurnNumber==1</c>,
///     <c>DiscardPile</c> empty, dealer is active.</term>
///   </item>
///   <item>
///     <term><c>EarthlyHand</c></term><term>地和</term>
///     <term>Non-dealer Hus on the dealer's very first discard — no other
///     actions between deal-complete and the Hu claim.</term>
///   </item>
///   <item>
///     <term><c>LastTileFromWall</c></term><term>海底捞月</term>
///     <term>Self-draw on the very last tile of the live wall — wall has
///     0 tiles remaining post-draw.</term>
///   </item>
///   <item>
///     <term><c>LastDiscardCatch</c></term><term>河底捞鱼</term>
///     <term>Hu on a discard while the wall is exhausted (no more draws
///     possible after this discard resolves).</term>
///   </item>
///   <item>
///     <term><c>KongReplacementWin</c></term><term>杠上开花</term>
///     <term>Self-draw on the replacement tile drawn immediately after
///     declaring a kong. Tracked via
///     <c>ChangshaGameState.LastDrawWasKongReplacement</c>, which Bishop sets
///     after every kong-replacement draw and clears on subsequent discards.</term>
///   </item>
/// </list>
///
/// Tests reach for Bishop's symbols via reflection (<c>WinPattern.HeavenlyHand</c>
/// et al., <c>ChangshaGameState.LastDrawWasKongReplacement</c>) so this assembly
/// compiles before his contract commits land. RED-fail messages name the missing
/// symbol so Bishop can grep for the exact contract owed.
/// </summary>
public class SpecialContextWinsTests
{
    // ────────────────────────────────────────────────────────────────────────
    //  HeavenlyHand (天和) — dealer wins on the initial deal
    // ────────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void HeavenlyHand_DealerSelfDrawsOnInitialHand_FlagsHeavenlyHand()
    {
        // Phase I Wave 1 §1 (天和): dealer is dealt 14 tiles forming a winning hand
        // and immediately declares Hu. No discards have occurred, no draws beyond
        // the initial deal — this is the highest big-win in Changsha.
        //
        // Acceptance contract: state.CurrentWin.AllPatterns must contain HeavenlyHand
        // AND state.CurrentWin.Method must be SelfDraw. Source-seat is the winner
        // themselves (per WinMethod.SelfDraw semantics).
        var heavenly = ResolveSpecialPatternEnum("HeavenlyHand", "天和",
            "Dealer wins on initial 14-tile deal (TurnNumber==1, DiscardPile empty).");

        var state = BuildHandAfterDeal(dealerSeat: 0);
        // 14-tile dealer hand: 4 chows + pair on Tong-5 (258 ✓). NOT a FullFlush,
        // NOT AllPungs — Standard structure only, so HeavenlyHand is the lone
        // additive pattern.
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.SelfDraw, state.CurrentWin!.Method);
        Assert.Equal(0, state.CurrentWin.WinningSeatIndex);
        Assert.Equal(0, state.CurrentWin.SourceSeatIndex);

        Assert.Contains(heavenly, state.CurrentWin.AllPatterns);
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void HeavenlyHand_NonDealerSelfDraw_DoesNotQualify_EvenOnFirstAction()
    {
        // Phase I Wave 1 §1 (天和 negative): even if a non-dealer seat somehow
        // has a winning 14-tile hand at TurnNumber==1, HeavenlyHand must NOT fire —
        // the pattern is strictly dealer-exclusive (天 = "heaven", reserved for
        // the dealer/east seat per canonical spec).
        var heavenly = ResolveSpecialPatternEnum("HeavenlyHand", "天和",
            "HeavenlyHand is dealer-exclusive — non-dealer self-draws must not flag.");

        var state = BuildHandAfterDeal(dealerSeat: 0);
        int[] winningTiles =
        [
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0),
            Tid(Suit.Tiao, 4, 0), Tid(Suit.Tiao, 5, 0), Tid(Suit.Tiao, 6, 0),
            Tid(Suit.Tong, 5, 0), Tid(Suit.Tong, 5, 1)
        ];
        var hand = state.Hands[1].ConcealedTiles;
        Assert.Equal(13, hand.Count);
        for (var index = 0; index < hand.Count; index++)
            SwapInto(hand, index, winningTiles[index]);
        SwapInto(state.Wall, 0, winningTiles[^1]);
        state.ActiveSeatIndex = 1; // force non-dealer to be active

        // Keep the first-action context, but establish the non-dealer's own draw.
        Assert.False(ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, 1));
        var draws = ChangshaGameStateMachine.DrawTile(state);
        Assert.Equal(winningTiles, hand);
        Assert.Equal(1, state.LastDrawSeatIndex);
        Assert.Contains(draws, draw => draw.EventType == "tile-drawn"
            && draw.SeatIndex == 1 && draw.TileId == winningTiles[^1]);
        Assert.Equal(1, state.TurnNumber);
        Assert.Empty(state.DiscardPile);
        Assert.Equal(Enumerable.Range(0, 108),
            state.Wall.Concat(state.Hands.SelectMany(seat => seat.ConcealedTiles)).OrderBy(tile => tile));

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 1);

        Assert.NotNull(state.CurrentWin);
        Assert.DoesNotContain(heavenly, state.CurrentWin!.AllPatterns);

        void SwapInto(List<int> target, int index, int tile)
        {
            var source = state.Hands.Select(seat => seat.ConcealedTiles).Append(state.Wall)
                .Single(tiles => tiles.Contains(tile));
            var sourceIndex = source.IndexOf(tile);
            (source[sourceIndex], target[index]) = (target[index], source[sourceIndex]);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  EarthlyHand (地和) — non-dealer Hus on dealer's very first discard
    // ────────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void EarthlyHand_NonDealerHus_OnDealerFirstDiscard()
    {
        // Phase I Wave 1 §1 (地和): dealer's very first discard completes a non-dealer's
        // hand. No intervening claims, no intervening draws. The non-dealer wins by
        // Discard with Method=Discard, SourceSeatIndex=dealer.
        var earthly = ResolveSpecialPatternEnum("EarthlyHand", "地和",
            "Non-dealer Hus on dealer's first discard (no actions between deal and Hu).");

        var state = BuildEarthlyHandScenario();

        // Dealer (seat 0) discards Wan-1 — seat 1's hand completes on it.
        ChangshaGameStateMachine.Discard(state, seatIndex: 0, tileId: Tid(Suit.Wan, 1, 0));
        Assert.NotNull(state.ClaimWindow);

        ChangshaGameStateMachine.ResolveClaim(state,
            claimingSeatIndex: 1, claimType: TableClaimType.Hu);

        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.Discard, state.CurrentWin!.Method);
        Assert.Equal(1, state.CurrentWin.WinningSeatIndex);
        Assert.Equal(0, state.CurrentWin.SourceSeatIndex);

        Assert.Contains(earthly, state.CurrentWin.AllPatterns);
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void EarthlyHand_OnSecondDiscard_DoesNotFire()
    {
        // Phase I Wave 1 §1 (地和 negative): once any seat has acted beyond the
        // dealer's first discard, the "earthly" window has closed. Here the dealer's
        // first discard is benign (no claims), seat 1 draws + discards, and seat 2
        // wins on seat 1's discard. EarthlyHand must NOT fire — the winner is not
        // claiming on the dealer's first discard.
        var earthly = ResolveSpecialPatternEnum("EarthlyHand", "地和",
            "EarthlyHand fires only on dealer's first discard — second discard must not flag.");

        var state = BuildHandAfterDeal(dealerSeat: 0);

        // Strip Wan-1 globally so seat-2's wait is deterministic.
        StripLogicalFromState(state, Logical(Suit.Wan, 1));

        // Dealer (seat 0): 14 tiles — 13 fillers + 1 Tiao-9 (benign discard that
        // no other seat waits on). Seats 1/3 empty so the claim window stays closed.
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Tiao, 9));
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        // Seat 2: 13-tile hand waiting on Wan-1 (uses the canonical waiting hand).
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[2].ConcealedTiles.AddRange(AcceptanceFixture.ThirteenTileWaitingForWan1());

        // Dealer's first discard — benign, no claim window opens.
        ChangshaGameStateMachine.Discard(state, seatIndex: 0,
            tileId: Tid(Suit.Tiao, 9, 0));
        Assert.Null(state.ClaimWindow); // no opportunities, cursor advanced

        // Cursor should have advanced to seat 1. Drive seat 1 to draw, then discard
        // Wan-1 (we inject Wan-1 into seat 1's hand so they can legally discard it).
        Assert.Equal(1, state.ActiveSeatIndex);
        // Inject Wan-1 into seat 1 directly (skip the draw to avoid wall-RNG surprises).
        state.Hands[1].ConcealedTiles.Add(Tid(Suit.Wan, 1, 0));
        // Bump TurnNumber to match a real "second discard" — Discard() already did
        // its own increment, but explicit set future-proofs against off-by-one issues.

        ChangshaGameStateMachine.Discard(state, seatIndex: 1,
            tileId: Tid(Suit.Wan, 1, 0));
        Assert.NotNull(state.ClaimWindow);

        ChangshaGameStateMachine.ResolveClaim(state,
            claimingSeatIndex: 2, claimType: TableClaimType.Hu);

        Assert.NotNull(state.CurrentWin);
        Assert.DoesNotContain(earthly, state.CurrentWin!.AllPatterns);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  LastTileFromWall (海底捞月) — self-draw on the final wall tile
    // ────────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void LastTileFromWall_SelfDrawOnFinalTile_FlagsLastTile()
    {
        // Phase I Wave 1 §1 (海底捞月): the dealer draws the very last tile from
        // the live wall, completing their hand. Post-draw wall must have 0 tiles
        // remaining; the drawn tile is added to their 13-tile hand and they
        // immediately declare Hu.
        var lastTile = ResolveSpecialPatternEnum("LastTileFromWall", "海底捞月",
            "Self-draw on the final wall tile (state.Wall.Count == 0 after draw).");

        var state = BuildHandAfterDeal(dealerSeat: 0);

        // Seat 0: 13-tile hand waiting on Wan-1 — canonical Vasquez waiting hand.
        StripLogicalFromState(state, Logical(Suit.Wan, 1));
        OverrideHandWith13Waiting(state, seatIndex: 0,
            AcceptanceFixture.ThirteenTileWaitingForWan1());

        // Drain the wall down to exactly 1 tile — and force that final tile to be
        // Wan-1 (the winning tile). DrawTile uses DrawFromFront, so we need
        // state.Wall[0] = Wan-1 after the drain.
        state.Wall.Clear();
        state.Wall.Add(Tid(Suit.Wan, 1, 0));
        state.WallDrawIndex = 0;
        state.WallBackIndex = state.Wall.Count - 1;

        // Dealer is already active, phase = AwaitingDiscard. Drive a DrawTile then
        // a self-draw win.
        ChangshaGameStateMachine.DrawTile(state);
        Assert.Empty(state.Wall);

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.SelfDraw, state.CurrentWin!.Method);
        Assert.Contains(lastTile, state.CurrentWin.AllPatterns);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  LastDiscardCatch (河底捞鱼) — Hu on a discard once the wall is exhausted
    // ────────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void LastDiscardCatch_HuOnDiscardAfterWallExhausted_FlagsLastDiscard()
    {
        // Phase I Wave 1 §1 (河底捞鱼): the wall is exhausted (Wall.Count == 0).
        // The seat that drew the last tile cannot win on it (different hand shape),
        // so they discard — and a different seat completes their hand on that final
        // discard. WinMethod must be Discard.
        var lastDiscard = ResolveSpecialPatternEnum("LastDiscardCatch", "河底捞鱼",
            "Hu on a discard when state.Wall.Count == 0 (no more draws possible).");

        var state = BuildHandAfterDeal(dealerSeat: 0);
        StripLogicalFromState(state, Logical(Suit.Wan, 1));

        // Wall exhausted.
        state.Wall.Clear();
        state.WallDrawIndex = 0;
        state.WallBackIndex = -1;

        // Dealer (seat 0): 14 tiles, holds Wan-1 as the discard tile. Not a winning
        // hand on its own.
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Wan, 1));

        // Seat 1: 13-tile hand waiting on Wan-1.
        OverrideHandWith13Waiting(state, seatIndex: 1,
            AcceptanceFixture.ThirteenTileWaitingForWan1());
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        // Drive the final discard.
        ChangshaGameStateMachine.Discard(state, seatIndex: 0,
            tileId: Tid(Suit.Wan, 1, 0));
        Assert.NotNull(state.ClaimWindow);

        ChangshaGameStateMachine.ResolveClaim(state,
            claimingSeatIndex: 1, claimType: TableClaimType.Hu);

        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.Discard, state.CurrentWin!.Method);
        Assert.Contains(lastDiscard, state.CurrentWin.AllPatterns);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  KongReplacementWin (杠上开花) — self-draw on a kong replacement tile
    // ────────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void KongReplacementWin_SelfDrawOnKongReplacement_FlagsKongReplacement()
    {
        // Phase I Wave 1 §1 (杠上开花): seat declares a kong, the replacement tile
        // drawn from the back of the wall completes their hand, and they declare
        // self-draw Hu. The state.LastDrawWasKongReplacement flag must have been
        // set by DeclareConcealedKong so the detector can pick up the context.
        var kongReplWin = ResolveSpecialPatternEnum("KongReplacementWin", "杠上开花",
            "Self-draw on the kong-replacement tile (state.LastDrawWasKongReplacement true).");

        var state = BuildKongReplacementScenario(winningReplacement: true);

        // Sanity: pre-kong, the flag must be false (or absent).
        Assert.False(GetLastDrawWasKongReplacement(state),
            "Bishop's LastDrawWasKongReplacement must default to false before any kong.");

        ChangshaGameStateMachine.DeclareConcealedKong(state, seatIndex: 0,
            logicalTile: Logical(Suit.Tiao, 9));

        // After the concealed-kong + replacement draw, the flag must be true.
        Assert.True(GetLastDrawWasKongReplacement(state),
            "After a concealed-kong replacement draw, " +
            "state.LastDrawWasKongReplacement must be true (Bishop's Phase I Wave 1 contract).");

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.SelfDraw, state.CurrentWin!.Method);
        Assert.Contains(kongReplWin, state.CurrentWin.AllPatterns);
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void KongReplacement_ClearedOnSubsequentDiscard()
    {
        // Phase I Wave 1 §1 (杠上开花 lifecycle): if the replacement tile does NOT
        // complete the hand and the seat instead discards, state.LastDrawWasKongReplacement
        // must flip back to false so a later self-draw is NOT misclassified as
        // KongReplacementWin.
        var state = BuildKongReplacementScenario(winningReplacement: false);

        ChangshaGameStateMachine.DeclareConcealedKong(state, seatIndex: 0,
            logicalTile: Logical(Suit.Tiao, 9));
        Assert.True(GetLastDrawWasKongReplacement(state),
            "Replacement draw must set LastDrawWasKongReplacement=true.");

        // Replacement tile is benign — discard it.
        var discardTile = state.Hands[0].ConcealedTiles[^1];
        ChangshaGameStateMachine.Discard(state, seatIndex: 0, tileId: discardTile);

        Assert.False(GetLastDrawWasKongReplacement(state),
            "After a discard following a kong replacement, " +
            "LastDrawWasKongReplacement must be cleared (Bishop's Phase I Wave 1 contract).");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Stacking — HeavenlyHand × FullFlush yields 2 patterns in AllPatterns
    // ────────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-1")]
    public void HeavenlyHand_StacksWithFullFlush_PopulatesBothInAllPatterns()
    {
        // Phase I Wave 1 §1 + Phase H Wave 2 §2.3: a hand that is BOTH HeavenlyHand
        // (天和 — dealer wins on initial deal) AND FullFlush (清一色 — single suit)
        // must surface both flags in AllPatterns so ScoringService applies the
        // stacking multiplier (×2). The two patterns are orthogonal axes — context
        // (deal-state) vs structure (suit composition) — so they MUST stack.
        var heavenly = ResolveSpecialPatternEnum("HeavenlyHand", "天和",
            "HeavenlyHand should stack with structural patterns like FullFlush.");

        var state = BuildHandAfterDeal(dealerSeat: 0);
        // 14-tile all-Wan hand: 4 chows + pair Wan-5 (258 ✓, FullFlush ✓).
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9));

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);

        Assert.NotNull(state.CurrentWin);
        var patterns = state.CurrentWin!.AllPatterns;

        Assert.Contains(heavenly, patterns);
        Assert.Contains(WinPattern.FullFlush, patterns);
        Assert.True(patterns.Count >= 2,
            $"HeavenlyHand + FullFlush must yield ≥2 patterns in AllPatterns " +
            $"(×2 stack). Got [{string.Join(",", patterns)}].");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Scenario builders
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive CreateGame → StartGame → RollDice → Deal and then clear the
    /// post-deal hand state so the test can deterministically override each seat.
    /// All seats bot-occupied so the SM doesn't gate on human seat presence.
    /// Phase = AwaitingDiscard, ActiveSeatIndex = dealer, TurnNumber = 1,
    /// DiscardPile empty, MissedWinSeats empty.
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
        state.TurnNumber = 1; // deal-just-completed semantics
        return state;
    }

    /// <summary>
    /// EarthlyHand positive scenario: dealer holds 14 tiles with Wan-1 as the tile
    /// they'll discard; seat 1 holds 13 tiles waiting on Wan-1; seats 2/3 empty.
    /// All Wan-1 copies stripped from other hands and the wall so seat 1's
    /// adjudicator pick is unambiguous.
    /// </summary>
    private static ChangshaGameState BuildEarthlyHandScenario()
    {
        var state = BuildHandAfterDeal(dealerSeat: 0);
        StripLogicalFromState(state, Logical(Suit.Wan, 1));

        // Dealer (seat 0): 14-tile hand with Wan-1 to discard. Fillers chosen to
        // avoid forming any winning shape on the remaining 13.
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Wan, 1));

        // Seat 1: 13-tile waiting hand from the AcceptanceFixture helper.
        OverrideHandWith13Waiting(state, seatIndex: 1,
            AcceptanceFixture.ThirteenTileWaitingForWan1());
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();
        return state;
    }

    /// <summary>
    /// KongReplacementWin scenario builder. Seat 0 holds 10 concealed tiles +
    /// 4× Tiao-9 (the concealed-kong source) for 14 tiles total. The 10 non-kong
    /// tiles are arranged so that:
    ///   - if <paramref name="winningReplacement"/> is true: replacement = Tong-5
    ///     completes the hand (4 chows + pair on Tong-5, 258 ✓).
    ///   - if false: replacement = Tong-9 leaves a non-winning shape, forcing
    ///     a discard.
    /// The back of the wall is rewritten so DrawFromBack returns the chosen
    /// replacement tile deterministically.
    /// </summary>
    private static ChangshaGameState BuildKongReplacementScenario(bool winningReplacement)
    {
        var state = BuildHandAfterDeal(dealerSeat: 0);

        // Strip Tiao-9 from all hands + wall so the kong is unique to seat 0,
        // plus strip Tong-5 (winning rep) and Tong-9 (benign rep) so the back-of-wall
        // injection is deterministic.
        StripLogicalFromState(state, Logical(Suit.Tiao, 9));
        StripLogicalFromState(state, Logical(Suit.Tong, 5));
        StripLogicalFromState(state, Logical(Suit.Tong, 9));

        // Seat 0: 14 concealed tiles — 4 Tiao-9 (kong) + 10 fillers that, once the
        // 4 kong tiles are extracted into a meld and a replacement is drawn,
        // form a complete winning structure (4 chows + pair on Tong-5) OR an
        // explicitly non-winning shape.
        //
        // Pre-kong concealed structure (14 tiles):
        //   Wan 1-2-3 + Wan 4-5-6 + Tong 1-2-3 + Tong 5 + Tong 5 + 4× Tiao 9
        // After kong: melds gain a ConcealedKong of Tiao-9×4, concealed has 10 tiles:
        //   Wan 1-2-3 + Wan 4-5-6 + Tong 1-2-3 + Tong 5-Tong 5
        // Replacement draws to 11 tiles. With the kong meld counting as a "set":
        //   replacement = Tong-5? → no — pair already 5-5, adding a 3rd Tong-5
        //     would NOT form 4 sets + pair. Need: 3 chows + 1 kong + pair
        //     on Tong-5. Let me restructure.
        //
        // Working structure (kong + 3 chows + pair):
        //   Pre-kong: 4× Tiao 9 + Wan 1-2-3 + Wan 4-5-6 + Tong 1-2-3 + Tong 5
        //     = 4 + 3 + 3 + 3 + 1 = 14 tiles
        //   After kong: concealed = Wan 1-2-3 + Wan 4-5-6 + Tong 1-2-3 + Tong 5
        //     = 10 tiles; melds = [ConcealedKong(Tiao-9)]
        //   Need replacement = Tong-5 to complete: 3 chows + pair Tong-5 + kong
        //     = valid 258 hand.
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

        // Other seats: empty (no claim-window interference).
        for (var i = 1; i < 4; i++)
        {
            state.Hands[i].ConcealedTiles.Clear();
            state.Hands[i].Melds.Clear();
        }

        // Replacement tile from back of wall. DrawFromBack pops state.Wall[^1].
        // For the winning case we need a SECOND Tong-5 (the wait); for the
        // non-winning case we need any benign tile (Tong-9 picked — irrelevant to
        // the structure).
        var replacementTile = winningReplacement
            ? Tid(Suit.Tong, 5, 1)
            : Tid(Suit.Tong, 9, 0);
        // Ensure no stale copy of the replacement is anywhere else.
        foreach (var h in state.Hands)
            h.ConcealedTiles.RemoveAll(t => t == replacementTile);
        state.Wall.RemoveAll(t => t == replacementTile);
        state.Wall.Add(replacementTile);
        state.WallBackIndex = state.Wall.Count - 1;

        return state;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Hand-override helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replace seat <paramref name="seatIndex"/>'s concealed tiles with the
    /// provided 14-tile composition (copies auto-assigned by occurrence count).
    /// Wipes melds. Used to set up a known winning/discarding hand atop a dealt
    /// game without re-running the deal.
    /// </summary>
    private static void OverrideHandWith14Tiles(ChangshaGameState state, int seatIndex,
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

    /// <summary>Place 13 specific tile ids into a seat's hand (clearing prior).</summary>
    private static void OverrideHandWith13Waiting(ChangshaGameState state, int seatIndex,
        IEnumerable<int> tileIds)
    {
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    /// <summary>Strip every tile of a given logical id from all hands and the wall.</summary>
    private static void StripLogicalFromState(ChangshaGameState state, int logicalTile)
    {
        foreach (var h in state.Hands)
            h.ConcealedTiles.RemoveAll(t => t / 4 == logicalTile);
        state.Wall.RemoveAll(t => t / 4 == logicalTile);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Reflection probes for Bishop's Phase I Wave 1 contracts
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve a <see cref="WinPattern"/> enum value added in Phase I Wave 1
    /// (HeavenlyHand / EarthlyHand / LastTileFromWall / LastDiscardCatch /
    /// KongReplacementWin). Fails RED with a descriptive contract message when
    /// Bishop hasn't yet added the enum value.
    /// </summary>
    internal static WinPattern ResolveSpecialPatternEnum(string name, string chinese, string trigger)
    {
        var names = Enum.GetNames(typeof(WinPattern));
        if (!names.Contains(name))
        {
            throw new InvalidOperationException(
                $"WinPattern.{name} ({chinese}) enum value not defined — Bishop owes the " +
                $"Phase I Wave 1 contract. Trigger: {trigger}. " +
                $"Current values: [{string.Join(",", names)}].");
        }
        return (WinPattern)Enum.Parse(typeof(WinPattern), name);
    }

    /// <summary>
    /// Read <c>ChangshaGameState.LastDrawWasKongReplacement</c> by reflection so
    /// this assembly compiles before Bishop ships the property. Returns false
    /// when the property is missing — the caller's assertion message names the
    /// contract owed.
    /// </summary>
    internal static bool GetLastDrawWasKongReplacement(ChangshaGameState state)
    {
        var prop = typeof(ChangshaGameState).GetProperty("LastDrawWasKongReplacement",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return false;
        return (bool)(prop.GetValue(state) ?? false);
    }
}
