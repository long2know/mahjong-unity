using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: 258 pair rule for standard wins per Vasquez §1.10 + MahjongPros §"Winning hands"
/// + Baidu §"258将". A standard 4-melds-plus-pair Hu requires the pair to be rank 2, 5, or 8 of
/// any suit. Big Wins (Seven Pairs / All Pungs / Full Flush) are exempt — covered in
/// <see cref="HuValidationBigWinsTests"/>.
/// </summary>
public class HuValidation258Tests
{
    private static readonly ChangshaWinDetector Detector = new();

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Hu_Standard_PairIs258_Accepted(int pairRank)
    {
        // MahjongPros §"258 Generals": pair of 2 / 5 / 8 satisfies the standard-win pair rule.
        // Build a Standard hand: Wan 1-2-3 chow + Wan 4-5-6 chow + Wan 7-8-9 chow + Tong 1-2-3
        // chow + pair of (Tong, pairRank) — assuming pairRank ≠ 1/2/3 we don't collide.
        // Sidestep ambiguity: use Tiao for both the closing chow and the pair carrier when needed.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
            (Suit.Tong, pairRank), (Suit.Tong, pairRank));

        var result = Detector.Detect(hand);

        Assert.True(result.IsWin,
            $"Standard hand with pair rank {pairRank} (258-compliant) must be a valid Hu.");
        Assert.Equal(WinPattern.Standard, result.Pattern);
    }

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public void Hu_Standard_PairIsNon258_Rejected(int pairRank)
    {
        // §1.10: a 4+1 hand whose pair rank is NOT 2/5/8 must FAIL the standard Hu check.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
            (Suit.Tong, pairRank), (Suit.Tong, pairRank));

        var result = Detector.Detect(hand);

        // The hand has structural 4+1 shape but invalid pair → no Standard match.
        Assert.False(result.IsWin && result.Pattern == WinPattern.Standard,
            $"Standard hand with non-258 pair (rank {pairRank}) must be rejected. " +
            $"Detector returned IsWin={result.IsWin}, Pattern={result.Pattern}.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Hu_FromDiscard_258Compliant_AcceptedViaResolveClaim()
    {
        // Vasquez §1.10 + §1.7: the discard-Hu path (点炮胡) must enforce 258 the same way
        // self-draw does. Build a tenpai hand waiting on Wan-1; the rotation will discard Wan-1.
        var state = AcceptanceFixture.NewDealtGame(seed: 23, dealerSeat: 0);
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 1));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 2));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 3));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 4));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 5));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 6));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 7));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 8));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Wan, 9));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 1));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 2));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 3));
        AcceptanceFixture.StripLogicalFromAllHands(state, Logical(Suit.Tong, 5));

        AcceptanceFixture.OverrideHand(state, 1, AcceptanceFixture.ThirteenTileWaitingForWan1().ToArray());

        // Dealer discards Wan-1.
        var dealerHand = state.Hands[0];
        dealerHand.ConcealedTiles.Clear();
        dealerHand.ConcealedTiles.Add(Tid(Suit.Wan, 1, 0));
        for (var i = 1; i < 14; i++)
            dealerHand.ConcealedTiles.Add(Tid(Suit.Tiao, ((i - 1) % 9) + 1, (i - 1) / 9));

        ChangshaGameStateMachine.Discard(state, 0, Tid(Suit.Wan, 1, 0));

        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.Contains(state.ClaimWindow!.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);

        ChangshaGameStateMachine.ResolveClaim(state, 1, TableClaimType.Hu);
        Assert.Equal(ChangshaPhase.Scoring, state.Phase);
        Assert.Equal(1, state.CurrentWin!.WinningSeatIndex);
        Assert.Equal(WinMethod.Discard, state.CurrentWin.Method);
        // Phase I Wave 1 — this scenario (dealer's very first discard, non-dealer Hu)
        // is the canonical 地和 (EarthlyHand) trigger. The hand is still structurally
        // a 258-compliant Standard win — that's what this test originally verified —
        // but the contextual EarthlyHand bonus now takes Pattern precedence over the
        // Standard baseline (per WinDetector + ScoringService.ClassifyWin's reliance
        // on Pattern to derive the Big Win category). Structural acceptance is still
        // covered by reaching Phase=Scoring without throwing on the non-258 strip;
        // the lone assertion change here just updates the headline-pattern expectation.
        Assert.Equal(WinPattern.EarthlyHand, state.CurrentWin.Pattern);
    }
}
