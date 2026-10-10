namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Builds the 108-tile Changsha deck.
/// 3 suits (Wan, Tong, Tiao) × 9 ranks × 4 copies = 108 tiles.
/// Tile IDs 0–107: logicalTile = tileId / 4, suit = logicalTile / 9, rank = logicalTile % 9 + 1.
/// </summary>
public static class ChangshaDeckBuilder
{
    public const int TotalTiles = 108;
    public const int SuitCount = 3;
    public const int RankCount = 9;
    public const int CopiesPerTile = 4;
    public const int LogicalTileCount = SuitCount * RankCount; // 27

    public static List<int> Build()
    {
        return Enumerable.Range(0, TotalTiles).ToList();
    }

    public static Suit GetSuit(int tileId)
    {
        var logicalTile = tileId / CopiesPerTile;
        return (Suit)(logicalTile / RankCount);
    }

    public static int GetRank(int tileId)
    {
        var logicalTile = tileId / CopiesPerTile;
        return logicalTile % RankCount + 1;
    }

    public static int GetLogicalTile(int tileId) => tileId / CopiesPerTile;

    public static Tile ToTile(int tileId) => new(GetSuit(tileId), GetRank(tileId));
}
