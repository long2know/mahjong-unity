namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Scoring service for Changsha Mahjong per Vasquez §5.
/// Two tiers:
///   Small Win: self-draw = 2 (each pays 2), discard = 1 from discarder + 2 from dealer/discarder if dealer involved
///   Big Win:   self-draw = 4 (each pays 4), discard = 6 from discarder + 7 if dealer
///
/// Per the spec's payment table (dealer bonus baked in):
///   Small Win self-draw: each other player pays 2 to winner
///   Small Win discard:   discarder pays 2 to winner (1 base + 1 discarder penalty)
///                        BUT if discarder is dealer → pays 2; if winner is dealer → discarder pays 2
///   Big Win self-draw:   each other player pays 4 to winner (3 base + 1 dealer bonus if applicable)
///   Big Win discard:     discarder pays 6 to winner (non-dealer) or 7 (dealer involved)
///
/// Simplified scoring table from spec:
///   Small Win = 1/2 → non-dealer discard = 1, self-draw per player = 2
///   Big Win   = 6/7 discard, 3/4 self-draw
///
/// Payment interpretation:
///   Small Win discard:   discarder pays 1 (non-dealer→non-dealer), 2 (dealer involved)
///   Small Win self-draw: each pays 2
///   Big Win discard:     discarder pays 6 (non-dealer→non-dealer), 7 (dealer involved)
///   Big Win self-draw:   each pays 3 (non-dealer→non-dealer), 4 (dealer involved)
///
/// Phase H Wave 2 — Big Win stacking multiplier (per Ripley's design memo §2.3),
/// gated OFF by default since issue #117 (spec §5.1 has NO stacking table):
///   Big Win patterns simultaneously satisfied → multiplier on base Big Win payment:
///     1 pattern  →  ×1 (spec-pure baseline; the default the live path passes)
///     2 patterns →  ×2
///     3+ patterns →  ×3 (cap)
///   The multiplier is a pure capability of this service (still exercised directly by
///   unit tests and by the opt-in <see cref="ChangshaScoringOptions.HouseRules"/> mode).
///   The live <see cref="ChangshaGameStateMachine.Score"/> path passes
///   <c>bigWinPatternCount = 1</c> unless house-rule stacking is enabled, so spec §5.1
///   magnitudes hold by default. Small Wins never stack.
/// </summary>
public interface IScoringService
{
    /// <summary>
    /// Legacy entry point — single big-win pattern (×1 multiplier). Preserved verbatim so
    /// pre-Phase-H-Wave-2 callers keep their behaviour. New callers that have a
    /// <see cref="WinDetectionResult"/> in hand should prefer the overload that takes
    /// <paramref name="bigWinPatternCount"/> directly.
    /// </summary>
    ScoreResult CalculateScore(WinResult win, int dealerSeatIndex, bool isFullFlush);

    /// <summary>
    /// Phase H Wave 2 — Big Win stacking aware overload. <paramref name="bigWinPatternCount"/>
    /// is the size of <see cref="WinDetectionResult.AllPatterns"/> (number of Big Win flags
    /// that fired). Values are clamped to [1, 3] before multiplying the base Big Win payment.
    /// For Small Wins (Standard-only) the multiplier is forced to 1 regardless of input.
    /// </summary>
    ScoreResult CalculateScore(WinResult win, int dealerSeatIndex, bool isFullFlush, int bigWinPatternCount);

    ScoreResult CalculateScore(WinResult win, int dealerSeatIndex, bool isFullFlush, int bigWinPatternCount, int baseUnit);

    /// <summary>
    /// Builds the payment list for a 诈胡 (false-Hu) penalty per Baidu §诈胡处罚 — the
    /// offending seat pays <see cref="ScoringService.FalseHuPenaltyPerOpponent"/> to each
    /// of the other three seats (Big-Win equivalent). Stateless; the caller is responsible
    /// for applying the returned payments to <c>CumulativeScores</c>.
    /// </summary>
    FalseHuPenalty CalculateFalseHuPenalty(int offendingSeatIndex);

