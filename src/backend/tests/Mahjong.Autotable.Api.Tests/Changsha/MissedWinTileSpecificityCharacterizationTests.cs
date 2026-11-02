using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CHARACTERIZATION — §3.6 Missed Win (过胡 / 过水) tile-specificity + decay (issue #117).
///
/// <para><b>Surfaced ambiguity (do NOT "fix" without product direction):</b> spec §3.6
/// is internally contradictory. It says a seat that passes a winning discard is
/// "prohibited from winning on <i>a discard</i> until after they draw a tile"
/// (a blanket seat-level lockout), but then "This restriction only applies to the
/// <i>specific tile</i> they missed winning on" (a tile-specific lockout). The current
/// implementation is <b>seat-level</b> (<see cref="ChangshaGameState.MissedWinSeats"/> is a
/// <c>HashSet&lt;int&gt;</c> of seats, not (seat, tile) pairs), so it also blocks a Hu on a
/// <i>different</i> winning tile — the behaviour the tile-specific clause would allow.</para>
///
/// <para>These tests PIN the current seat-level behaviour so a future product decision to
/// switch to tile-specific semantics is a deliberate, test-visible change. They do not
/// assert which reading is "correct" — that is surfaced to Stephen in the #117 PR /
/// decisions memo.</para>
/// </summary>
public class MissedWinTileSpecificityCharacterizationTests
{
    /// <summary>
    /// Seat 1 holds a two-sided wait (Wan-2-3 waits on Wan-1 OR Wan-4, both completing a
    /// valid Standard hand with the Tong-5 258 pair).
    /// </summary>
    private static List<int> TwoSidedWaitOnWan1OrWan4() => new()
    {
        Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),          // completes with Wan-1 or Wan-4
        Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0), Tid(Suit.Wan, 7, 0),
        Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0),
        Tid(Suit.Tiao, 4, 0), Tid(Suit.Tiao, 5, 0), Tid(Suit.Tiao, 6, 0),
        Tid(Suit.Tong, 5, 0), Tid(Suit.Tong, 5, 1),         // 258 pair
    };

    private static ChangshaGameState SetupTwoSidedWait()
    {
        var state = NewGameDealtTo(seed: 23);
        state.DealerSeatIndex = 0;
        state.ActiveSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;

        state.Hands[1].ConcealedTiles = TwoSidedWaitOnWan1OrWan4();
        state.Hands[1].Melds.Clear();

        // Isolate seat 1 as the only claimant: empty seats 2 & 3.
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        // Dealer (seat 0) holds Wan-1 and Wan-4 to discard on demand + benign filler.
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 4, 0),
            Tid(Suit.Tiao, 9, 0), Tid(Suit.Tiao, 9, 1), Tid(Suit.Tiao, 9, 2),
            Tid(Suit.Tiao, 8, 0), Tid(Suit.Tiao, 8, 1), Tid(Suit.Tiao, 8, 2),
            Tid(Suit.Tiao, 7, 0), Tid(Suit.Tiao, 7, 1), Tid(Suit.Tiao, 7, 2),
            Tid(Suit.Tiao, 2, 0), Tid(Suit.Tiao, 2, 1), Tid(Suit.Tiao, 2, 2),
        });
        state.Hands[0].Melds.Clear();
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.MissedWinSeats.Clear();
        return state;
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "117-SpecReconciliation")]
    public void MissedWin_SeatLevelLockout_BlocksHuOnADifferentTile_Characterization()
    {
        var state = SetupTwoSidedWait();
        var wan1 = Tid(Suit.Wan, 1, 0);
        var wan4 = Tid(Suit.Wan, 4, 0);

        // CONTROL: absent any lockout, Wan-4 is a genuine winning tile for seat 1 — proving
        // Wan-4 is a DIFFERENT winning tile from the Wan-1 that will be missed below.
        var wan4Opportunities = new ClaimAdjudicator().GetOpportunities(0, wan4, state.Hands);
        Assert.Contains(wan4Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);

        // Seat 1 misses the Hu on Wan-1 (passes) → flagged seat-level.
        ChangshaGameStateMachine.Discard(state, 0, wan1);
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.Contains(state.ClaimWindow!.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);
        ChangshaGameStateMachine.PassClaim(state);
        Assert.Contains(1, state.MissedWinSeats);

        // Dealer now discards the DIFFERENT winning tile Wan-4. Under the current
        // seat-level lockout, seat 1's Hu is STILL stripped even though this is not the
        // tile they missed. (A tile-specific reading of §3.6 would allow this Hu.)
        state.ActiveSeatIndex = 0;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        ChangshaGameStateMachine.Discard(state, 0, wan4);

        if (state.ClaimWindow is null)
        {
            Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        }
        else
        {
            Assert.DoesNotContain(state.ClaimWindow.Opportunities,
                o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);
        }
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "117-SpecReconciliation")]
    public void MissedWin_DecaysOnNextOwnDraw_RestoresHu_Characterization()
    {
        var state = SetupTwoSidedWait();
        var wan1 = Tid(Suit.Wan, 1, 0);

        // Seat 1 misses the Hu on Wan-1 → flagged.
        ChangshaGameStateMachine.Discard(state, 0, wan1);
        ChangshaGameStateMachine.PassClaim(state);
        Assert.Contains(1, state.MissedWinSeats);

        // Per Baidu §过水 ("until your next draw"), seat 1 drawing a tile clears the lockout.
        state.ActiveSeatIndex = 1;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        Assert.NotEmpty(state.Wall);
        ChangshaGameStateMachine.DrawTile(state);

        Assert.DoesNotContain(1, state.MissedWinSeats);
    }
}
