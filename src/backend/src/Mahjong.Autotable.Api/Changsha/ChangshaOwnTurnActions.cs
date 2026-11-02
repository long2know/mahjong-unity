namespace Mahjong.Autotable.Api.Changsha;

public sealed class ChangshaOwnTurnActions
{
    public bool Hu { get; init; }
    public List<int[]> ConcealedKongs { get; init; } = [];
    public List<int> AddedKongs { get; init; } = [];

    public static bool IsReady(ChangshaGameState state, int seatIndex)
    {
        if (state.Phase != ChangshaPhase.AwaitingDiscard || state.ActiveSeatIndex != seatIndex
            || state.ClaimWindow is not null || state.IsGameComplete)
            return false;
        var hand = state.Hands.Single(hand => hand.SeatIndex == seatIndex);
        return hand.ConcealedTiles.Count + 3 * hand.Melds.Count == 14;
    }

    public static ChangshaOwnTurnActions? Available(ChangshaGameState state, int seatIndex)
    {
        if (!IsReady(state, seatIndex)) return null;
        var hand = state.Hands.Single(hand => hand.SeatIndex == seatIndex);
        var actions = new ChangshaOwnTurnActions
        {
            Hu = ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, seatIndex),
            ConcealedKongs = ChangshaGameStateMachine.GetConcealedKongCandidates(state, seatIndex)
                .Select(logical => hand.ConcealedTiles.Where(tile => ChangshaDeckBuilder.GetLogicalTile(tile) == logical)
                    .OrderBy(tile => tile).Take(4).ToArray()).ToList(),
            AddedKongs = ChangshaGameStateMachine.GetAddedKongCandidates(state, seatIndex).ToList()
        };
        return actions.Hu || actions.ConcealedKongs.Count > 0 || actions.AddedKongs.Count > 0
            ? actions : null;
    }
}
