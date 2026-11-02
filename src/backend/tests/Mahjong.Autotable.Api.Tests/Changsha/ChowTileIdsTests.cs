using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// FIX-4 (Phase 3 stream B): chow resolution must honor the explicit `tileIds` chosen by the
/// claimant when multiple valid chow patterns are possible. Legacy clients (no tileIds) fall
/// back to lowest-rank pattern. Invalid tileIds raise a contract error.
/// </summary>
public class ChowTileIdsTests
{
    /// <summary>
    /// Drive the state machine to the AwaitingClaim phase with seat 1 holding Wan-2/3/4/5
    /// and seat 0 having just discarded Wan-3. From seat 1's hand, both chows
    /// (Wan-1)2-3-(Wan-4) — wait, seat 1 needs to use 2 concealed tiles + the discarded 3.
    /// Patterns available with hand having 2,4,5 and discard 3:
    ///   • [2, 4] + 3 → chow 2-3-4 (lowest pattern)
    ///   • [4, 5] + 3 → chow 3-4-5
    /// </summary>
    private static ChangshaGameState BuildChowWindow(out int seat1ChowTile2, out int seat1ChowTile4, out int seat1ChowTile5)
    {
        var state = NewGameDealtTo(seed: 11, botSeats: new[] { 1, 2, 3 });
        var dealer = state.DealerSeatIndex;

        // Wipe seat 1's hand and inject Wan 2, 4, 5 only (plus filler tiles to satisfy any
        // total-tile invariants — but the state machine doesn't enforce 13 here, so leave it
        // sparse and just inject the chow ingredients).
        state.Hands[1].ConcealedTiles.Clear();
        seat1ChowTile2 = Tid(Suit.Wan, 2, 0);
        seat1ChowTile4 = Tid(Suit.Wan, 4, 0);
        seat1ChowTile5 = Tid(Suit.Wan, 5, 0);
        state.Hands[1].ConcealedTiles.AddRange(new[] { seat1ChowTile2, seat1ChowTile4, seat1ChowTile5 });

        // Force the dealer to hold Wan 3 (copy 1) and discard it.
        var wan3 = Tid(Suit.Wan, 3, 1);
        if (!state.Hands[dealer].ConcealedTiles.Contains(wan3))
            state.Hands[dealer].ConcealedTiles.Add(wan3);

        ChangshaGameStateMachine.Discard(state, dealer, wan3);
        Assert.Equal(ChangshaPhase.AwaitingClaim, state.Phase);
        Assert.NotNull(state.ClaimWindow);
        // The chow opportunity for seat 1 should be present.
        Assert.Contains(state.ClaimWindow!.Opportunities,
            o => o.SeatIndex == 1 && o.ClaimType == TableClaimType.Chow);
        return state;
    }

    [Fact, Trait("Category", "Changsha")]
    public void Chow_TileIdsRespected_WhenClaimantHasMultipleValidPatterns()
    {
        // Seat 1 holds Wan 2,4,5; discarder offered Wan 3.
        // Two valid chow patterns: {2,4} or {4,5}. Choosing {4,5} must produce a 3-4-5 chow,
        // NOT the lowest-pattern 2-3-4 fallback.
        var state = BuildChowWindow(out var w2, out var w4, out var w5);

        ChangshaGameStateMachine.ResolveClaim(
            state, claimingSeatIndex: 1, TableClaimType.Chow,
            chosenTileIds: new[] { w4, w5 });

        var meld = state.Hands[1].Melds.Single();
        Assert.Equal(MeldKind.Chow, meld.Kind);
        // Meld contains the chosen tiles + the discarded Wan-3 (copy 1).
        Assert.Contains(w4, meld.TileIds);
        Assert.Contains(w5, meld.TileIds);
        Assert.DoesNotContain(w2, meld.TileIds);
        // Wan-2 must remain in seat 1's hand (untouched).
        Assert.Contains(w2, state.Hands[1].ConcealedTiles);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Chow_EmptyTileIds_FallsBackToLowestPattern()
    {
        // Legacy contract: tileIds null/empty → fall back to lowest-rank pattern (2-3-4 here).
        var state = BuildChowWindow(out var w2, out var w4, out var w5);

        ChangshaGameStateMachine.ResolveClaim(
            state, claimingSeatIndex: 1, TableClaimType.Chow,
            chosenTileIds: null);

        var meld = state.Hands[1].Melds.Single();
        Assert.Equal(MeldKind.Chow, meld.Kind);
        Assert.Contains(w2, meld.TileIds); // lowest pattern uses Wan-2
        Assert.Contains(w4, meld.TileIds);
        Assert.DoesNotContain(w5, meld.TileIds);
        Assert.Contains(w5, state.Hands[1].ConcealedTiles);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Chow_InvalidTileIds_ReturnsContractError_NotInHand()
    {
        var state = BuildChowWindow(out var w2, out var w4, out _);
        // Pass tile IDs not in seat 1's hand.
        var foreign = Tid(Suit.Tong, 7, 0);

        var ex = Assert.Throws<TableRuleException>(() =>
            ChangshaGameStateMachine.ResolveClaim(
                state, 1, TableClaimType.Chow,
                chosenTileIds: new[] { foreign, w2 }));
        Assert.Equal(TableActionErrorCodes.ChowTilesInvalid, ex.Code);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Chow_InvalidTileIds_ReturnsContractError_NotSequential()
    {
        // Inject a Wan-8 into seat 1 and try to chow [4, 8] with Wan-3 discard — not consecutive.
        var state = BuildChowWindow(out _, out var w4, out _);
        var w8 = Tid(Suit.Wan, 8, 0);
        state.Hands[1].ConcealedTiles.Add(w8);

        var ex = Assert.Throws<TableRuleException>(() =>
            ChangshaGameStateMachine.ResolveClaim(
                state, 1, TableClaimType.Chow,
                chosenTileIds: new[] { w4, w8 }));
        Assert.Equal(TableActionErrorCodes.ChowTilesInvalid, ex.Code);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Chow_InvalidTileIds_ReturnsContractError_DifferentSuits()
    {
        var state = BuildChowWindow(out _, out var w4, out _);
        var tong4 = Tid(Suit.Tong, 4, 0);
        state.Hands[1].ConcealedTiles.Add(tong4);

        var ex = Assert.Throws<TableRuleException>(() =>
            ChangshaGameStateMachine.ResolveClaim(
                state, 1, TableClaimType.Chow,
                chosenTileIds: new[] { w4, tong4 }));
        Assert.Equal(TableActionErrorCodes.ChowTilesInvalid, ex.Code);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Chow_TileIds_WrongCount_ReturnsContractError()
    {
        var state = BuildChowWindow(out _, out var w4, out _);

        var ex = Assert.Throws<TableRuleException>(() =>
            ChangshaGameStateMachine.ResolveClaim(
                state, 1, TableClaimType.Chow,
                chosenTileIds: new[] { w4 })); // only 1 tile — must be exactly 2
        Assert.Equal(TableActionErrorCodes.ChowTilesInvalid, ex.Code);
    }
}
