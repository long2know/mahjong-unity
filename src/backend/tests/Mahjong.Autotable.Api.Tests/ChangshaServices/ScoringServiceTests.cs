using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class ScoringServiceTests
{
    private readonly ScoringService _svc = new();

    [Fact]
    public void SmallWin_SelfDraw_NonDealer_DealerPays2_OthersPay1()
    {
        var win = new WinResult
        {
            WinningSeatIndex = 1,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.Standard,
            WinningTileId = 0,
            SourceSeatIndex = 1
        };

        var result = _svc.CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Equal(ScoreCategory.SmallWin, result.Category);
        Assert.Equal(3, result.Payments.Count);

        // Spec §5.1: Small Win self-draw — dealer pays 2, other non-dealers pay 1.
        var dealerPayment = Assert.Single(result.Payments, p => p.FromSeatIndex == 0);
        Assert.Equal(2, dealerPayment.Amount);
        foreach (var p in result.Payments.Where(p => p.FromSeatIndex != 0))
        {
            Assert.Equal(1, p.Amount);
        }
    }

    [Fact]
    public void SmallWin_Discard_NonDealerToNonDealer_Pays1()
    {
        var win = new WinResult
        {
            WinningSeatIndex = 1,
            Method = WinMethod.Discard,
            Pattern = WinPattern.Standard,
            WinningTileId = 0,
            SourceSeatIndex = 2 // non-dealer discards to non-dealer winner
        };

        var result = _svc.CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Single(result.Payments);
        Assert.Equal(1, result.Payments[0].Amount);
    }

    [Fact]
    public void SmallWin_Discard_DealerInvolved_Pays2()
    {
        var win = new WinResult
        {
            WinningSeatIndex = 0, // dealer wins
            Method = WinMethod.Discard,
            Pattern = WinPattern.Standard,
            WinningTileId = 0,
            SourceSeatIndex = 1
        };

        var result = _svc.CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Single(result.Payments);
        Assert.Equal(2, result.Payments[0].Amount);
    }

    [Fact]
    public void BigWin_SelfDraw_NonDealer_Pays3Or4()
    {
        var win = new WinResult
        {
            WinningSeatIndex = 1,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.AllPungs,
            WinningTileId = 0,
            SourceSeatIndex = 1
        };

        var result = _svc.CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Equal(3, result.Payments.Count);

        // Dealer pays 4 (dealer involved), others pay 3
        var dealerPayment = result.Payments.Single(p => p.FromSeatIndex == 0);
        Assert.Equal(4, dealerPayment.Amount);

        var otherPayments = result.Payments.Where(p => p.FromSeatIndex != 0).ToList();
        Assert.All(otherPayments, p => Assert.Equal(3, p.Amount));
    }

    [Fact]
    public void BigWin_Discard_NonDealer_Pays6()
    {
        var win = new WinResult
        {
            WinningSeatIndex = 1,
            Method = WinMethod.Discard,
            Pattern = WinPattern.SevenPairs,
            WinningTileId = 0,
            SourceSeatIndex = 2 // non-dealer to non-dealer
        };

        var result = _svc.CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Single(result.Payments);
        Assert.Equal(6, result.Payments[0].Amount);
    }

    [Fact]
    public void BigWin_Discard_DealerInvolved_Pays7()
    {
        var win = new WinResult
        {
            WinningSeatIndex = 0, // dealer wins from discard
            Method = WinMethod.Discard,
            Pattern = WinPattern.AllPungs,
            WinningTileId = 0,
            SourceSeatIndex = 1
        };

        var result = _svc.CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Single(result.Payments);
        Assert.Equal(7, result.Payments[0].Amount);
    }

    [Fact]
    public void FullFlush_BigWin_FlatPayment_NoDoubling()
    {
        var win = new WinResult
        {
            WinningSeatIndex = 1,
            Method = WinMethod.Discard,
            Pattern = WinPattern.FullFlush,
            WinningTileId = 0,
            SourceSeatIndex = 2
        };

        var result = _svc.CalculateScore(win, dealerSeatIndex: 0, isFullFlush: true);
        Assert.Single(result.Payments);
        // Spec §5.1 (v1): Big Win categories pay a flat amount; Full Flush does NOT double.
        Assert.Equal(6, result.Payments[0].Amount);
    }
}
