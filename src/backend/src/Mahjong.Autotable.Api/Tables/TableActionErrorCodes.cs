namespace Mahjong.Autotable.Api.Tables;

public static class TableActionErrorCodes
{
    public const string RoundNotActive = "ROUND_NOT_ACTIVE";
    public const string InvalidPhase = "INVALID_PHASE";
    public const string NotActiveSeat = "NOT_ACTIVE_SEAT";
    public const string SeatNotFound = "SEAT_NOT_FOUND";
    public const string InvalidSeat = "INVALID_SEAT";
    public const string TileNotInHand = "TILE_NOT_IN_HAND";
    public const string ClaimWindowNotOpen = "CLAIM_WINDOW_NOT_OPEN";
    public const string InvalidClaimDecision = "INVALID_CLAIM_DECISION";
    public const string ClaimSelectionUnavailable = "CLAIM_SELECTION_UNAVAILABLE";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string StateInvariantBroken = "STATE_INVARIANT_BROKEN";
    public const string ChowTilesInvalid = "CHOW_TILES_INVALID";
}
