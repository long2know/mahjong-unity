using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance/characterization: Fan-catalog folding into <see cref="ChangshaGameStateMachine.Score"/>
/// under the opt-in <see cref="ChangshaScoringOptions.HouseRules"/> mode.
///
/// <para><b>#117 note:</b> the additive fan layer (fan points folded into the payment
/// rows with a <c>"fan:"</c> <c>Reason</c> prefix) is NOT applied on the default
/// spec-pure path — spec §5.1 has no fan/stacking table, so live payments stay at the
/// §5.1 magnitude and the fan catalog is query-only (surfaced on
/// <see cref="ScoreResult.Fans"/> / <see cref="ScoreResult.FanPoints"/> for display).
/// This suite drives <c>Score(state, ChangshaScoringOptions.HouseRules)</c> so it pins
/// the pre-#117 fan-folding contract as characterization — nothing is silently lost,
/// and a future tournament option that flips the flag keeps a green guardrail.</para>
///
/// <para>The house-rules fan layer is ADDITIVE on top of the existing 258-pair
/// small/big-win tier, surfaced via:</para>
///
/// <list type="bullet">
///   <item><see cref="ScoreResult.Fans"/> — every detected fan in deterministic
///         <see cref="Fan"/>-enum-declaration order.</item>
///   <item><see cref="ScoreResult.FanPoints"/> — sum of per-payment fan points.</item>
///   <item><see cref="ScoreResult.BasePoints"/> — base small/big-win payout PLUS the
///         fan bonus distributed across every base payment (so the total still equals
///         <c>Payments.Sum(p =&gt; p.Amount)</c>; zero-sum holds).</item>
///   <item><see cref="ScoreResult.Payments"/> — base payments first, then one
///         fan-bonus row per (existing-payment × detected-fan), with
///         <c>Reason</c> prefix <c>"fan:"</c>.</item>
///   <item><see cref="ScoreResultEntry"/> wire shape — <c>fans</c> + <c>fanPoints</c>
///         optional fields. Legacy clients that ignore them continue to work.</item>
/// </list>
/// </summary>
public class FanCatalogIntegrationTests
{
    // ────────────────────────────────────────────────────────────────────
    //  1. 自摸 — SelfDraw win pulls in the SelfDraw fan bonus
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "FanCatalog")]
    public void SelfDrawHu_AddsSelfDrawFanBonusOnTopOfBaseScore()
    {
        // Dealer (seat 0) self-draws a deterministic 14-tile Standard winning shape
        // with NO contextual fans (wall non-empty so 海底捞月 stays false; a benign
        // prior discard suppresses 天和; no melds means 门清 ALSO fires).
        //
        // Expected fans: SelfDraw (1) + ConcealedHand (1) = 2 per payment.
        // Base score: dealer self-draw SmallWin = SmallWinSelfDrawDealer (2) per opp
        //   × 3 opponents = 6.
        // Fan bonus: 2 fan points × 3 base payments = 6.
        // BasePoints total: 6 (base) + 6 (fans) = 12. Zero-sum: every payment is a
        //   (from, to, amount) triple so CumulativeScores.Values.Sum() == 0.
        var state = BuildPostDealState(dealerSeat: 0);
        SuppressFirstDiscardContext(state);

        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));
        ClearOtherHands(state, keepSeat: 0);

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);
        Assert.Equal(ChangshaPhase.Scoring, state.Phase);

        ChangshaGameStateMachine.Score(state, ChangshaScoringOptions.HouseRules);
        var score = state.CurrentScore!;

        // Fan catalog assertions.
        Assert.Contains(score.Fans, f => f.Fan == Fan.SelfDraw);
        Assert.Contains(score.Fans, f => f.Fan == Fan.ConcealedHand);
        Assert.Equal(1 + 1, score.FanPoints);

        // Base payment shape — preserved by the integration (3 base payments,
        // each 2 points per spec §5 SmallWin dealer-self-draw).
        var baseRows = score.Payments.Where(p => !p.Reason.StartsWith("fan:")).ToList();
        Assert.Equal(3, baseRows.Count);
        Assert.All(baseRows, p => Assert.Equal(2, p.Amount));

        // Fan-bonus rows: 3 base × 2 detected fans = 6 fan-bonus rows.
        var fanRows = score.Payments.Where(p => p.Reason.StartsWith("fan:")).ToList();
        Assert.Equal(6, fanRows.Count);
        Assert.All(fanRows, p => Assert.True(p.Amount == 1,
            $"each Changsha SelfDraw / ConcealedHand fan is 1 point per payment. Got {p.Amount}."));

        // BasePoints includes the fan bonus.
        Assert.Equal(6 + 6, score.BasePoints);
        Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);

        // Zero-sum preserved across the table (Vasquez §5).
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        // Bot-friendly: the fan-bonus reason carries the camelCase fan id so the
        // wire surface can render localised labels via FanCatalog.Get.
        Assert.Contains(fanRows, p => p.Reason == "fan:selfDraw");
        Assert.Contains(fanRows, p => p.Reason == "fan:concealedHand");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. 杠上开花 — Kong-replacement self-draw stacks the KongReplacement fan
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "FanCatalog")]
    public void KongReplacementSelfDraw_AddsKongReplacementFanBonus()
    {
        // Dealer (seat 0) declares a concealed kong (4×Tiao-9), draws the replacement
        // from the back of the wall (we plant Tong-5 there), and self-draws Hu.
        //
        // Expected fans (Changsha-variant):
        //   SelfDraw (1) — self-drew via replacement
        //   KongReplacement (2) — 杠上开花
        //   ConcealedHand — does NOT fire (a concealed kong IS allowed for 门清 but
        //     the FanCalculator's `IsConcealedHand` predicate counts concealed kongs as
        //     concealed, so this DOES fire — see FanCalculator.cs:225)
        // Per FanCalculator.IsConcealedHand: 门清 requires every meld to be a concealed
        // kong, which is satisfied here. So ConcealedHand fires too (1 point).
        //
        // The 14-tile shape after the kong replacement draw is: concealed kong of Tiao-9
        // (1 meld), 9 chow tiles, Tong-5 pair. Pattern = Standard (258 pair Tong-5 ✓),
        // SmallWin. Base: dealer self-draw SmallWin = 2 × 3 = 6.
        // Fans: SelfDraw(1) + KongReplacement(2) + ConcealedHand(1) = 4 per payment ×
        //       3 base payments = 12. BasePoints total = 18.
        var state = BuildKongReplacementWinScenario(dealerSeat: 0);

        ChangshaGameStateMachine.DeclareConcealedKong(state, seatIndex: 0,
            logicalTile: Logical(Suit.Tiao, 9));
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 0);
        Assert.Equal(ChangshaPhase.Scoring, state.Phase);

        // Pre-Score: sanity check the win record carries the kong-replacement axis.
        Assert.True(state.CurrentWin!.IsKongReplacement,
            "Win must record IsKongReplacement=true after a kong-replacement self-draw " +
            "for the fan layer to detect KongReplacement.");
        Assert.True(state.CurrentWin.IsSelfDraw,
            "Win must record IsSelfDraw=true after a kong-replacement self-draw.");

        ChangshaGameStateMachine.Score(state, ChangshaScoringOptions.HouseRules);
        var score = state.CurrentScore!;

        // Both contextual fans must be detected.
        Assert.Contains(score.Fans, f => f.Fan == Fan.SelfDraw);
        Assert.Contains(score.Fans, f => f.Fan == Fan.KongReplacement);
        Assert.Contains(score.Fans, f => f.Fan == Fan.ConcealedHand);
        // FanPoints = 1 (SelfDraw) + 2 (KongReplacement) + 1 (ConcealedHand) = 4.
        Assert.Equal(4, score.FanPoints);

        // BasePoints includes the fan bonus distributed across the 3 base payments.
        // (Specific point totals are encoded above; the key invariant for this test
        // is the fan firing + the bonus being layered onto every base row.)
        var fanRows = score.Payments.Where(p => p.Reason.StartsWith("fan:")).ToList();
        Assert.Equal(3 * 3, fanRows.Count); // 3 detected fans × 3 base payments
        Assert.Contains(fanRows, p => p.Reason == "fan:kongReplacement" && p.Amount == 2);

        Assert.Equal(score.Payments.Sum(p => p.Amount), score.BasePoints);
        Assert.Equal(0, state.CumulativeScores.Values.Sum());
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Wire shape — ScoreResult.Fans surfaces through the translator
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "FanCatalog")]
    public void ScoreResult_FanBreakdown_RoundTripsThroughBundleTranslator()
    {
        // Drive a self-draw win through ChangshaGameStateMachine.Score, then translate
        // the resulting state to a HandResultEntry (the shape Ferro's win-screen modal
        // consumes via the WS UPDATE pipe). The fan breakdown must arrive intact with
        // Chinese / Pinyin / English labels rehydrated from FanCatalog.
        var state = BuildPostDealState(dealerSeat: 0);
        SuppressFirstDiscardContext(state);

        // Non-dealer (seat 1) self-draws an AllPungs hand — guarantees AllPungs + SelfDraw
        // + ConcealedHand fans fire, giving the wire shape a multi-fan payload.
        state.ActiveSeatIndex = 1;
        OverrideConcealedWith14(state, seatIndex: 1,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tong, 5), (Suit.Tong, 5));
        ClearOtherHands(state, keepSeat: 1);

        var hand = state.Hands[1].ConcealedTiles;
        var drawnTile = hand[^1];
        hand.RemoveAt(hand.Count - 1);
        state.Wall.RemoveAll(tile => tile == drawnTile);
        state.Wall.Insert(0, drawnTile);
        state.WallBackIndex = state.Wall.Count - 1;
        Assert.Equal(13, hand.Count);
        ChangshaGameStateMachine.DrawTile(state);
        Assert.Equal(1, state.LastDrawSeatIndex);
        Assert.Equal(drawnTile, hand[^1]);
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 1);
        ChangshaGameStateMachine.Score(state, ChangshaScoringOptions.HouseRules);

        Assert.Equal(ChangshaPhase.EndHand, state.Phase);

        var translated = ChangshaToAutotableTranslator.BuildHandResult(state);
        Assert.NotNull(translated);
        Assert.NotNull(translated!.ScoreResult);

        var scoreEntry = translated.ScoreResult!;
        Assert.NotEmpty(scoreEntry.Fans);
        Assert.True(scoreEntry.FanPoints > 0,
            "FanPoints must be non-zero when AllPungs + SelfDraw + ConcealedHand all fire.");

        // Wire-id camelCase + label rehydration.
        var selfDrawEntry = scoreEntry.Fans.SingleOrDefault(f => f.Fan == "selfDraw");
        Assert.NotNull(selfDrawEntry);
        Assert.Equal("自摸", selfDrawEntry!.Chinese);
        Assert.Equal("zì mō", selfDrawEntry.Pinyin);
        Assert.Equal("Self-draw", selfDrawEntry.English);
        Assert.Equal(1, selfDrawEntry.Points);

        var allPungsEntry = scoreEntry.Fans.SingleOrDefault(f => f.Fan == "allPungs");
        Assert.NotNull(allPungsEntry);
        Assert.Equal("碰碰胡", allPungsEntry!.Chinese);
        Assert.Equal(4, allPungsEntry.Points);

        var concealedEntry = scoreEntry.Fans.SingleOrDefault(f => f.Fan == "concealedHand");
        Assert.NotNull(concealedEntry);
        Assert.Equal("门清", concealedEntry!.Chinese);

        // The "category" and "basePoints" channels remain backward compatible —
        // legacy clients keep working.
        Assert.Equal("bigWin", scoreEntry.Category);
        Assert.True(scoreEntry.BasePoints > 0);
        Assert.NotEmpty(scoreEntry.Payments);

        // Reason channel on the base payments also exposes the fan ids so a UI that
        // prefers the flat payment list (over the structured Fans breakdown) can
        // render the same information.
        Assert.Contains(scoreEntry.Payments, p => p.Reason.StartsWith("fan:"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Scenario builders
    // ────────────────────────────────────────────────────────────────────

    private static ChangshaGameState BuildPostDealState(int dealerSeat)
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
    /// Add a benign prior discard so the HeavenlyHand gate
    /// (<c>DiscardPile.Count == 0</c>) does NOT fire when the dealer self-draws.
    /// Lets the test isolate the SelfDraw + ConcealedHand fans without HeavenlyHand
    /// inflation.
    /// </summary>
    private static void SuppressFirstDiscardContext(ChangshaGameState state)
    {
        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = (state.DealerSeatIndex + 1) % 4,
            TileId = Tid(Suit.Tiao, 8, 0),
            TurnNumber = 1
        });
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

    private static void ClearOtherHands(ChangshaGameState state, int keepSeat)
    {
        for (var i = 0; i < 4; i++)
        {
            if (i == keepSeat) continue;
            state.Hands[i].ConcealedTiles.Clear();
            state.Hands[i].Melds.Clear();
        }
    }

    /// <summary>
    /// Kong-replacement self-draw fixture: seat 0 holds 4×Tiao-9 (kong) + 10 chow
    /// tiles + Tong-5 single; back of wall pre-planted with another Tong-5 so the
    /// replacement draw completes the hand (Standard 258 ✓ via Tong-5 pair).
    /// </summary>
    private static ChangshaGameState BuildKongReplacementWinScenario(int dealerSeat)
    {
        var state = BuildPostDealState(dealerSeat);
        SuppressFirstDiscardContext(state);

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
        state.Hands[dealerSeat].ConcealedTiles.Clear();
        state.Hands[dealerSeat].Melds.Clear();
        state.Hands[dealerSeat].ConcealedTiles.AddRange(seat0Tiles);

        for (var i = 0; i < 4; i++)
        {
            if (i == dealerSeat) continue;
            state.Hands[i].ConcealedTiles.Clear();
            state.Hands[i].Melds.Clear();
        }

        var replacementTile = Tid(Suit.Tong, 5, 1);
        state.Wall.RemoveAll(t => t == replacementTile);
        state.Wall.Add(replacementTile);
        state.WallBackIndex = state.Wall.Count - 1;
        return state;
    }
}
