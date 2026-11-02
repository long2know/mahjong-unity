using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

internal static class ChangshaTestHelpers
{
    public static int Tid(Suit suit, int rank, int copy = 0)
        => ((int)suit * 9 + (rank - 1)) * 4 + copy;

    public static int Logical(Suit suit, int rank) => (int)suit * 9 + (rank - 1);

    public static List<int> Tiles(params (Suit suit, int rank)[] tiles)
    {
        var copies = new Dictionary<int, int>();
        var result = new List<int>(tiles.Length);
        foreach (var (s, r) in tiles)
        {
            var logical = Logical(s, r);
            copies.TryGetValue(logical, out var copy);
            result.Add(Tid(s, r, copy));
            copies[logical] = copy + 1;
        }
        return result;
    }

    public static ChangshaHandState HandOf(int seatIndex, params (Suit suit, int rank)[] tiles)
        => new()
        {
            SeatIndex = seatIndex,
            ConcealedTiles = Tiles(tiles),
            Melds = new List<Meld>()
        };

    public static ChangshaHandState HandOf(int seatIndex, IEnumerable<int> tileIds, IEnumerable<Meld>? melds = null)
        => new()
        {
            SeatIndex = seatIndex,
            ConcealedTiles = tileIds.ToList(),
            Melds = (melds ?? Enumerable.Empty<Meld>()).ToList()
        };

    public static ChangshaGameState NewGameDealtTo(int seed, int[]? botSeats = null)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed, botSeats);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
        ChangshaGameStateMachine.Deal(state);
        return state;
    }
}
