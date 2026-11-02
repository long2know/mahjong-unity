using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-I: State machine, event log, and version invariants.
/// </summary>
public class StateMachineTests
{
    [Fact, Trait("Category", "Changsha")]
    public void CreateGame_StartsInSeating_EmitsGameCreatedEvent()
    {
        var (state, events) = ChangshaGameStateMachine.CreateGame(seed: 5);

        Assert.Equal(ChangshaPhase.Seating, state.Phase);
        Assert.Equal(4, state.Seats.Count);
        Assert.Contains(events, e => e.EventType == "game-created");
    }

    [Fact, Trait("Category", "Changsha")]
    public void StartGame_TransitionsToRollingDice()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 5);
        ChangshaGameStateMachine.StartGame(state);

        Assert.Equal(ChangshaPhase.RollingDice, state.Phase);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Phase_MisuseFromSeating_DealRequiresDealingPhase_Throws()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 5);
        Assert.Throws<InvalidOperationException>(() => ChangshaGameStateMachine.Deal(state));
    }

    [Fact, Trait("Category", "Changsha")]
    public void StateVersion_Monotonically_Increases()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 5);
        var v0 = state.StateVersion;
        ChangshaGameStateMachine.StartGame(state);
        var v1 = state.StateVersion;
        ChangshaGameStateMachine.RollDice(state, new DiceService(5));
        var v2 = state.StateVersion;

        Assert.True(v1 > v0);
        Assert.True(v2 > v1);
    }

    [Fact, Trait("Category", "Changsha")]
    public void EventSequence_AlwaysIncrementing_NoGaps()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 3);
        var sequences = state.EventLog.Select(e => e.Sequence).ToList();

        Assert.NotEmpty(sequences);
        for (var i = 1; i < sequences.Count; i++)
            Assert.Equal(sequences[i - 1] + 1, sequences[i]);
    }

    [Fact, Trait("Category", "Changsha")]
    public void EventLog_RecordsAllPhaseTransitions()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 3);
        var types = state.EventLog.Select(e => e.EventType).ToList();

        Assert.Contains("game-created", types);
        Assert.Contains("game-started", types);
        Assert.Contains("dice-rolled", types);
        Assert.Contains("tiles-dealt", types);
    }

    [Fact, Trait("Category", "Changsha")]
    public void DeterministicReplay_SameSeed_ProducesSameEventTypes()
    {
        var s1 = ChangshaTestHelpers.NewGameDealtTo(seed: 77);
        var s2 = ChangshaTestHelpers.NewGameDealtTo(seed: 77);

        Assert.Equal(
            s1.EventLog.Select(e => e.EventType).ToList(),
            s2.EventLog.Select(e => e.EventType).ToList());
        Assert.Equal(s1.Hands[0].ConcealedTiles, s2.Hands[0].ConcealedTiles);
    }

    [Fact, Trait("Category", "Changsha")]
    public void DeclareSelfDrawWin_OnNonWinningHand_Throws()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 1);
        // The dealer's randomly dealt hand is overwhelmingly unlikely to be a winning hand.
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.DeclareSelfDrawWin(state, state.DealerSeatIndex));
    }
}
