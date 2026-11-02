using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-G: Scoring tests per Changsha spec §5.1.
///
/// Spec payment table (v1):
///   Small Win self-draw: each non-winner pays 1 (or 2 if dealer involved)
///   Small Win discard:   discarder pays 2 (or 3 if dealer involved)  → simplified to 1/2 per spec lock
///   Big Win   self-draw: each pays 3 (or 4 if dealer involved)
///   Big Win   discard:   discarder pays 6 (or 7 if dealer involved)
///   Full Flush: SINGLE-tier Big Win, no doubling.
/// </summary>
public class ScoringTests
{
    private static WinResult MakeWin(int winnerSeat, int sourceSeat, WinMethod method,
        WinPattern pattern, bool fullFlush = false)
        => new()
        {
            WinningSeatIndex = winnerSeat,
            SourceSeatIndex = sourceSeat,
            Method = method,
            Pattern = pattern,
            WinningTileId = 0,
            IsFullFlush = fullFlush
        };

    [Fact, Trait("Category", "Changsha")]
    public void SmallWin_NonDealerSelfDraw_DealerPays2_OthersPay1()
    {
        // Non-dealer (seat 2) self-draws Standard. Dealer (seat 0) involvement → pays 2.
        // Other non-dealers pay 1 each. Total = 2 + 1 + 1 = 4.
        // FAIL (expected) — see Bishop bug 1: ScoringService applies a flat SmallWinSelfDrawBase=2.
        var win = MakeWin(winnerSeat: 2, sourceSeat: 2, WinMethod.SelfDraw, WinPattern.Standard);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        Assert.Equal(ScoreCategory.SmallWin, result.Category);

        var fromDealer = result.Payments.Single(p => p.FromSeatIndex == 0).Amount;
        var fromOther1 = result.Payments.Single(p => p.FromSeatIndex == 1).Amount;
        var fromOther3 = result.Payments.Single(p => p.FromSeatIndex == 3).Amount;

        Assert.Equal(2, fromDealer);
        Assert.Equal(1, fromOther1);
        Assert.Equal(1, fromOther3);
    }

    [Fact, Trait("Category", "Changsha")]
    public void SmallWin_DealerSelfDraw_AllOthersPay2()
    {
        // Dealer (seat 0) self-draws Standard. Each non-dealer pays 2 (dealer involved).
        var win = MakeWin(winnerSeat: 0, sourceSeat: 0, WinMethod.SelfDraw, WinPattern.Standard);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        Assert.Equal(ScoreCategory.SmallWin, result.Category);
        Assert.All(result.Payments, p => Assert.Equal(2, p.Amount));
        Assert.Equal(3, result.Payments.Count);
    }

    [Fact, Trait("Category", "Changsha")]
    public void SmallWin_DiscardFromNonDealer_DiscarderPays1()
    {
        // Seat 1 (non-dealer) wins on seat 2's (non-dealer) discard. No dealer involved → 1.
        var win = MakeWin(winnerSeat: 1, sourceSeat: 2, WinMethod.Discard, WinPattern.Standard);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        Assert.Single(result.Payments);
        Assert.Equal(1, result.Payments[0].Amount);
        Assert.Equal(2, result.Payments[0].FromSeatIndex);
        Assert.Equal(1, result.Payments[0].ToSeatIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void SmallWin_DiscardWhenDealerInvolved_DiscarderPays2()
    {
        // Dealer (seat 0) discards, seat 2 wins → dealer involved → 2.
        var win = MakeWin(winnerSeat: 2, sourceSeat: 0, WinMethod.Discard, WinPattern.Standard);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        Assert.Single(result.Payments);
        Assert.Equal(2, result.Payments[0].Amount);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BigWin_NonDealerSelfDraw_DealerPays4_OthersPay3()
    {
        // Non-dealer (seat 1) self-draws AllPungs.
        var win = MakeWin(winnerSeat: 1, sourceSeat: 1, WinMethod.SelfDraw, WinPattern.AllPungs);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        Assert.Equal(ScoreCategory.BigWin, result.Category);

        Assert.Equal(4, result.Payments.Single(p => p.FromSeatIndex == 0).Amount);
        Assert.Equal(3, result.Payments.Single(p => p.FromSeatIndex == 2).Amount);
        Assert.Equal(3, result.Payments.Single(p => p.FromSeatIndex == 3).Amount);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BigWin_NonDealerWinsFromDiscard_DiscarderPays6()
    {
        // Non-dealer wins on non-dealer discard, AllPungs (no flush).
        var win = MakeWin(winnerSeat: 1, sourceSeat: 2, WinMethod.Discard, WinPattern.AllPungs);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Equal(6, result.Payments[0].Amount);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BigWin_DealerWinsFromDiscard_DiscarderPays7()
    {
        // Dealer wins on a non-dealer discard, AllPungs.
        var win = MakeWin(winnerSeat: 0, sourceSeat: 2, WinMethod.Discard, WinPattern.AllPungs);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        Assert.Equal(7, result.Payments[0].Amount);
    }

    [Fact, Trait("Category", "Changsha")]
    public void FullFlush_BigWin_SingleTier_NoDoubling()
    {
        // Per spec lock: Full Flush is a single-tier Big Win — no x2 multiplier.
        // FAIL (expected) — see Bishop bug 2: ScoringService applies flushMultiplier=2 for big-win flush.
        var win = MakeWin(winnerSeat: 1, sourceSeat: 2, WinMethod.Discard, WinPattern.FullFlush, fullFlush: true);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: true);

        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Equal(6, result.Payments[0].Amount); // not 12
    }

    [Fact, Trait("Category", "Changsha")]
    public void Score_PaymentsBalance_ZeroSum()
    {
        // Payments must net to zero across the table.
        var win = MakeWin(winnerSeat: 2, sourceSeat: 2, WinMethod.SelfDraw, WinPattern.Standard);
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);

        var totals = new int[4];
        foreach (var p in result.Payments)
        {
            totals[p.FromSeatIndex] -= p.Amount;
            totals[p.ToSeatIndex]   += p.Amount;
        }
        Assert.Equal(0, totals.Sum());
    }
}
