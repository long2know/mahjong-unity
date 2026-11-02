using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Scoring;

/// <summary>
/// Characterization — the <see cref="ChangshaScoringOptions"/> gate introduced by
/// issue #117. Pins BOTH sides of the switch for identical winning hands so the
/// spec-pure default AND the pre-#117 "fan-on / stacking" magnitudes are captured
/// side-by-side and nothing is silently lost:
///
/// <list type="bullet">
///   <item><see cref="ChangshaScoringOptions.SpecPure"/> (default) — payments equal the
///         binding §5.1 table; the fan catalog is surfaced on Fans/FanPoints for display
///         but NOT folded into payments; Big Wins do NOT stack.</item>
///   <item><see cref="ChangshaScoringOptions.HouseRules"/> (opt-in) — the pre-#117 live
///         behaviour: fan points folded into every base payment (with <c>"fan:"</c>
///         reason rows) and Big-Win stacking (×Clamp(count,1,3)).</item>
/// </list>
///
/// <para>These tests document the exact divergence the issue flagged: e.g. a plain
/// Standard-258 self-draw pays the §5.1 base under spec-pure but is inflated by the fan
/// layer under house-rules.</para>
/// </summary>
public class ScoringOptionsCharacterizationTests
{
    // ── Case A — Standard-258 non-dealer self-draw (concealed) ────────────────────
    // Fans fired: SelfDraw (1) + ConcealedHand (1) = 2 FanPoints.
    // Winner seat 1 (non-dealer), dealer seat 0.

    [Fact, Trait("Category", "ScoringCharacterization"), Trait("Wave", "117-SpecReconciliation")]
    public void StandardSelfDraw_SpecPure_PaysBinding51Base_FansQueryOnly()
    {
        var state = DriveNonDealerSelfDrawStandard(ChangshaScoringOptions.SpecPure);
        var score = state.CurrentScore!;

        Assert.Equal(ScoreCategory.SmallWin, score.Category);

        // §5.1 Example 1 base: dealer (seat 0) pays 2, other non-dealers pay 1 each.
        Assert.Equal(2, PaymentFrom(score, seat: 0));
        Assert.Equal(1, PaymentFrom(score, seat: 2));
        Assert.Equal(1, PaymentFrom(score, seat: 3));
        Assert.Equal(4, state.CumulativeScores[1]);   // winner banks 1 + 1 + 2 = 4.

        // Fan catalog surfaced for display, but NOT folded into the money.
        Assert.Contains(score.Fans, f => f.Fan == Fan.SelfDraw);
        Assert.Contains(score.Fans, f => f.Fan == Fan.ConcealedHand);
        Assert.Equal(2, score.FanPoints);
        Assert.DoesNotContain(score.Payments, p => p.Reason.StartsWith("fan:"));
        Assert.Equal(4, score.BasePoints);             // base only — FanPoints not added.
        Assert.Equal(0, state.CumulativeScores.Values.Sum());
    }

    [Fact, Trait("Category", "ScoringCharacterization"), Trait("Wave", "117-SpecReconciliation")]
    public void StandardSelfDraw_HouseRules_FoldsFansIntoPayments_PreFixMagnitude()
    {
        var state = DriveNonDealerSelfDrawStandard(ChangshaScoringOptions.HouseRules);
        var score = state.CurrentScore!;

        Assert.Equal(ScoreCategory.SmallWin, score.Category);

        // Pre-#117 fan-on magnitude: each base payment += 2 fan points (SelfDraw + Concealed).
        // dealer 2+2 = 4, other non-dealers 1+2 = 3 each → winner banks 4 + 3 + 3 = 10.
        Assert.Equal(10, state.CumulativeScores[1]);
        Assert.Equal(2, score.FanPoints);
        Assert.Contains(score.Payments, p => p.Reason == "fan:selfDraw");
        Assert.Contains(score.Payments, p => p.Reason == "fan:concealedHand");
        Assert.Equal(10, score.BasePoints);
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        // The whole point of #117: house-rules pays MORE than the binding spec base (4).
        Assert.True(state.CumulativeScores[1] > 4);
    }

    // ── Case B — AllPungs + FullFlush non-dealer self-draw (stacking) ─────────────
    // Fans fired: SelfDraw (1) + FullFlush (6) + AllPungs (4) + ConcealedHand (1) = 12.
    // AllPatterns = [AllPungs, FullFlush] → stacking count 2. Winner seat 1, dealer 0.

