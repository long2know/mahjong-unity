namespace Mahjong.Autotable.Api.Changsha.Scoring;

/// <summary>
/// Scoring-policy gate for the live Changsha payment path (spec §5). Introduced by
/// issue #117 to keep the authoritative payments spec-pure by default.
///
/// <para><b>Default (<see cref="SpecPure"/>) — binding spec §5.1:</b> the emitted
/// payments are exactly the §5.1 payment table (1/2 Small Win, 3/4 self-draw &amp;
/// 6/7 discard Big Win, +1 dealer bonus). No fan-catalog bonus is folded into the
/// money and no Big-Win stacking multiplier is applied. The fan catalog
/// (<see cref="FanCalculator"/>) is still evaluated and surfaced on
/// <see cref="ScoreResult.Fans"/> / <see cref="ScoreResult.FanPoints"/> as a
/// read-only breakdown for display/audit — it is <b>query-only</b> with respect to
/// the payment amounts (it never moves chips). This preserves the invariant
/// <c>BasePoints == Σ Payments.Amount</c> at the spec §5.1 magnitude.</para>
///
/// <para><b><see cref="HouseRules"/> — non-spec opt-in:</b> flips both switches on so
/// each detected fan's points are added to every base payment and simultaneously
/// satisfied Big Win patterns stack (×Clamp(count,1,3)). This is the pre-#117
/// "Post-W23" behaviour, retained behind the flag so nothing is lost and a future
/// tournament/house-rule option can enable it without a second rewrite. Spec §5.1
/// has <b>no</b> fan/stacking table, so this mode is intentionally NOT the default.
/// The open product question "should canonical Changsha score a fan catalog +
/// big-win stacking at all?" is surfaced (not decided) by #117.</para>
/// </summary>
public sealed class ChangshaScoringOptions
{
    /// <summary>
    /// When true, each detected fan's <see cref="FanInfo.Points"/> is added to every
    /// base payment (the additive Post-W23 fan layer). Default <c>false</c> keeps the
    /// spec §5.1 payment magnitudes and leaves the fan catalog query-only.
    /// </summary>
    public bool ApplyFanBonuses { get; init; }

    /// <summary>
    /// When true, the Big-Win payment is multiplied by the number of simultaneously
    /// satisfied Big Win patterns (<see cref="WinResult.AllPatterns"/> count, clamped
    /// to [1, 3]). Default <c>false</c> = no stacking; spec §5.1 has no stacking table.
    /// </summary>
    public bool ApplyBigWinStacking { get; init; }

    /// <summary>
    /// Binding default: spec §5.1 payments verbatim; fan catalog surfaced but
    /// query-only (never folded into payments); no Big-Win stacking.
    /// </summary>
    public static ChangshaScoringOptions SpecPure { get; } = new();

    /// <summary>
    /// Non-spec house-rule mode: fan bonuses folded into payments AND Big-Win
    /// stacking applied. Retained behind this flag for a future tournament option and
    /// for characterization tests that pin the pre-#117 magnitudes.
    /// </summary>
    public static ChangshaScoringOptions HouseRules { get; } = new()
    {
        ApplyFanBonuses = true,
        ApplyBigWinStacking = true,
    };
}
