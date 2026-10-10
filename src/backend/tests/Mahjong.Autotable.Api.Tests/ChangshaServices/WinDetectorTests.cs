using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class WinDetectorTests
{
    private readonly ChangshaWinDetector _detector = new();

    // Helper: create tile IDs from logical tile indices
    // logical = suit*9 + (rank-1), tileId = logical*4 + copy
    private static List<int> MakeTiles(params (int suit, int rank, int copy)[] tiles)
        => tiles.Select(t => (t.suit * 9 + (t.rank - 1)) * 4 + t.copy).ToList();

    [Fact]
    public void StandardWin_With258Pair_IsSmallWin()
    {
        // 4 melds + pair of rank 2 (Wan 2)
        // Melds: Wan 1-2-3, Wan 4-5-6, Wan 7-8-9, Tong 1-1-1; Pair: Wan 2
        // Wait, let's build a valid 14-tile hand:
        // 3× Wan1 (pung) + 3× Wan4 (pung) + 3× Wan7 (pung) + 3× Tong3 (pung) + 2× Tong2 (pair, rank 2)
        // But that's all pungs... let me build a standard with chows.

        // Chow: Wan 1-2-3, Wan 4-5-6, Wan 7-8-9, Tong 1-2-3, Pair: Tong 5 (rank 5)
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 2, 0), (0, 3, 0),  // Wan 1-2-3
                (0, 4, 0), (0, 5, 0), (0, 6, 0),  // Wan 4-5-6
                (0, 7, 0), (0, 8, 0), (0, 9, 0),  // Wan 7-8-9
                (1, 1, 0), (1, 2, 0), (1, 3, 0),  // Tong 1-2-3
                (1, 5, 0), (1, 5, 1)               // Tong 5 pair
            )
        };

        var result = _detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.Equal(WinPattern.Standard, result.Pattern);
        Assert.Equal(ScoreCategory.SmallWin, result.Category);
    }

    [Fact]
    public void StandardWin_WithNon258Pair_IsNotWin()
    {
        // Same hand but pair rank 1 (not 2/5/8)
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 2, 0), (0, 3, 0),
                (0, 4, 0), (0, 5, 0), (0, 6, 0),
                (0, 7, 0), (0, 8, 0), (0, 9, 0),
                (1, 1, 0), (1, 2, 0), (1, 3, 0),
                (1, 1, 1), (1, 1, 2)  // Tong 1 pair (rank 1 — not 258)
            )
        };

        var result = _detector.Detect(hand);
        // This is a valid hand shape but the pair is rank 1, not 2/5/8
        // So it should NOT be a standard win. Could still be AllPungs or Flush though.
        // Actually this hand has chows, so not AllPungs, and is multi-suit so not Flush.
        Assert.False(result.IsWin);
    }

    [Fact]
    public void SevenPairs_IsDetected()
    {
        // 7 pairs: 2× each of 7 different logical tiles
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 1, 1),  // Wan 1 pair
                (0, 3, 0), (0, 3, 1),  // Wan 3 pair
                (0, 5, 0), (0, 5, 1),  // Wan 5 pair
                (0, 7, 0), (0, 7, 1),  // Wan 7 pair
                (1, 2, 0), (1, 2, 1),  // Tong 2 pair
                (1, 4, 0), (1, 4, 1),  // Tong 4 pair
                (1, 6, 0), (1, 6, 1)   // Tong 6 pair
            )
        };

        var result = _detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.True(result.IsSevenPairs);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact]
    public void AllPungs_IsDetected()
    {
        // 4 pungs + pair (all concealed)
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 1, 1), (0, 1, 2),  // Wan 1 pung
                (0, 4, 0), (0, 4, 1), (0, 4, 2),  // Wan 4 pung
                (0, 7, 0), (0, 7, 1), (0, 7, 2),  // Wan 7 pung
                (1, 3, 0), (1, 3, 1), (1, 3, 2),  // Tong 3 pung
                (1, 9, 0), (1, 9, 1)               // Tong 9 pair
            )
        };

        var result = _detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.True(result.IsAllPungs);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact]
    public void FullFlush_IsDetected()
    {
        // All tiles same suit, valid winning shape
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 2, 0), (0, 3, 0),  // Wan 1-2-3
                (0, 4, 0), (0, 5, 0), (0, 6, 0),  // Wan 4-5-6
                (0, 7, 0), (0, 8, 0), (0, 9, 0),  // Wan 7-8-9
                (0, 1, 1), (0, 2, 1), (0, 3, 1),  // Wan 1-2-3
                (0, 5, 1), (0, 5, 2)               // Wan 5 pair
            )
        };

        var result = _detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.True(result.IsFullFlush);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
    }

    [Fact]
    public void NoWin_IncompleteHand()
    {
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 3, 0), (0, 5, 0),
                (0, 7, 0), (0, 9, 0), (1, 1, 0),
                (1, 3, 0), (1, 5, 0), (1, 7, 0),
                (1, 9, 0), (2, 1, 0), (2, 3, 0),
                (2, 5, 0), (2, 7, 0)
            )
        };

        var result = _detector.Detect(hand);
        Assert.False(result.IsWin);
    }

    [Fact]
    public void IsWinningWith_ReturnsTrueForWinningTile()
    {
        // Hand needs one more tile to win: Wan 1-2-3, 4-5-6, 7-8-9, Tong 1-2-3 + Tong 5
        // Adding another Tong 5 completes the 258 pair
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 2, 0), (0, 3, 0),
                (0, 4, 0), (0, 5, 0), (0, 6, 0),
                (0, 7, 0), (0, 8, 0), (0, 9, 0),
                (1, 1, 0), (1, 2, 0), (1, 3, 0),
                (1, 5, 0)
            )
        };
        var winTileId = (1 * 9 + 4) * 4 + 1; // Tong 5, copy 1
        Assert.True(ChangshaWinDetector.IsWinningWith(hand, winTileId));
    }

    [Fact]
    public void AllPungs_WithExposedPungMeld_IsDetected()
    {
        // 3 concealed pungs + pair + 1 exposed pung
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 1, 1), (0, 1, 2),  // Wan 1 pung
                (0, 4, 0), (0, 4, 1), (0, 4, 2),  // Wan 4 pung
                (0, 7, 0), (0, 7, 1), (0, 7, 2),  // Wan 7 pung
                (1, 9, 0), (1, 9, 1)               // Tong 9 pair
            ),
            Melds =
            [
                new Meld
                {
                    Kind = MeldKind.Pung,
                    TileIds = MakeTiles((1, 3, 0), (1, 3, 1), (1, 3, 2)),
                    ClaimedFromSeatIndex = 1
                }
            ]
        };

        var result = _detector.Detect(hand);
        Assert.True(result.IsWin);
        Assert.True(result.IsAllPungs);
    }

    [Fact]
    public void AllPungs_WithChowMeld_IsNotAllPungs()
    {
        // Has a chow meld — disqualifies from AllPungs
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = MakeTiles(
                (0, 1, 0), (0, 1, 1), (0, 1, 2),  // Wan 1 pung
                (0, 4, 0), (0, 4, 1), (0, 4, 2),  // Wan 4 pung
                (0, 7, 0), (0, 7, 1), (0, 7, 2),  // Wan 7 pung
                (1, 8, 0), (1, 8, 1)               // Tong 8 pair (rank 8)
            ),
            Melds =
            [
                new Meld
                {
                    Kind = MeldKind.Chow,
                    TileIds = MakeTiles((1, 1, 0), (1, 2, 0), (1, 3, 0)),
                    ClaimedFromSeatIndex = 1
                }
            ]
        };

        var result = _detector.Detect(hand);
        Assert.False(result.IsAllPungs);
        // But it could still be a standard win (has a 258 pair: rank 8)
        Assert.True(result.IsWin);
        Assert.Equal(WinPattern.Standard, result.Pattern);
    }
}