    FalseHuPenalty CalculateFalseHuPenalty(int offendingSeatIndex, int baseUnit);
}

public sealed class ScoringService : IScoringService
{
    // Payment table per spec §5
    private const int SmallWinDiscardBase = 1;
    private const int SmallWinDiscardDealer = 2;
    private const int SmallWinSelfDrawBase = 1;
    private const int SmallWinSelfDrawDealer = 2;

    private const int BigWinDiscardBase = 6;
    private const int BigWinDiscardDealer = 7;
    private const int BigWinSelfDrawBase = 3;
    private const int BigWinSelfDrawDealer = 4;

    /// <summary>
    /// 诈胡 (false-Hu) per-opponent penalty: Big-Win equivalent (6 points to each of the
    /// three opponents per Baidu §诈胡处罚). Caller pays 18 total; opponents split evenly.
    /// </summary>
    public const int FalseHuPenaltyPerOpponent = 6;

    public ScoreResult CalculateScore(WinResult win, int dealerSeatIndex, bool isFullFlush)
        => CalculateScore(win, dealerSeatIndex, isFullFlush, bigWinPatternCount: 1);

    public ScoreResult CalculateScore(WinResult win, int dealerSeatIndex, bool isFullFlush, int bigWinPatternCount)
        => CalculateScore(win, dealerSeatIndex, isFullFlush, bigWinPatternCount, baseUnit: 1);

    public ScoreResult CalculateScore(WinResult win, int dealerSeatIndex, bool isFullFlush, int bigWinPatternCount, int baseUnit)
    {
        ChangshaBaseUnit.Validate(baseUnit);
        var payments = new List<PaymentEntry>();
        var category = ClassifyWin(win);

        // Stacking multiplier (Phase H Wave 2): clamp the raw pattern count to [1, 3] for
        // Big Wins; Small Wins never stack (no Big Win flag can fire), so force ×1. The
        // multiplier is applied to per-payment Big Win amounts; Small Win amounts are
        // unchanged for backward compatibility.
        var multiplier = category == ScoreCategory.BigWin
            ? Math.Clamp(bigWinPatternCount, 1, 3)
            : 1;

        if (win.Method == WinMethod.SelfDraw)
        {
            CalculateSelfDrawPayments(win, dealerSeatIndex, category, multiplier, payments);
        }
        else
        {
            CalculateDiscardPayments(win, dealerSeatIndex, category, multiplier, payments);
        }

        payments = ScalePayments(payments, baseUnit);
        var basePoints = payments.Sum(p => p.Amount);

        return new ScoreResult
        {
            Category = category,
            BasePoints = basePoints,
            Payments = payments
        };
    }

    public FalseHuPenalty CalculateFalseHuPenalty(int offendingSeatIndex)
        => CalculateFalseHuPenalty(offendingSeatIndex, baseUnit: 1);

    public FalseHuPenalty CalculateFalseHuPenalty(int offendingSeatIndex, int baseUnit)
    {
        if (offendingSeatIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(offendingSeatIndex));
        ChangshaBaseUnit.Validate(baseUnit);
        var perOpponent = checked(FalseHuPenaltyPerOpponent * baseUnit);
        var payments = new List<PaymentEntry>();
        for (var seat = 0; seat < 4; seat++)
        {
            if (seat == offendingSeatIndex) continue;
            payments.Add(new PaymentEntry
            {
                FromSeatIndex = offendingSeatIndex,
                ToSeatIndex = seat,
                Amount = perOpponent,
                Reason = "falseHu-penalty"
            });
        }
        return new FalseHuPenalty
        {
            OffendingSeatIndex = offendingSeatIndex,
            PenaltyPerOpponent = perOpponent,
            Payments = payments
        };
    }

    internal static List<PaymentEntry> ScalePayments(IReadOnlyList<PaymentEntry> payments, int baseUnit)
    {
        ChangshaBaseUnit.Validate(baseUnit);
        return payments.Select(payment => new PaymentEntry
        {
            FromSeatIndex = payment.FromSeatIndex,
            ToSeatIndex = payment.ToSeatIndex,
            Amount = checked(payment.Amount * baseUnit),
            Reason = payment.Reason
        }).ToList();
    }

