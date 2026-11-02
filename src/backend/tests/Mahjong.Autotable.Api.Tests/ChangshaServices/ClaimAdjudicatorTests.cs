using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

public class ClaimAdjudicatorTests
{
    private readonly ClaimAdjudicator _svc = new();

    [Fact]
    public void Hu_TakesPriorityOverKong()
    {
        var hands = new List<ChangshaHandState>
        {
            new() { SeatIndex = 0, ConcealedTiles = [] }, // discarder
            new() { SeatIndex = 1, ConcealedTiles = CreateHuHand(0) }, // has Hu
            new() { SeatIndex = 2, ConcealedTiles = [0, 1, 2] }, // has Kong (3 matching)
            new() { SeatIndex = 3, ConcealedTiles = [] }
        };

        // Discard tile 3 (logical 0 = Wan 1)
        var winner = _svc.Adjudicate(0, 3, hands, []);
        Assert.NotNull(winner);
        Assert.Equal(TableClaimType.Hu, winner.ClaimType);
    }

    [Fact]
    public void KongAndPung_SameTier_CCWClosestSeatWins_KongCloserCCW()
    {
        // v1.2: Kong and Pung are the SAME tier; CCW seat-proximity tiebreak decides.
        // Discarder seat 0; seat 1 has Kong, seat 2 has Pung.
        // Seat 1 is closer CCW (distance 1) than seat 2 (distance 2), so seat 1 wins.
        // (Outcome happens to be Kong — but the rationale is seat proximity, NOT Kong > Pung.)
        var hands = new List<ChangshaHandState>
        {
            new() { SeatIndex = 0, ConcealedTiles = [] },
            new() { SeatIndex = 1, ConcealedTiles = [0, 1, 2] }, // Kong
            new() { SeatIndex = 2, ConcealedTiles = [0, 1] }, // Pung
            new() { SeatIndex = 3, ConcealedTiles = [] }
        };

        var winner = _svc.Adjudicate(0, 3, hands, []);
        Assert.NotNull(winner);
        Assert.Equal(1, winner.SeatIndex); // closer CCW
        Assert.Equal(TableClaimType.Kong, winner.ClaimType);
    }

    [Fact]
    public void KongAndPung_SameTier_PungCloserCCW_BeatsKong()
    {
        // Counterexample proving Kong is NOT strictly above Pung.
        // Discarder seat 0; seat 1 has Pung, seat 3 has Kong.
        // Seat 1 is closer CCW (distance 1) than seat 3 (distance 3), so seat 1 (Pung) wins.
        var hands = new List<ChangshaHandState>
        {
            new() { SeatIndex = 0, ConcealedTiles = [] },
            new() { SeatIndex = 1, ConcealedTiles = [0, 1] }, // Pung (2 matching)
            new() { SeatIndex = 2, ConcealedTiles = [] },
            new() { SeatIndex = 3, ConcealedTiles = [0, 1, 2] } // Kong (3 matching)
        };

        var winner = _svc.Adjudicate(0, 3, hands, []);
        Assert.NotNull(winner);
        Assert.Equal(1, winner.SeatIndex);
        Assert.Equal(TableClaimType.Pung, winner.ClaimType); // Pung beats Kong by CCW proximity
    }

    [Theory]
    [InlineData(0, 1, 3)] // discarder 0: seat 1 (Pung) beats seat 3 (Kong)
    [InlineData(1, 2, 0)] // discarder 1: seat 2 (Pung) beats seat 0 (Kong)
    [InlineData(2, 3, 1)] // discarder 2: seat 3 (Pung) beats seat 1 (Kong)
    [InlineData(3, 0, 2)] // discarder 3: seat 0 (Pung) beats seat 2 (Kong)
    public void ClaimPriority_PungAndKong_SameTier_CCWProximityTiebreak(int discarderSeat, int pungSeat, int kongSeat)
    {
        var hands = Enumerable.Range(0, 4)
            .Select(i => new ChangshaHandState { SeatIndex = i, ConcealedTiles = new List<int>() })
            .ToList();
        // Seed all of seat 'pungSeat' with 2 matching, 'kongSeat' with 3 matching of the same logical tile.
        // Use logical tile 0 (tile IDs 0..3).
        hands[pungSeat].ConcealedTiles = [0, 1];
        hands[kongSeat].ConcealedTiles = [0, 1, 2];
        // Discard the 4th copy of logical tile 0 (tile ID 3).
        var winner = _svc.Adjudicate(discarderSeat, 3, hands, []);
        Assert.NotNull(winner);
        Assert.Equal(pungSeat, winner.SeatIndex); // CCW proximity wins
        Assert.Equal(TableClaimType.Pung, winner.ClaimType);
    }

