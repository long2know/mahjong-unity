using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-C: Initial Deal Tests
/// </summary>
public class InitialDealTests
{
    [Fact, Trait("Category", "Changsha")]
    public void Deal_DealerReceives14_OthersReceive13()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);

        Assert.Equal(14, state.Hands[state.DealerSeatIndex].ConcealedTiles.Count);
        for (var i = 0; i < 4; i++)
            if (i != state.DealerSeatIndex)
                Assert.Equal(13, state.Hands[i].ConcealedTiles.Count);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Deal_LeavesExactly55TilesInWall()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        Assert.Equal(55, state.Wall.Count);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Deal_AllTilesAccountedFor_NoLossesOrDuplicates()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 13);

        var allTiles = new List<int>();
        foreach (var h in state.Hands) allTiles.AddRange(h.ConcealedTiles);
        allTiles.AddRange(state.Wall);

        Assert.Equal(108, allTiles.Count);
        Assert.Equal(108, allTiles.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 108).ToHashSet(), allTiles.ToHashSet());
    }

    [Fact, Trait("Category", "Changsha")]
    public void Deal_Order_BatchOfFourCounterClockwiseFromDealer()
    {
        // Build a known wall (0..107 in order), deal directly without break-point reordering.
        var wall = ChangshaDeckBuilder.Build();
        var result = new DealService().Deal(wall, dealerSeatIndex: 0);

        // First 4 to dealer, next 4 to seat 1, etc.; then round 2, round 3, then 1 each, then dealer extra.
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Hands[0].Take(4));
        Assert.Equal(new[] { 4, 5, 6, 7 }, result.Hands[1].Take(4));
        Assert.Equal(new[] { 8, 9, 10, 11 }, result.Hands[2].Take(4));
        Assert.Equal(new[] { 12, 13, 14, 15 }, result.Hands[3].Take(4));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Deal_DealerPhaseTransitionsToAwaitingDiscard()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(state.DealerSeatIndex, state.ActiveSeatIndex);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Deal_RejectsWallNotExactly108Tiles()
    {
        var shortWall = Enumerable.Range(0, 100).ToList();
        Assert.Throws<ArgumentException>(() => new DealService().Deal(shortWall, 0));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Deal_EmitsTilesDealtEvent()
    {
        var (state, events) = ChangshaGameStateMachine.CreateGame(seed: 7);
        events.AddRange(ChangshaGameStateMachine.StartGame(state));
        events.AddRange(ChangshaGameStateMachine.RollDice(state, new DiceService(7)));
        var dealEvents = ChangshaGameStateMachine.Deal(state);

        Assert.Contains(dealEvents, e => e.EventType == "tiles-dealt");
    }
}
