using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Resolves claim priority for a discarded tile per spec §3.3.
///
/// Priority tiers (highest → lowest):
///   Hu &gt; {Kong, Pung} &gt; Chow &gt; Pass
///
/// Kong and Pung are the SAME tier (v1.2 lock 2026-05-13). Within a tier, ties
/// are broken by counter-clockwise distance from the discarder — closest seat wins.
///
/// Chow is restricted to the immediate next seat (CCW) from the discarder.
/// </summary>
public interface IClaimAdjudicator
{
    ChangshaClaimOpportunity? Adjudicate(
        int discardSeatIndex,
        int discardTileId,
        IReadOnlyList<ChangshaHandState> hands,
        IReadOnlyList<Meld> existingMelds);

    List<ChangshaClaimOpportunity> GetOpportunities(
        int discardSeatIndex,
        int discardTileId,
        IReadOnlyList<ChangshaHandState> hands);

    /// <summary>
    /// Phase H Wave 2 — Hu-only opportunity scan used by the robbing-the-added-kong
    /// (抢杠胡) graft on <see cref="ChangshaGameStateMachine.DeclareAddedKong"/>.
    /// Unlike <see cref="GetOpportunities"/>, this overload:
    ///   1. Excludes the seat declaring the added-kong (<paramref name="kongDeclarerSeatIndex"/>),
    ///      whose pung is being upgraded.
    ///   2. Surfaces ONLY <see cref="Tables.TableClaimType.Hu"/> opportunities — Pung/Kong/Chow
    ///      on an added-kong tile are illegal because the tile is mid-meld, not a discard.
    ///   3. Returns an empty list (cheap exit) when no seat can Hu on the candidate tile,
    ///      letting the caller skip opening a claim window with zero latency cost.
    /// Per spec §3.4.3, concealed kongs are NOT robbable — callers must not invoke this
    /// helper for ConcealedKong declarations.
    /// </summary>
    List<ChangshaClaimOpportunity> GetHuOnlyOpportunitiesForKong(
        int kongDeclarerSeatIndex,
        int kongTileId,
        IReadOnlyList<ChangshaHandState> hands);
}

public sealed class ClaimAdjudicator : IClaimAdjudicator
{
    private const int SeatCount = ChangshaClaimPriority.SeatCount;

    public ChangshaClaimOpportunity? Adjudicate(
        int discardSeatIndex,
        int discardTileId,
        IReadOnlyList<ChangshaHandState> hands,
        IReadOnlyList<Meld> existingMelds)
    {
        var opportunities = GetOpportunities(discardSeatIndex, discardTileId, hands);
        return SelectWinner(opportunities, discardSeatIndex);
    }

    public List<ChangshaClaimOpportunity> GetOpportunities(
        int discardSeatIndex,
        int discardTileId,
        IReadOnlyList<ChangshaHandState> hands)
    {
        var opportunities = new List<ChangshaClaimOpportunity>();
        var discardLogical = ChangshaDeckBuilder.GetLogicalTile(discardTileId);

        foreach (var hand in hands)
        {
            if (hand.SeatIndex == discardSeatIndex)
                continue;

            var logicalTiles = hand.ConcealedTiles.Select(ChangshaDeckBuilder.GetLogicalTile).ToList();
            var matchingCount = logicalTiles.Count(t => t == discardLogical);

            // Check Hu
            if (ChangshaWinDetector.IsWinningWith(hand, discardTileId))
            {
                opportunities.Add(new ChangshaClaimOpportunity
                {
                    SeatIndex = hand.SeatIndex,
                    ClaimType = TableClaimType.Hu,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Hu)
                });
            }

            // Check Kong (3 matching in hand) — same tier as Pung
            if (matchingCount >= 3)
            {
                opportunities.Add(new ChangshaClaimOpportunity
                {
                    SeatIndex = hand.SeatIndex,
                    ClaimType = TableClaimType.Kong,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Kong)
                });
            }
            // Check Pung (2 matching in hand) — same tier as Kong
            if (matchingCount >= 2)
            {
                opportunities.Add(new ChangshaClaimOpportunity
                {
                    SeatIndex = hand.SeatIndex,
                    ClaimType = TableClaimType.Pung,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Pung)
                });
            }

            // Check Chow (next seat only)
            if (IsNextSeat(discardSeatIndex, hand.SeatIndex)
                && IsChowCandidate(logicalTiles, discardLogical))
            {
                opportunities.Add(new ChangshaClaimOpportunity
                {
                    SeatIndex = hand.SeatIndex,
                    ClaimType = TableClaimType.Chow,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Chow)
                });
            }
        }

        return opportunities;
    }

    public List<ChangshaClaimOpportunity> GetHuOnlyOpportunitiesForKong(
        int kongDeclarerSeatIndex,
        int kongTileId,
        IReadOnlyList<ChangshaHandState> hands)
    {
        var opportunities = new List<ChangshaClaimOpportunity>();

        foreach (var hand in hands)
        {
            if (hand.SeatIndex == kongDeclarerSeatIndex)
                continue;

            if (ChangshaWinDetector.IsWinningWith(hand, kongTileId))
            {
                opportunities.Add(new ChangshaClaimOpportunity
                {
                    SeatIndex = hand.SeatIndex,
                    ClaimType = TableClaimType.Hu,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Hu)
                });
            }
        }

        return opportunities;
    }

    private static ChangshaClaimOpportunity? SelectWinner(
        List<ChangshaClaimOpportunity> opportunities,
        int discardSeatIndex)
    {
        if (opportunities.Count == 0)
            return null;

        return opportunities
            .OrderByDescending(o => o.Priority)
            .ThenBy(o => ChangshaClaimPriority.CounterClockwiseDistance(discardSeatIndex, o.SeatIndex))
            .ThenBy(o => o.SeatIndex)
            .First();
    }

    private static bool IsNextSeat(int discardSeatIndex, int seatIndex) =>
        (discardSeatIndex + 1) % SeatCount == seatIndex;

    private static bool IsChowCandidate(IReadOnlyCollection<int> logicalTiles, int discardLogical)
    {
        if (discardLogical is < 0 or >= 27)
            return false;

        var suitOffset = discardLogical / 9;
        var rank = discardLogical % 9;
        var suitedRanks = logicalTiles
            .Where(t => t / 9 == suitOffset)
            .Select(t => t % 9)
            .ToHashSet();

        return (rank >= 2 && suitedRanks.Contains(rank - 2) && suitedRanks.Contains(rank - 1))
            || (rank >= 1 && rank <= 7 && suitedRanks.Contains(rank - 1) && suitedRanks.Contains(rank + 1))
            || (rank <= 6 && suitedRanks.Contains(rank + 1) && suitedRanks.Contains(rank + 2));
    }
}
