using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: missed-win (过胡) lockout per Vasquez §1.7 + Baidu §"过胡":
///   A seat that declines a winning discard is forbidden from winning on subsequent
///   discards in the same hand. Self-draw is still allowed. Lockout clears on every new hand.
///
/// And: 诈胡 (false-Hu declaration) — per Baidu §"诈胡处罚", declaring Hu on a non-winning hand
/// should trigger a penalty. V1 runtime throws an exception; Phase D-backend must define the
/// penalty payment shape. Tests covering the penalty payment are SKIPPED with descriptive reasons.
/// </summary>
public class MissedWinPenaltyTests
{
    private static ChangshaGameState BuildSeat1Tenpai()
    {
        // Standard tenpai for seat 1: waiting on Wan-1, 258-compliant pair (Tong-5).
        var state = AcceptanceFixture.NewDealtGame(seed: 41, dealerSeat: 0);

        foreach (var hand in state.Hands)
            hand.ConcealedTiles.RemoveAll(t =>
            {
                var logical = t / 4;
                return logical >= Logical(Suit.Wan, 1) && logical <= Logical(Suit.Wan, 9)
                    || logical == Logical(Suit.Tong, 1)
                    || logical == Logical(Suit.Tong, 2)
                    || logical == Logical(Suit.Tong, 3)
                    || logical == Logical(Suit.Tong, 5);
            });
        AcceptanceFixture.OverrideHand(state, 1, AcceptanceFixture.ThirteenTileWaitingForWan1().ToArray());

        // Dealer hand needs a Wan-1 to discard. Reset to known 14-tile shape.
        var dealerHand = state.Hands[0];
        dealerHand.ConcealedTiles.Clear();
        dealerHand.ConcealedTiles.Add(Tid(Suit.Wan, 1, 0));
        for (var i = 1; i < 14; i++)
            dealerHand.ConcealedTiles.Add(Tid(Suit.Tiao, ((i - 1) % 9) + 1, (i - 1) / 9));
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.ActiveSeatIndex = 0;
        return state;
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Player_PassesOnHuOpportunity_LocksOutFutureDiscardHu()
    {
        // Baidu §"过胡": once a seat passes on a winning discard, they cannot Hu on that
        // tile (or any subsequent winning discard) until their next draw.
        var state = BuildSeat1Tenpai();

        ChangshaGameStateMachine.Discard(state, 0, Tid(Suit.Wan, 1, 0));
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.Contains(state.ClaimWindow!.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);

        ChangshaGameStateMachine.PassClaim(state);

        Assert.Contains(1, state.MissedWinSeats);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Player_AfterMissedWin_CannotHuOnNextWinningDiscard_SameHand()
    {
        // Per spec §3.6 the lockout persists for the remainder of the hand. Drive a second
        // winning discard and assert seat 1's Hu opportunity is filtered out.
        var state = BuildSeat1Tenpai();
        ChangshaGameStateMachine.Discard(state, 0, Tid(Suit.Wan, 1, 0));
        ChangshaGameStateMachine.PassClaim(state);
        Assert.Contains(1, state.MissedWinSeats);

        // Reset dealer to AwaitingDiscard with a second Wan-1 copy.
        state.ActiveSeatIndex = 0;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        var wan1Copy2 = Tid(Suit.Wan, 1, 1);
        state.Hands[0].ConcealedTiles.Add(wan1Copy2);

        ChangshaGameStateMachine.Discard(state, 0, wan1Copy2);

        if (state.ClaimWindow is not null)
        {
            Assert.DoesNotContain(state.ClaimWindow.Opportunities,
                o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Hu);
        }
        else
        {
            Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Player_AfterMissedWin_StillAllowedToSelfDrawWin()
    {
        // §3.6: missed-win lockout does NOT block self-draw — only discard wins.
        var state = AcceptanceFixture.NewDealtGame(seed: 41, dealerSeat: 0);
        state.MissedWinSeats.Add(0);

        // Replace dealer hand with a known Standard winning hand (258 pair of Tong-5).
        AcceptanceFixture.OverrideHand(state, 0,
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 8, 0), Tid(Suit.Wan, 9, 0),
            Tid(Suit.Tong, 5, 0), Tid(Suit.Tong, 5, 1),
            Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0));
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.ActiveSeatIndex = 0;

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, 0);

        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.SelfDraw, state.CurrentWin!.Method);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Player_DeclaresHuOnNonWinningHand_RuntimeRejects()
    {
        // 诈胡 (false Hu): the runtime today throws to abort the action. Phase D-backend must
        // decide whether to also assess a payment penalty per Baidu §"诈胡处罚".
        var state = AcceptanceFixture.NewDealtGame(seed: 99, dealerSeat: 0);

        // Dealer's randomly dealt hand is overwhelmingly unlikely to be a winning hand.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.DeclareSelfDrawWin(state, 0));
        Assert.Contains("not a winning hand", ex.Message);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Player_FalseHuDeclaration_AppliesPenaltyToOtherThreeSeats()
    {
        // Once Phase D-backend implements the penalty:
        //   1. The runtime should NOT throw; it should record the false declaration.
        //   2. The penalty payment should be applied (typical: Big-Win equivalent, ~6 units to each).
        //   3. CumulativeScores must reflect the deductions.
        var state = AcceptanceFixture.NewDealtGame(seed: 99, dealerSeat: 0);
        // Ensure baseline scores exist for all four seats (zero) so the assertions below
        // can index into CumulativeScores without nullables.
        for (var s = 0; s < 4; s++)
            if (!state.CumulativeScores.ContainsKey(s)) state.CumulativeScores[s] = 0;

        // (1) RecordFalseHu does NOT throw and returns a penalty descriptor.
        var penalty = ChangshaGameStateMachine.RecordFalseHu(state, seatIndex: 0);
        Assert.NotNull(penalty);

        // (2) Big-Win equivalent: 6 units to each of the three opponents.
        Assert.Equal(6, penalty.PenaltyPerOpponent);
        Assert.Equal(3, penalty.Payments.Count);

        // (3) Cumulative scores: caller -18, each opponent +6.
        Assert.Equal(-18, state.CumulativeScores[0]);
        Assert.Equal(6, state.CumulativeScores[1]);
        Assert.Equal(6, state.CumulativeScores[2]);
        Assert.Equal(6, state.CumulativeScores[3]);

        // Zero-sum invariant (Vasquez §5).
        Assert.Equal(0, state.CumulativeScores.Values.Sum());

        // Audit log records the offence.
        Assert.Single(state.FalseHuPenalties);
        Assert.Equal(0, state.FalseHuPenalties[0].OffendingSeatIndex);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Player_MissedWinLockout_ClearsAfterTheirNextDraw()
    {
        // Today's implementation: MissedWinSeats persists for the entire hand until Deal() clears it.
        // Per Baidu, the lockout is only "until your next draw" — after drawing, the seat can Hu again.
        // Phase D-backend must:
        //   1. On DrawTile() for a missed-win seat, remove them from MissedWinSeats.
        //   2. Keep them blocked until then.
        var state = BuildSeat1Tenpai();

        // Seat 0 discards Wan-1; seat 1 (tenpai for Wan-1) passes → locked out.
        ChangshaGameStateMachine.Discard(state, 0, Tid(Suit.Wan, 1, 0));
        ChangshaGameStateMachine.PassClaim(state);
        Assert.Contains(1, state.MissedWinSeats);

        // Advance the turn until seat 1 draws. The first AdvanceToNextPlayer after a
        // passed claim makes seat 1 active (CCW from discarder seat 0).
        Assert.Equal(1, state.ActiveSeatIndex);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);

        ChangshaGameStateMachine.DrawTile(state);

        // Lockout cleared by the draw.
        Assert.DoesNotContain(1, state.MissedWinSeats);
    }
}