    [Fact]
    public void ClaimPriority_PriorityTablesAgree_NoDrift()
    {
        // The adjudicator and the runtime resolver MUST agree on (tier, CCW-distance) ordering
        // for every claim type. Both call sites now go through ChangshaClaimPriority — assert that.
        foreach (var t in new[] { TableClaimType.Hu, TableClaimType.Kong, TableClaimType.Pung, TableClaimType.Chow })
        {
            var tier = ChangshaClaimPriority.TierOf(t);
            Assert.True(tier >= 1 && tier <= 3, $"Tier for {t} out of bounds: {tier}");
        }
        // Hu strictly above Kong/Pung
        Assert.True(ChangshaClaimPriority.TierOf(TableClaimType.Hu)
                  > ChangshaClaimPriority.TierOf(TableClaimType.Kong));
        Assert.True(ChangshaClaimPriority.TierOf(TableClaimType.Hu)
                  > ChangshaClaimPriority.TierOf(TableClaimType.Pung));
        // Kong and Pung SAME tier (v1.2 lock)
        Assert.Equal(
            ChangshaClaimPriority.TierOf(TableClaimType.Kong),
            ChangshaClaimPriority.TierOf(TableClaimType.Pung));
        // Chow strictly below Kong/Pung
        Assert.True(ChangshaClaimPriority.TierOf(TableClaimType.Pung)
                  > ChangshaClaimPriority.TierOf(TableClaimType.Chow));
        // CCW distance contract: 0 (same seat), 1 (next CCW), 2, 3
        for (var d = 0; d < 4; d++)
        for (var c = 0; c < 4; c++)
        {
            var dist = ChangshaClaimPriority.CounterClockwiseDistance(d, c);
            Assert.Equal((c - d + 4) % 4, dist);
        }

        // The Adjudicator surfaces the same Priority value that the helper computes.
        var hands = new List<ChangshaHandState>
        {
            new() { SeatIndex = 0, ConcealedTiles = [] },
            new() { SeatIndex = 1, ConcealedTiles = [0, 1, 2] }, // Kong
            new() { SeatIndex = 2, ConcealedTiles = [0, 1] },    // Pung
            new() { SeatIndex = 3, ConcealedTiles = [] }
        };
        var opps = _svc.GetOpportunities(0, 3, hands);
        foreach (var opp in opps)
        {
            Assert.Equal(ChangshaClaimPriority.TierOf(opp.ClaimType), opp.Priority);
        }
    }

    [Fact]
    public void Pung_TakesPriorityOverChow()
    {
        var hands = new List<ChangshaHandState>
        {
            new() { SeatIndex = 0, ConcealedTiles = [] },
            new() { SeatIndex = 1, ConcealedTiles = [4, 8] }, // Chow (next seat): Wan2, Wan3 for Wan1 discard
            new() { SeatIndex = 2, ConcealedTiles = [0, 1] }, // Pung
            new() { SeatIndex = 3, ConcealedTiles = [] }
        };

        // Discard tile 3 (logical 0 = Wan 1)
        var winner = _svc.Adjudicate(0, 3, hands, []);
        Assert.NotNull(winner);
        Assert.Equal(TableClaimType.Pung, winner.ClaimType);
        Assert.Equal(2, winner.SeatIndex);
    }

    [Fact]
    public void Chow_OnlyFromNextSeat()
    {
        var hands = new List<ChangshaHandState>
        {
            new() { SeatIndex = 0, ConcealedTiles = [] },
            new() { SeatIndex = 1, ConcealedTiles = [4, 8] }, // Next seat — can chow
            new() { SeatIndex = 2, ConcealedTiles = [4, 8] }, // Not next seat — cannot chow
            new() { SeatIndex = 3, ConcealedTiles = [4, 8] }  // Not next seat — cannot chow
        };

        var opportunities = _svc.GetOpportunities(0, 3, hands);
        var chows = opportunities.Where(o => o.ClaimType == TableClaimType.Chow).ToList();

        Assert.Single(chows);
        Assert.Equal(1, chows[0].SeatIndex);
    }

    [Fact]
    public void NoOpportunities_ReturnsNull()
    {
        var hands = new List<ChangshaHandState>
        {
            new() { SeatIndex = 0, ConcealedTiles = [] },
            new() { SeatIndex = 1, ConcealedTiles = [40, 44] }, // unrelated tiles
            new() { SeatIndex = 2, ConcealedTiles = [48, 52] },
            new() { SeatIndex = 3, ConcealedTiles = [56, 60] }
        };

        var winner = _svc.Adjudicate(0, 3, hands, []);
        Assert.Null(winner);
    }

    // Helper: create a 13-tile hand that wins with tile logical=0 (Wan 1)
    // Standard win: Wan 2-3-4, Wan 5-6-7, Wan 8-8 pair, Tong 1-2-3 = needs Wan 1 to complete Wan 1-2-3
    // Actually, let's make it simpler: hand has tiles that win when Wan1 (tile 3) is added
    private static List<int> CreateHuHand(int discardLogical)
    {
        // Build a hand that's 1 tile away from winning
        // 3 melds + pair + need one more tile for the 4th meld
        // Wan 2-3 (need Wan 1 to complete), Wan 4-5-6, Wan 7-8-9, Tong 5-5 pair
        return [
            4, 8,      // Wan 2 (copy 0), Wan 3 (copy 0) — need Wan 1
            12, 16, 20, // Wan 4, 5, 6
            24, 28, 32, // Wan 7, 8, 9
            52, 53,     // Tong 5 (copy 0, 1) — pair (rank 5 = 258)
            36, 40, 44  // Tong 1, 2, 3
        ];
    }
}
