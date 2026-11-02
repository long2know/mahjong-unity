namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Implements the Changsha batch-of-4 deal procedure per Vasquez §2:
///   1. Shuffle 108 tiles, compute break point, build draw order.
///   2. Deal in batches of 4 tiles (2 stacks) starting with dealer, then counter-clockwise.
///      3 rounds of 4 = 12 tiles each.
///   3. Final round: 1 tile to each player.
///   4. Dealer gets 1 extra tile → dealer has 14, others have 13.
///   5. 55 tiles remain in the wall.
/// </summary>
public interface IDealService
{
    DealResult Deal(List<int> wall, int dealerSeatIndex);
}

public sealed class DealResult
{
    public required List<List<int>> Hands { get; init; } // index by seat index
    public required List<int> RemainingWall { get; init; }
}

public sealed class DealService : IDealService
{
    public const int SeatCount = 4;
    public const int BatchSize = 4;
    public const int BatchRounds = 3;
    public const int DealerTiles = 14;
    public const int NonDealerTiles = 13;
    public const int ExpectedRemainingWall = 55;

    public DealResult Deal(List<int> wall, int dealerSeatIndex)
    {
        if (wall.Count != ChangshaDeckBuilder.TotalTiles)
            throw new ArgumentException($"Wall must contain {ChangshaDeckBuilder.TotalTiles} tiles.", nameof(wall));

        // Build a draw queue from the wall (front = first to draw)
        var drawQueue = new Queue<int>(wall);
        var hands = new List<List<int>>(SeatCount);
        for (var i = 0; i < SeatCount; i++)
            hands.Add(new List<int>());

        // Deal order: starting from dealer, counterclockwise
        var dealOrder = new int[SeatCount];
        for (var i = 0; i < SeatCount; i++)
            dealOrder[i] = (dealerSeatIndex + i) % SeatCount;

        // 3 rounds of batch-4
        for (var round = 0; round < BatchRounds; round++)
        {
            for (var i = 0; i < SeatCount; i++)
            {
                var seatIndex = dealOrder[i];
                for (var t = 0; t < BatchSize; t++)
                    hands[seatIndex].Add(drawQueue.Dequeue());
            }
        }

        // Final round: 1 tile each
        for (var i = 0; i < SeatCount; i++)
        {
            var seatIndex = dealOrder[i];
            hands[seatIndex].Add(drawQueue.Dequeue());
        }

        // Dealer gets 1 extra tile (14th)
        hands[dealerSeatIndex].Add(drawQueue.Dequeue());

        // Remaining wall
        var remainingWall = drawQueue.ToList();

        return new DealResult
        {
            Hands = hands,
            RemainingWall = remainingWall
        };
    }
}
