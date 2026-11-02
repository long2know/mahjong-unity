using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Shared scaffolding for Phase D-tests acceptance suite (Vasquez, 2026-05-19).
///
/// These tests define the contract for "fully playable Changsha." They drive the pure
/// <see cref="ChangshaGameStateMachine"/> directly — the WS relay layer (Bishop's Phase D-backend)
/// will graft this rules engine onto autotable's protocol; the rule outcomes asserted here
/// are what the relay must surface unchanged.
///
/// Acceptance shape:
///   - Deterministic seed-driven setup.
///   - State-machine command surface only (no live HTTP, no SignalR client).
///   - Each test cites the Changsha source axis it's anchored on
///     (Vasquez rules-diff manifest §1.5–§1.13 / MahjongPros / Baidu / Reddit).
/// </summary>
internal static class AcceptanceFixture
{
    /// <summary>
    /// Build a four-bot game already dealt, dealer = seat 0, missed-win flags clear.
    /// </summary>
    public static ChangshaGameState NewDealtGame(int seed = 42, int dealerSeat = 0)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed,
            botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats)
            s.IsDealer = s.SeatIndex == dealerSeat;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
        ChangshaGameStateMachine.Deal(state);
        state.ActiveSeatIndex = dealerSeat;
        return state;
    }

    /// <summary>
    /// 13-tile near-winning hand that completes on Wan-1: chow Wan-1-2-3 + Wan-4-5-6 + Wan-7-8-9
    /// + Tong-1-2-3 chow + Tong-5-Tong-5 pair (258-compliant).
    /// </summary>
    public static List<int> ThirteenTileWaitingForWan1()
        => new()
        {
            ChangshaTestHelpers.Tid(Suit.Wan, 2, 0), ChangshaTestHelpers.Tid(Suit.Wan, 3, 0),
            ChangshaTestHelpers.Tid(Suit.Wan, 4, 0), ChangshaTestHelpers.Tid(Suit.Wan, 5, 0),
            ChangshaTestHelpers.Tid(Suit.Wan, 6, 0),
            ChangshaTestHelpers.Tid(Suit.Wan, 7, 0), ChangshaTestHelpers.Tid(Suit.Wan, 8, 0),
            ChangshaTestHelpers.Tid(Suit.Wan, 9, 0),
            ChangshaTestHelpers.Tid(Suit.Tong, 5, 0), ChangshaTestHelpers.Tid(Suit.Tong, 5, 1),
            ChangshaTestHelpers.Tid(Suit.Tong, 1, 0), ChangshaTestHelpers.Tid(Suit.Tong, 2, 0),
            ChangshaTestHelpers.Tid(Suit.Tong, 3, 0)
        };

    /// <summary>
    /// Place the given tile IDs into the seat's hand, clearing whatever was there. Used by
    /// claim-priority tests to deterministically construct the discard-window scenario.
    /// </summary>
    public static void OverrideHand(ChangshaGameState state, int seatIndex, params int[] tileIds)
    {
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    /// <summary>
    /// Strip every tile of a given logical-rank from all hands; used to remove pre-existing
    /// copies of an injected scenario tile so the claim window opens with the exact opportunities
    /// the test expects (deterministic across all seeds).
    /// </summary>
    public static void StripLogicalFromAllHands(ChangshaGameState state, int logicalTile)
    {
        foreach (var hand in state.Hands)
            hand.ConcealedTiles.RemoveAll(t => t / 4 == logicalTile);
    }

    /// <summary>
    /// Drive a claim window to resolution via the highest-priority opportunity (no client choice).
    /// Returns the resolved claim type; null if the window was passed.
    /// </summary>
    public static TableClaimType? ResolveByPriority(ChangshaGameState state)
    {
        if (state.ClaimWindow is null) return null;
        var winner = state.ClaimWindow.Opportunities
            .OrderByDescending(o => o.Priority)
            .ThenBy(o => ChangshaClaimPriority.CounterClockwiseDistance(
                state.ClaimWindow.DiscardSeatIndex, o.SeatIndex))
            .ThenBy(o => o.SeatIndex)
            .FirstOrDefault();
        if (winner is null)
        {
            ChangshaGameStateMachine.PassClaim(state);
            return null;
        }
        ChangshaGameStateMachine.ResolveClaim(state, winner.SeatIndex, winner.ClaimType);
        return winner.ClaimType;
    }
}
