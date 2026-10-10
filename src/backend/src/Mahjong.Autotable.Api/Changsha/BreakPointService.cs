namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Given dice sum + roller (dealer) seat, computes which wall to break and where.
/// Per Vasquez §2:
///   1. Dice sum selects wall counting counterclockwise from dealer: (sum-1) % 4 maps to
///      dealer(0)/right(1)/opposite(2)/left(3).
///   2. From the RIGHT end of that wall, count stacks equal to dice sum.
///      Break occurs AFTER that stack. Drawing proceeds counterclockwise from the break.
///
/// Wall layout: 54 stacks total. Two walls have 14 stacks, two have 13 stacks.
///
/// <para><b>Seat-absolute wall-size frame (F1 — single source of truth):</b> which walls
/// are 14 vs 13 stacks is fixed by <b>seat index</b>, matching
/// <see cref="Mahjong.Autotable.Api.Autotable.AutotableSlotMap.WallStackCount"/>: seats
/// 0 and 1 own the 14-stack walls, seats 2 and 3 own the 13-stack walls. The wall to break
/// is the physical wall belonging to the selected <b>absolute</b> seat, so its size and the
/// tiles preceding it are looked up by absolute seat — never by a dealer-relative offset.
/// This is what lets the renderer anchor the break as
/// <c>WallDealerOriginOrdinal(dealer) + TileIndex</c> and land on the physically correct
/// stack for every dealer. (Previously this service used a dealer-relative
/// [14,13,14,13] frame — dealer/opposite always 14 — which disagreed with the fixed
/// per-seat render frame and drifted the anchor a stack for many dealer×dice combinations.)</para>
///
/// The wall is treated as a flat ordered list of 108 tiles; <c>TileIndex</c> is the
/// absolute flat index of the break tile counted counterclockwise from the dealer's wall.
/// </summary>
public interface IBreakPointService
{
    BreakPointResult ComputeBreakPoint(int diceSum, int dealerSeatIndex);
}

public sealed class BreakPointService : IBreakPointService
{
    // Seat-absolute wall stacks (F1): indexed by ABSOLUTE seat, NOT by dealer-relative
    // offset. Seats 0,1 own the 14-stack walls; seats 2,3 own the 13-stack walls. This
    // matches AutotableSlotMap.WallStackCount so the render ring, dealer origin, and this
    // break anchor share one frame.
    private static readonly int[] StacksPerSeat = [14, 14, 13, 13];

    public BreakPointResult ComputeBreakPoint(int diceSum, int dealerSeatIndex)
    {
        if (diceSum < 2 || diceSum > 12)
            throw new ArgumentOutOfRangeException(nameof(diceSum), "Dice sum must be 2–12.");

        // Step 1: Determine which wall to break (counterclockwise from dealer)
        // Count: 1=dealer, 2=right, 3=opposite, 4=left, 5=dealer, ...
        var wallOffset = (diceSum - 1) % 4;
        var wallIndex = (dealerSeatIndex + wallOffset) % 4;

        // Step 2: Count stacks from the RIGHT end of that wall, using the wall's
        // seat-absolute size (the physical wall belongs to seat `wallIndex`).
        var wallStacks = StacksPerSeat[wallIndex];
        var stacksFromRight = diceSum;

        // Dice sum max is 12 and the smallest wall is 13 stacks, so stackIndex stays in
        // [1, 12] ⊂ [0, wallStacks): no wrap and never out of range.
        var stackIndex = wallStacks - stacksFromRight;

        // Step 3: Compute absolute tile index
        // Tiles are ordered from the dealer's wall counterclockwise; each stack = 2 tiles.
        var tilesBeforeWall = GetTilesBeforeWall(dealerSeatIndex, wallIndex);
        var tileIndex = tilesBeforeWall + stackIndex * 2;

        return new BreakPointResult(wallIndex, stackIndex, tileIndex);
    }

    private static int GetTilesBeforeWall(int dealerSeatIndex, int wallIndex)
    {
        var tiles = 0;
        for (var i = 0; i < 4; i++)
        {
            var currentWall = (dealerSeatIndex + i) % 4;
            if (currentWall == wallIndex)
                break;
            // Seat-absolute size of each wall we pass on the way from the dealer's wall.
            tiles += StacksPerSeat[currentWall] * 2;
        }
        return tiles;
    }
}
