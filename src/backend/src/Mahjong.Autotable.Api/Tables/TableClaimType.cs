namespace Mahjong.Autotable.Api.Tables;

// Retained after the Phase A legacy `Tables/*` purge because the surviving
// Changsha rules engine (state machine, claim adjudicator, bot policy) and
// the autotable transport still reference this enum. Originally defined in
// the deleted `TableGameState.cs`.
public enum TableClaimType
{
    Hu,
    Kong,
    Pung,
    Chow
}