    internal static Dictionary<int, int> ApplyPayments(
        IReadOnlyDictionary<int, int> scores,
        IReadOnlyList<PaymentEntry> payments)
    {
        if (scores.Count != 4 || Enumerable.Range(0, 4).Any(seat => !scores.ContainsKey(seat)))
            throw new InvalidOperationException("Cumulative scores must contain exactly the four seats.");

        // Accumulate in a wider temporary vector; publish nothing until every final
        // seat total fits. A late overflow must not leave a partially settled hand.
        var totals = Enumerable.Range(0, 4).Select(seat => (long)scores[seat]).ToArray();
        foreach (var payment in payments)
        {
            if (payment.FromSeatIndex is < 0 or > 3
                || payment.ToSeatIndex is < 0 or > 3
                || payment.FromSeatIndex == payment.ToSeatIndex
                || payment.Amount <= 0)
            {
                throw new InvalidOperationException("A payment must transfer a positive amount between distinct seats.");
            }
            totals[payment.FromSeatIndex] = checked(totals[payment.FromSeatIndex] - payment.Amount);
            totals[payment.ToSeatIndex] = checked(totals[payment.ToSeatIndex] + payment.Amount);
        }
        return Enumerable.Range(0, 4).ToDictionary(seat => seat, seat => checked((int)totals[seat]));
    }

    private static ScoreCategory ClassifyWin(WinResult win)
    {
        // Issue #157 — classification is centralised in ChangshaWinCategory so that the
        // contextual Big Wins (天和 / 地和 / 海底捞月 / 河底捞鱼 / 杠上开花) and 抢杠胡
        // (WinResult.IsRobbedKong) are scored as Big Wins even on a Standard shape, and
        // the Big-Win pattern set cannot drift between the detector and this service.
        return ChangshaWinCategory.Classify(win);
    }

    private static void CalculateSelfDrawPayments(
        WinResult win,
        int dealerSeatIndex,
        ScoreCategory category,
        int multiplier,
        List<PaymentEntry> payments)
    {
        for (var seat = 0; seat < 4; seat++)
        {
            if (seat == win.WinningSeatIndex)
                continue;

            var dealerInvolved = seat == dealerSeatIndex || win.WinningSeatIndex == dealerSeatIndex;
            int amount;

            if (category == ScoreCategory.BigWin)
            {
                amount = (dealerInvolved ? BigWinSelfDrawDealer : BigWinSelfDrawBase) * multiplier;
            }
            else
            {
                amount = dealerInvolved ? SmallWinSelfDrawDealer : SmallWinSelfDrawBase;
            }

            payments.Add(new PaymentEntry
            {
                FromSeatIndex = seat,
                ToSeatIndex = win.WinningSeatIndex,
                Amount = amount,
                Reason = $"{category}-selfDraw{(dealerInvolved ? "-dealer" : "")}{(multiplier > 1 ? $"-x{multiplier}" : "")}"
            });
        }
    }

    private static void CalculateDiscardPayments(
        WinResult win,
        int dealerSeatIndex,
        ScoreCategory category,
        int multiplier,
        List<PaymentEntry> payments)
    {
        var dealerInvolved = win.SourceSeatIndex == dealerSeatIndex
                          || win.WinningSeatIndex == dealerSeatIndex;
        int amount;

        if (category == ScoreCategory.BigWin)
        {
            amount = (dealerInvolved ? BigWinDiscardDealer : BigWinDiscardBase) * multiplier;
        }
        else
        {
            amount = dealerInvolved ? SmallWinDiscardDealer : SmallWinDiscardBase;
        }

        payments.Add(new PaymentEntry
        {
            FromSeatIndex = win.SourceSeatIndex,
            ToSeatIndex = win.WinningSeatIndex,
            Amount = amount,
            Reason = $"{category}-discard{(dealerInvolved ? "-dealer" : "")}{(multiplier > 1 ? $"-x{multiplier}" : "")}"
        });
    }
}
