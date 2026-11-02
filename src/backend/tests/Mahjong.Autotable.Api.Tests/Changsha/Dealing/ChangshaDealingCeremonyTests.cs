using Mahjong.Autotable.Api.Changsha.Dealing;

namespace Mahjong.Autotable.Api.Tests.Changsha.Dealing;

/// <summary>
/// Frost — Pure-function coverage of the Changsha dealing ceremony engine.
/// Exercises every phase transition + every dice sum (2..12) for both wall
/// and break-index, plus a full-deal smoke that drives the entire 14-pickup
/// sequence and asserts dealer ends at 14 and the other three at 13.
///
/// <para>NOTE: This suite does NOT touch <c>ChangshaGameRuntime</c> /
/// <c>ChangshaStateMachine</c> — the ceremony is a pure rule engine and is
/// tested in isolation. The runtime-side state-machine parity is covered by
/// existing tests under <c>tests/.../Changsha/</c>.</para>
/// </summary>
public class ChangshaDealingCeremonyTests
{
    // ── Start ────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void Start_FreshSession_PhaseIsWaitingForDice()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);

        Assert.Equal(ChangshaDealingPhase.WaitingForDice, s.Phase);
        Assert.Equal(0, s.DealerSeat);
        Assert.Equal(0, s.CurrentPickerSeat);
        Assert.Equal(0, s.RoundIndex);
        Assert.Equal(0, s.TilesTakenThisRound);
        Assert.Null(s.DiceRoll);
        Assert.Null(s.StartingWall);
        Assert.Null(s.BreakIndex);
        Assert.Equal(new[] { 0, 0, 0, 0 }, s.HandSizes);
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Start_DealerSeatStored_AndUsedAsInitialPicker(int dealer)
    {
        var s = ChangshaDealingCeremony.Start(dealer);

        Assert.Equal(dealer, s.DealerSeat);
        Assert.Equal(dealer, s.CurrentPickerSeat);
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void Start_InvalidDealerSeat_Throws(int dealer)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChangshaDealingCeremony.Start(dealer));
    }

    // ── ApplyDiceRoll ────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void ApplyDiceRoll_ValidDice_TransitionsToPickingFour()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);

        var next = ChangshaDealingCeremony.ApplyDiceRoll(s, new[] { 3, 5 });

        Assert.Equal(ChangshaDealingPhase.PickingFour, next.Phase);
        Assert.Equal(new[] { 3, 5 }, next.DiceRoll);
        Assert.Equal(0, next.RoundIndex);
        Assert.Equal(0, next.TilesTakenThisRound);
        Assert.Equal(0, next.CurrentPickerSeat); // dealer picks first
        Assert.NotNull(next.StartingWall);
        Assert.NotNull(next.BreakIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ApplyDiceRoll_DoesNotMutateInputState()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 2);

        _ = ChangshaDealingCeremony.ApplyDiceRoll(s, new[] { 4, 4 });

        Assert.Null(s.DiceRoll);
        Assert.Null(s.StartingWall);
        Assert.Null(s.BreakIndex);
        Assert.Equal(ChangshaDealingPhase.WaitingForDice, s.Phase);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ApplyDiceRoll_DiceCloned_NotAliased()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 1);
        var dice = new[] { 2, 6 };

        var next = ChangshaDealingCeremony.ApplyDiceRoll(s, dice);

        dice[0] = 99;
        Assert.Equal(2, next.DiceRoll![0]);
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(13, 0)]
    public void ApplyDiceRoll_DieOutOfRange_Throws(int die1, int die2)
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);
        Assert.Throws<ArgumentException>(() => ChangshaDealingCeremony.ApplyDiceRoll(s, new[] { die1, die2 }));
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(1)]
    [InlineData(3)]
    public void ApplyDiceRoll_WrongDiceCount_Throws(int count)
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);
        var dice = Enumerable.Repeat(3, count).ToArray();
        Assert.Throws<ArgumentException>(() => ChangshaDealingCeremony.ApplyDiceRoll(s, dice));
    }

    [Fact, Trait("Category", "Changsha")]
    public void ApplyDiceRoll_OutOfPhase_Throws()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);
        var afterRoll = ChangshaDealingCeremony.ApplyDiceRoll(s, new[] { 3, 4 });

        Assert.Throws<InvalidOperationException>(
            () => ChangshaDealingCeremony.ApplyDiceRoll(afterRoll, new[] { 1, 1 }));
    }

    // ── Dice-sum table-driven wall + break-index ─────────────────────

    [Theory, Trait("Category", "Changsha")]
    [InlineData(0, 2, 1)]
    [InlineData(0, 3, 2)]
    [InlineData(0, 4, 3)]
    [InlineData(0, 5, 0)]
    [InlineData(0, 6, 1)]
    [InlineData(0, 7, 2)]
    [InlineData(0, 8, 3)]
    [InlineData(0, 9, 0)]
    [InlineData(0, 10, 1)]
    [InlineData(0, 11, 2)]
    [InlineData(0, 12, 3)]
    [InlineData(1, 5, 1)]
    [InlineData(2, 5, 2)]
    [InlineData(3, 5, 3)]
    [InlineData(3, 4, 2)]
    public void ComputeStartingWall_AllDiceValues_MatchesRules(int dealer, int sum, int expected)
    {
        Assert.Equal(expected, ChangshaDealingCeremony.ComputeStartingWall(dealer, sum));
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(2, 4)]
    [InlineData(3, 6)]
    [InlineData(4, 8)]
    [InlineData(5, 10)]
    [InlineData(6, 12)]
    [InlineData(7, 14)]
    [InlineData(8, 16)]
    [InlineData(9, 18)]
    [InlineData(10, 20)]
    [InlineData(11, 22)]
    [InlineData(12, 24)]
    public void ComputeBreakIndex_AllDiceValues_MatchesRules(int sum, int expectedTileIndex)
    {
        Assert.Equal(expectedTileIndex, ChangshaDealingCeremony.ComputeBreakIndex(sum));
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(-1)]
    public void ComputeBreakIndex_InvalidSum_Throws(int sum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChangshaDealingCeremony.ComputeBreakIndex(sum));
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(0, 1)]
    [InlineData(0, 13)]
    [InlineData(-1, 5)]
    [InlineData(4, 5)]
    public void ComputeStartingWall_InvalidArgs_Throws(int dealer, int sum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ChangshaDealingCeremony.ComputeStartingWall(dealer, sum));
    }

    [Fact, Trait("Category", "Changsha")]
    public void ApplyDiceRoll_DiceSum2_StartsAtCorrectWall()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);
        var next = ChangshaDealingCeremony.ApplyDiceRoll(s, new[] { 1, 1 });

        Assert.Equal(1, next.StartingWall);
        Assert.Equal(4, next.BreakIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ApplyDiceRoll_DiceSum12_StartsAtCorrectWall()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);
        var next = ChangshaDealingCeremony.ApplyDiceRoll(s, new[] { 6, 6 });

        Assert.Equal(3, next.StartingWall);
        Assert.Equal(24, next.BreakIndex);
    }

    // ── ValidateAndApplyPickup ───────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_DealerPicksFour_AdvancesToNextSeatCCW()
    {
        var s = AfterDiceRoll(dealer: 0, dice: new[] { 3, 4 });

        var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 0, requestedCount: 4);

        Assert.True(r.Valid);
        Assert.Null(r.RejectReason);
        Assert.Equal(4, r.TilesPickedUp);
        Assert.Equal(4, r.NewState.HandSizes[0]);
        Assert.Equal(1, r.NewState.CurrentPickerSeat);
        Assert.Equal(1, r.NewState.TilesTakenThisRound);
        Assert.Equal(ChangshaDealingPhase.PickingFour, r.NewState.Phase);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_OutOfTurnSeat_Rejected()
    {
        var s = AfterDiceRoll(dealer: 0, dice: new[] { 3, 4 });

        var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 2, requestedCount: 4);

        Assert.False(r.Valid);
        Assert.NotNull(r.RejectReason);
        Assert.Contains("not the active picker", r.RejectReason);
        Assert.Equal(0, r.TilesPickedUp);
        Assert.Equal(0, r.NewState.HandSizes[2]);
        Assert.Same(s, r.NewState);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_PickFiveWhileExpectingFour_Rejected()
    {
        var s = AfterDiceRoll(dealer: 0, dice: new[] { 3, 4 });

        var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 0, requestedCount: 5);

        Assert.False(r.Valid);
        Assert.NotNull(r.RejectReason);
        Assert.Contains("count mismatch", r.RejectReason);
        Assert.Equal(0, r.NewState.HandSizes[0]);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_BeforeDice_Rejected()
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: 0);

        var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 0, requestedCount: 4);

        Assert.False(r.Valid);
        Assert.Contains("dice have not been rolled", r.RejectReason);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_SeatIndexOutOfRange_Rejected()
    {
        var s = AfterDiceRoll(dealer: 0, dice: new[] { 3, 4 });

        var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 7, requestedCount: 4);

        Assert.False(r.Valid);
        Assert.Contains("Seat index", r.RejectReason);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_PickingOne_ExpectsCountOne()
    {
        var s = AfterDiceRoll(dealer: 0, dice: new[] { 3, 4 });
        for (var round = 0; round < 3; round++)
        {
            for (var seat = 0; seat < 4; seat++)
            {
                var picker = (0 + seat) % 4;
                var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, picker, 4);
                Assert.True(r.Valid, r.RejectReason);
                s = r.NewState;
            }
        }

        Assert.Equal(ChangshaDealingPhase.PickingOne, s.Phase);

        var bad = ChangshaDealingCeremony.ValidateAndApplyPickup(s, 0, 4);
        Assert.False(bad.Valid);
        Assert.Contains("count mismatch", bad.RejectReason);

        var good = ChangshaDealingCeremony.ValidateAndApplyPickup(s, 0, 1);
        Assert.True(good.Valid);
        Assert.Equal(13, good.NewState.HandSizes[0]);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_RoundCompletion_RotatesBackToDealer()
    {
        var s = AfterDiceRoll(dealer: 1, dice: new[] { 2, 3 });

        var pickOrder = new[] { 1, 2, 3, 0 };
        foreach (var seat in pickOrder)
        {
            var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seat, 4);
            Assert.True(r.Valid, r.RejectReason);
            s = r.NewState;
        }

        Assert.Equal(1, s.RoundIndex);
        Assert.Equal(1, s.CurrentPickerSeat);
        Assert.Equal(0, s.TilesTakenThisRound);
        Assert.Equal(ChangshaDealingPhase.PickingFour, s.Phase);
        Assert.All(s.HandSizes, h => Assert.Equal(4, h));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Sequence_FullDeal_DealerEnds14_OthersEnd13()
    {
        var final = RunFullDeal(dealer: 0, dice: new[] { 3, 4 });

        Assert.Equal(ChangshaDealingPhase.Complete, final.Phase);
        Assert.Equal(14, final.HandSizes[0]);
        Assert.Equal(13, final.HandSizes[1]);
        Assert.Equal(13, final.HandSizes[2]);
        Assert.Equal(13, final.HandSizes[3]);
        Assert.Equal(0, final.CurrentPickerSeat);
        Assert.Equal(0, final.TilesTakenThisRound);
        Assert.Equal(53, final.HandSizes.Sum());
    }

    [Theory, Trait("Category", "Changsha")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Sequence_FullDeal_EveryDealerWorks(int dealer)
    {
        var final = RunFullDeal(dealer: dealer, dice: new[] { 4, 5 });

        Assert.Equal(ChangshaDealingPhase.Complete, final.Phase);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(i == dealer ? 14 : 13, final.HandSizes[i]);
        }
    }

    [Fact, Trait("Category", "Changsha")]
    public void Sequence_PhaseTransitions_FollowRulesExactly()
    {
        var s = AfterDiceRoll(dealer: 0, dice: new[] { 3, 4 });
        var transitionLog = new List<(ChangshaDealingPhase, int)>();

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                transitionLog.Add((s.Phase, s.RoundIndex));
                var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, s.CurrentPickerSeat, 4);
                Assert.True(r.Valid);
                s = r.NewState;
            }
        }

        Assert.Equal(ChangshaDealingPhase.PickingOne, s.Phase);
        Assert.Equal(3, s.RoundIndex);

        for (var i = 0; i < 4; i++)
        {
            transitionLog.Add((s.Phase, s.RoundIndex));
            var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, s.CurrentPickerSeat, 1);
            Assert.True(r.Valid);
            s = r.NewState;
        }

        Assert.Equal(ChangshaDealingPhase.DealerExtra, s.Phase);

        transitionLog.Add((s.Phase, s.RoundIndex));
        var extra = ChangshaDealingCeremony.ValidateAndApplyPickup(s, 0, 1);
        Assert.True(extra.Valid);

        Assert.Equal(ChangshaDealingPhase.Complete, extra.NewState.Phase);

        Assert.Equal(17, transitionLog.Count);
        Assert.Equal(12, transitionLog.Count(t => t.Item1 == ChangshaDealingPhase.PickingFour));
        Assert.Equal(4, transitionLog.Count(t => t.Item1 == ChangshaDealingPhase.PickingOne));
        Assert.Equal(1, transitionLog.Count(t => t.Item1 == ChangshaDealingPhase.DealerExtra));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Sequence_DealerExtra_OnlyDealerMayPick()
    {
        var s = AfterDiceRoll(dealer: 2, dice: new[] { 2, 2 });
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, s.CurrentPickerSeat, 4);
                s = r.NewState;
            }
        }
        for (var i = 0; i < 4; i++)
        {
            var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, s.CurrentPickerSeat, 1);
            s = r.NewState;
        }

        Assert.Equal(ChangshaDealingPhase.DealerExtra, s.Phase);
        Assert.Equal(2, s.CurrentPickerSeat);

        var bad = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 0, requestedCount: 1);
        Assert.False(bad.Valid);
        Assert.Contains("not the active picker", bad.RejectReason);

        var ok = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 2, requestedCount: 1);
        Assert.True(ok.Valid);
        Assert.Equal(ChangshaDealingPhase.Complete, ok.NewState.Phase);
        Assert.Equal(14, ok.NewState.HandSizes[2]);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_AfterComplete_Rejected()
    {
        var s = RunFullDeal(dealer: 0, dice: new[] { 3, 4 });

        var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, seatIndex: 0, requestedCount: 1);

        Assert.False(r.Valid);
        Assert.Contains("already complete", r.RejectReason);
    }

    // ── Purity / immutability ────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void ValidateAndApplyPickup_DoesNotMutateInputState()
    {
        var s = AfterDiceRoll(dealer: 0, dice: new[] { 3, 4 });
        var originalHandSizes = (int[])s.HandSizes.Clone();
        var originalPicker = s.CurrentPickerSeat;

        _ = ChangshaDealingCeremony.ValidateAndApplyPickup(s, 0, 4);

        Assert.Equal(originalHandSizes, s.HandSizes);
        Assert.Equal(originalPicker, s.CurrentPickerSeat);
    }

    [Fact, Trait("Category", "Changsha")]
    public void ApplyDiceRoll_AllSums_ProducesValidWallAndBreak()
    {
        for (var d1 = 1; d1 <= 6; d1++)
        {
            for (var d2 = 1; d2 <= 6; d2++)
            {
                var s = ChangshaDealingCeremony.Start(dealerSeat: 0);
                var next = ChangshaDealingCeremony.ApplyDiceRoll(s, new[] { d1, d2 });
                var sum = d1 + d2;

                Assert.InRange(next.StartingWall!.Value, 0, 3);
                Assert.Equal(sum * 2, next.BreakIndex);
                Assert.Equal(ChangshaDealingPhase.PickingFour, next.Phase);
            }
        }
    }

    [Fact, Trait("Category", "Changsha")]
    public void ExpectedPickupCount_AllPhases_MatchSpec()
    {
        Assert.Equal(0, ChangshaDealingCeremony.ExpectedPickupCount(ChangshaDealingPhase.WaitingForDice));
        Assert.Equal(4, ChangshaDealingCeremony.ExpectedPickupCount(ChangshaDealingPhase.PickingFour));
        Assert.Equal(1, ChangshaDealingCeremony.ExpectedPickupCount(ChangshaDealingPhase.PickingOne));
        Assert.Equal(1, ChangshaDealingCeremony.ExpectedPickupCount(ChangshaDealingPhase.DealerExtra));
        Assert.Equal(0, ChangshaDealingCeremony.ExpectedPickupCount(ChangshaDealingPhase.Complete));
    }

    // ── Smoke: rules-driven full deal across all dealers + sums ──────

    [Theory, Trait("Category", "Changsha")]
    [InlineData(0, 2)]
    [InlineData(0, 7)]
    [InlineData(0, 12)]
    [InlineData(1, 4)]
    [InlineData(2, 9)]
    [InlineData(3, 11)]
    public void RulesSmoke_FullDealCombinatorial_AlwaysCompletes(int dealer, int sum)
    {
        var d1 = Math.Max(1, sum - 6);
        var d2 = sum - d1;
        var final = RunFullDeal(dealer: dealer, dice: new[] { d1, d2 });

        Assert.Equal(ChangshaDealingPhase.Complete, final.Phase);
        Assert.Equal(53, final.HandSizes.Sum());
        Assert.Equal(14, final.HandSizes[dealer]);
        for (var i = 0; i < 4; i++)
        {
            if (i != dealer) Assert.Equal(13, final.HandSizes[i]);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static ChangshaDealingState AfterDiceRoll(int dealer, int[] dice)
    {
        var s = ChangshaDealingCeremony.Start(dealerSeat: dealer);
        return ChangshaDealingCeremony.ApplyDiceRoll(s, dice);
    }

    private static ChangshaDealingState RunFullDeal(int dealer, int[] dice)
    {
        var s = AfterDiceRoll(dealer: dealer, dice: dice);

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, s.CurrentPickerSeat, 4);
                Assert.True(r.Valid, r.RejectReason);
                s = r.NewState;
            }
        }
        for (var i = 0; i < 4; i++)
        {
            var r = ChangshaDealingCeremony.ValidateAndApplyPickup(s, s.CurrentPickerSeat, 1);
            Assert.True(r.Valid, r.RejectReason);
            s = r.NewState;
        }
        var extra = ChangshaDealingCeremony.ValidateAndApplyPickup(s, dealer, 1);
        Assert.True(extra.Valid, extra.RejectReason);
        return extra.NewState;
    }
}
