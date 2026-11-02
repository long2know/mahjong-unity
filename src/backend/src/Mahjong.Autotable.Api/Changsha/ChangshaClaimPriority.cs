using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Single source of truth for Changsha v1 claim priority tiers (spec §3.3).
///
///   Tier 3: Hu  (highest — wins outright)
///   Tier 2: Kong, Pung  (same tier — CCW seat-proximity tiebreak)
///   Tier 1: Chow
///   Tier 0: Pass (no claim)
///
/// Within a tier, ties are broken by counter-clockwise distance from the discarder:
/// the seat closest CCW to the discarder wins. Example: discarder = seat 0;
/// seat 1's Pung beats seat 3's Kong because seat 1 is closer CCW.
///
/// Both <see cref="ClaimAdjudicator"/> and the runtime resolver MUST use this helper —
/// having two priority tables risks drift (audited 2026-05-13, fixed Phase 3 stream B).
/// </summary>
public static class ChangshaClaimPriority
{
    public const int SeatCount = 4;

    /// <summary>Tier number for a claim type. Higher = stronger.</summary>
    public static int TierOf(TableClaimType claimType) => claimType switch
    {
        TableClaimType.Hu => 3,
        TableClaimType.Kong => 2,
        TableClaimType.Pung => 2,
        TableClaimType.Chow => 1,
        _ => 0
    };

    /// <summary>
    /// Counter-clockwise distance from <paramref name="discardSeat"/> to <paramref name="claimSeat"/>.
    /// Returns 1, 2, or 3 (0 only if same seat, which is invalid for claims).
    /// Lower distance = closer CCW = stronger tiebreak position.
    /// </summary>
    public static int CounterClockwiseDistance(int discardSeat, int claimSeat) =>
        (claimSeat - discardSeat + SeatCount) % SeatCount;
}