    [Fact, Trait("Category", "ScoringCharacterization"), Trait("Wave", "117-SpecReconciliation")]
    public void StackedBigWinSelfDraw_SpecPure_NoStacking_NoFanFold()
    {
        var state = DriveNonDealerSelfDrawAllPungsFullFlush(ChangshaScoringOptions.SpecPure);
        var score = state.CurrentScore!;

        Assert.Equal(ScoreCategory.BigWin, score.Category);
        Assert.Equal(2, state.CurrentWin!.AllPatterns.Count); // detector still stacks-aware.

        // §5.1 Big Win self-draw base, NO ×2 stacking: dealer 4, others 3 → banks 10.
        Assert.Equal(4, PaymentFrom(score, seat: 0));
        Assert.Equal(3, PaymentFrom(score, seat: 2));
        Assert.Equal(3, PaymentFrom(score, seat: 3));
        Assert.Equal(10, state.CumulativeScores[1]);

        Assert.Equal(12, score.FanPoints);            // surfaced (display), not folded.
        Assert.DoesNotContain(score.Payments, p => p.Reason.StartsWith("fan:"));
        Assert.Equal(10, score.BasePoints);
        Assert.Equal(0, state.CumulativeScores.Values.Sum());
    }

    [Fact, Trait("Category", "ScoringCharacterization"), Trait("Wave", "117-SpecReconciliation")]
    public void StackedBigWinSelfDraw_HouseRules_StacksAndFolds_PreFixMagnitude()
    {
        var state = DriveNonDealerSelfDrawAllPungsFullFlush(ChangshaScoringOptions.HouseRules);
        var score = state.CurrentScore!;

        Assert.Equal(ScoreCategory.BigWin, score.Category);

        // Pre-#117 magnitude: base ×2 stacking (dealer 8, others 6 = 20) PLUS fan fold
        // (12 FanPoints × 3 payments = 36) → winner banks 56.
        Assert.Equal(12, score.FanPoints);
        Assert.Equal(56, state.CumulativeScores[1]);
        Assert.Equal(56, score.BasePoints);
        Assert.Contains(score.Payments, p => p.Reason == "fan:fullFlush");
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        // House-rules dwarfs the binding spec base (10).
        Assert.True(state.CumulativeScores[1] > 10);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────

    private static int PaymentFrom(ScoreResult score, int seat) =>
        score.Payments.Where(p => p.FromSeatIndex == seat && !p.Reason.StartsWith("fan:"))
            .Sum(p => p.Amount);

    private static ChangshaGameState DriveNonDealerSelfDrawStandard(ChangshaScoringOptions options)
    {
        var state = BuildPostDealState(dealerSeat: 0);
        // Seat 1 (non-dealer) self-draws a concealed Standard hand with a 258 (Tong-5) pair.
        ArrangeThirteenAndDraw(state, seatIndex: 1,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 1);
        ChangshaGameStateMachine.Score(state, options);
        return state;
    }

    private static ChangshaGameState DriveNonDealerSelfDrawAllPungsFullFlush(ChangshaScoringOptions options)
    {
        var state = BuildPostDealState(dealerSeat: 0);
        // Seat 1 self-draws an all-Wan AllPungs hand → AllPungs + FullFlush stack.
        ArrangeThirteenAndDraw(state, seatIndex: 1,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 4), (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 7), (Suit.Wan, 7), (Suit.Wan, 7),
            (Suit.Wan, 2), (Suit.Wan, 2));
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 1);
        ChangshaGameStateMachine.Score(state, options);
        return state;
    }

    private static ChangshaGameState BuildPostDealState(int dealerSeat)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 42, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.MissedWinSeats.Clear();
        return state;
    }

    private static void ArrangeThirteenAndDraw(ChangshaGameState state, int seatIndex,
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
        var hand = state.Hands[seatIndex].ConcealedTiles;
        Assert.Equal(13, hand.Count);
        Assert.Equal(14, tileIds.Count);
        for (var index = 0; index < hand.Count; index++)
            SwapInto(state, hand, index, tileIds[index]);
        SwapInto(state, state.Wall, 0, tileIds[^1]);

        var dealer = state.DealerSeatIndex;
        ChangshaGameStateMachine.Discard(state, dealer, state.Hands[dealer].ConcealedTiles[0]);
        if (state.ClaimWindow is not null)
            ChangshaGameStateMachine.PassClaim(state);
        Assert.Equal(seatIndex, state.ActiveSeatIndex);
        Assert.Equal(13, hand.Count);
        Assert.False(ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, seatIndex));

        var draws = ChangshaGameStateMachine.DrawTile(state);
        Assert.Equal(tileIds, hand);
        Assert.Equal(seatIndex, state.LastDrawSeatIndex);
        Assert.Contains(draws, draw => draw.EventType == "tile-drawn"
            && draw.SeatIndex == seatIndex && draw.TileId == tileIds[^1]);
        Assert.Equal(Enumerable.Range(0, 108), state.Wall
            .Concat(state.DiscardPile.Select(discard => discard.TileId))
            .Concat(state.Hands.SelectMany(seat => seat.ConcealedTiles))
            .OrderBy(tile => tile));
    }

    private static void SwapInto(ChangshaGameState state, List<int> target, int index, int tile)
    {
        var source = state.Hands.Select(seat => seat.ConcealedTiles).Append(state.Wall)
            .Single(tiles => tiles.Contains(tile));
        var sourceIndex = source.IndexOf(tile);
        (source[sourceIndex], target[index]) = (target[index], source[sourceIndex]);
    }
}
