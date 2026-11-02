using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase H Wave 2 — Robbing the (added/exposed) Kong (抢杠胡) per Ripley's design
/// memo §2.2. Drives the new sub-state machine grafted onto <c>DeclareAddedKong</c>:
///
///   AwaitingDiscard
///     → DeclareAddedKong [Kind=Added, candidate-tile-id captured]
///       → ClaimWindow{IsKongRobbing=true, Opportunities=Hu-only}
///         ├─ Hu claimed → CurrentWin{Method=RobbingKong, IsRobbedKong=true,
///         │                          SourceSeatIndex=declarer}
///         └─ all pass → kong completes (replacement draw, back to AwaitingDiscard)
///
/// Concealed kong (暗杠) is NOT robbable — verified by the
/// <see cref="ConcealedKong_IsNotRobbable"/> Fact.
///
/// Tests reach for Bishop's symbols via reflection (<c>WinResult.IsRobbedKong</c>,
/// <c>ChangshaClaimWindow.IsKongRobbing</c>) so the assembly compiles before his
/// contract commits land. RED-fail messages name the missing symbol.
/// </summary>
public class RobbingKongAcceptanceTests
{
    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void AddedKong_OpensRobbingHuWindow_WhenOtherSeatCanHu()
    {
        // Phase H Wave 2 §2.2 (declarer-side, opportunity-scan view): when seat 0
        // declares an added kong on Wan-5 and seat 2's hand completes on Wan-5,
        // DeclareAddedKong must open a Hu-only claim window naming seat 2.
        var state = BuildRobbingKongScenario(seat2CanHu: true);

        ChangshaGameStateMachine.DeclareAddedKong(state, seatIndex: 0,
            tileId: Tid(Suit.Wan, 5, 3));

        Assert.NotNull(state.ClaimWindow);
        AssertIsKongRobbingWindow(state.ClaimWindow!,
            "Added-kong on a tile that another seat can Hu must open a kong-robbing claim window. " +
            "Bishop owes the Phase H Wave 2 contract (ChangshaClaimWindow.IsKongRobbing=true).");

        var seat2HuOpps = state.ClaimWindow!.Opportunities
            .Where(o => o.SeatIndex == 2 && o.ClaimType == TableClaimType.Hu)
            .ToList();
        Assert.True(seat2HuOpps.Count == 1,
            $"Robbing-kong window must surface exactly 1 Hu opportunity for seat 2. " +
            $"Got [{string.Join(",", state.ClaimWindow.Opportunities.Select(o => $"seat={o.SeatIndex}:{o.ClaimType}"))}].");

        // The window should NOT advertise Pung/Kong/Chow (the tile is being added to a
        // kong, not discarded into the river).
        Assert.DoesNotContain(state.ClaimWindow.Opportunities,
            o => o.ClaimType is TableClaimType.Pung or TableClaimType.Kong or TableClaimType.Chow);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void AddedKong_AwardsHuToRobber_WithIsRobbedKongFlag()
    {
        // Phase H Wave 2 §2.2 (claimer-side resolution view): when seat 2 claims Hu
        // on the kong-target tile, the resulting WinResult must tag Method=RobbingKong,
        // IsRobbedKong=true, SourceSeatIndex=declarer (seat 0). The hand transitions
        // to Scoring, the kong meld is NOT committed (kong was "robbed").
        var state = BuildRobbingKongScenario(seat2CanHu: true);

        ChangshaGameStateMachine.DeclareAddedKong(state, seatIndex: 0,
            tileId: Tid(Suit.Wan, 5, 3));

        // Robber declares Hu.
        ChangshaGameStateMachine.ResolveClaim(state, claimingSeatIndex: 2,
            claimType: TableClaimType.Hu);

        Assert.NotNull(state.CurrentWin);
        var win = state.CurrentWin!;

        Assert.Equal(WinMethod.RobbingKong, win.Method);
        Assert.Equal(2, win.WinningSeatIndex);
        Assert.Equal(0, win.SourceSeatIndex);

        var isRobbedKong = ResolveIsRobbedKong(win)
            ?? throw new InvalidOperationException(
                "WinResult.IsRobbedKong property not found — Bishop owes the Phase H Wave 2 contract.");
        Assert.True(isRobbedKong,
            "WinResult.IsRobbedKong must be true on a robbing-kong win.");

        Assert.Equal(ChangshaPhase.Scoring, state.Phase);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void AddedKong_SkipsClaimWindow_WhenNoSeatCanHu()
    {
        // Phase H Wave 2 §2.2 — opportunity-scan empty case: when no other seat can
        // Hu on the kong-target tile, DeclareAddedKong must NOT open a claim window
        // (no claim-window timeout delay), and the kong completes immediately
        // (replacement tile drawn, AddedKong meld committed, phase stays/returns to
        // AwaitingDiscard for the declarer to continue).
        var state = BuildRobbingKongScenario(seat2CanHu: false);

        var wallBefore = state.Wall.Count;
        ChangshaGameStateMachine.DeclareAddedKong(state, seatIndex: 0,
            tileId: Tid(Suit.Wan, 5, 3));

        Assert.Null(state.ClaimWindow);
        // Kong meld is committed regardless of robbing.
        var meld = state.Hands[0].Melds.Single(m =>
            m.TileIds.All(t => t / 4 == Logical(Suit.Wan, 5)));
        Assert.Equal(MeldKind.AddedKong, meld.Kind);
        Assert.Equal(4, meld.TileIds.Count);
        // Replacement tile drawn from BACK of wall.
        Assert.Equal(wallBefore - 1, state.Wall.Count);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void ConcealedKong_IsNotRobbable()
    {
        // Phase H Wave 2 §2.2 — concealed kong (暗杠) is explicitly NOT robbable
        // (spec §3.4.2; Baidu confirms). Even when seat 2's hand would complete on
        // the concealed-kong tile, DeclareConcealedKong must not open a claim window.
        var state = BuildRobbingKongScenario(seat2CanHu: true,
            seat0Mode: Seat0Mode.AllFourInConcealed);

        ChangshaGameStateMachine.DeclareConcealedKong(state, seatIndex: 0,
            logicalTile: Logical(Suit.Wan, 5));

        Assert.Null(state.ClaimWindow);
        var meld = state.Hands[0].Melds.Single(m =>
            m.TileIds.All(t => t / 4 == Logical(Suit.Wan, 5)));
        Assert.Equal(MeldKind.ConcealedKong, meld.Kind);
        Assert.Equal(4, meld.TileIds.Count);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Wave", "2")]
    public void AddedKong_AllSeatsPass_KongCompletesNormally()
    {
        // Phase H Wave 2 §2.2 — when a robbing-kong window opens but every eligible
        // seat passes, the kong completes normally (replacement tile, AddedKong meld,
        // claim window cleared). The declarer's turn continues.
        var state = BuildRobbingKongScenario(seat2CanHu: true);

        ChangshaGameStateMachine.DeclareAddedKong(state, seatIndex: 0,
            tileId: Tid(Suit.Wan, 5, 3));
        Assert.NotNull(state.ClaimWindow);

        ChangshaGameStateMachine.PassClaim(state);

        Assert.Null(state.ClaimWindow);
        var meld = state.Hands[0].Melds.Single(m =>
            m.TileIds.All(t => t / 4 == Logical(Suit.Wan, 5)));
        Assert.Equal(MeldKind.AddedKong, meld.Kind);
        Assert.Null(state.CurrentWin);
    }

    // ── Setup helpers ────────────────────────────────────────────────────────

    private enum Seat0Mode
    {
        /// <summary>Seat 0 holds an exposed Pung of Wan-5 + the 4th Wan-5 in concealed
        /// (the added-kong setup).</summary>
        PungPlusFourth = 0,

        /// <summary>Seat 0 holds all four Wan-5 in concealed (the concealed-kong setup).</summary>
        AllFourInConcealed = 1,
    }

    /// <summary>
    /// Construct an isolated game state ready for a kong-declaration test, with seat 0
    /// as the active dealer (in AwaitingDiscard), and seat 2's hand optionally arranged
    /// to win on Wan-5. Other seats hold empty hands so they never enter the opportunity
    /// scan.
    /// </summary>
    private static ChangshaGameState BuildRobbingKongScenario(
        bool seat2CanHu,
        Seat0Mode seat0Mode = Seat0Mode.PungPlusFourth)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 42,
            botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        state.ActiveSeatIndex = 0;

        // Strip Wan-5 from every hand and the wall so injection is deterministic.
        var wan5 = Logical(Suit.Wan, 5);
        foreach (var h in state.Hands) h.ConcealedTiles.RemoveAll(t => t / 4 == wan5);
        state.Wall.RemoveAll(t => t / 4 == wan5);

        // ── Seat 0 ── kong setup
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].Melds.Clear();
        if (seat0Mode == Seat0Mode.PungPlusFourth)
        {
            state.Hands[0].Melds.Add(new Meld
            {
                Kind = MeldKind.Pung,
                TileIds = new List<int>
                {
                    Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 5, 1), Tid(Suit.Wan, 5, 2)
                },
                ClaimedFromSeatIndex = 3
            });
            state.Hands[0].ConcealedTiles.Add(Tid(Suit.Wan, 5, 3));
            // 10 filler tiles (concealed total = 11 → 14 with the 3-tile meld).
            state.Hands[0].ConcealedTiles.AddRange(new[]
            {
                Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 2, 1), Tid(Suit.Tong, 2, 2),
                Tid(Suit.Tong, 3, 0), Tid(Suit.Tong, 3, 1), Tid(Suit.Tong, 3, 2),
                Tid(Suit.Tong, 4, 0), Tid(Suit.Tong, 4, 1), Tid(Suit.Tong, 4, 2),
                Tid(Suit.Tong, 5, 0),
            });
        }
        else // AllFourInConcealed
        {
            state.Hands[0].ConcealedTiles.AddRange(new[]
            {
                Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 5, 1),
                Tid(Suit.Wan, 5, 2), Tid(Suit.Wan, 5, 3),
            });
            // 10 filler tiles to round out to 14 concealed.
            state.Hands[0].ConcealedTiles.AddRange(new[]
            {
                Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 2, 1), Tid(Suit.Tong, 2, 2),
                Tid(Suit.Tong, 3, 0), Tid(Suit.Tong, 3, 1), Tid(Suit.Tong, 3, 2),
                Tid(Suit.Tong, 4, 0), Tid(Suit.Tong, 4, 1), Tid(Suit.Tong, 4, 2),
                Tid(Suit.Tong, 5, 0),
            });
        }

        // ── Seat 1 ── empty (no opportunity scan interest)
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();

        // ── Seat 2 ── either a Wan-5 wait or empty
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        if (seat2CanHu)
        {
            // 13-tile hand: chow Wan-1-2-3 + Wan-4 + Wan-6 + chow Wan-7-8-9 +
            // pung Tiao-1 + pair Tiao-5 (258 pair). Completes on Wan-5.
            state.Hands[2].ConcealedTiles.AddRange(new[]
            {
                Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
                Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 6, 0),
                Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 8, 0), Tid(Suit.Wan, 9, 0),
                Tid(Suit.Tiao, 1, 0), Tid(Suit.Tiao, 1, 1), Tid(Suit.Tiao, 1, 2),
                Tid(Suit.Tiao, 5, 0), Tid(Suit.Tiao, 5, 1),
            });
        }

        // ── Seat 3 ── empty
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        // §3.6: clear any latent missed-win flags so the opportunity scan isn't filtered.
        state.MissedWinSeats.Clear();
        state.Phase = ChangshaPhase.AwaitingDiscard;

        return state;
    }

    // ── Reflection helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Read <c>WinResult.IsRobbedKong</c> via reflection so the test assembly compiles
    /// before Bishop's contract lands. Returns <c>null</c> when the property is
    /// missing — callers throw with a descriptive error.
    /// </summary>
    internal static bool? ResolveIsRobbedKong(WinResult result)
    {
        var prop = typeof(WinResult).GetProperty("IsRobbedKong",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return null;
        return (bool?)prop.GetValue(result);
    }

    /// <summary>
    /// Verify a claim window is the new kong-robbing variant. Reads
    /// <c>ChangshaClaimWindow.IsKongRobbing</c> via reflection so the assembly
    /// compiles before Bishop adds the property. Throws with a descriptive message
    /// when the property is missing or false.
    /// </summary>
    internal static void AssertIsKongRobbingWindow(ChangshaClaimWindow window, string failMessage)
    {
        var prop = typeof(ChangshaClaimWindow).GetProperty("IsKongRobbing",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null)
        {
            throw new InvalidOperationException(
                "ChangshaClaimWindow.IsKongRobbing property not found — Bishop owes the Phase H Wave 2 contract.");
        }
        var isKongRobbing = (bool?)prop.GetValue(window) ?? false;
        Assert.True(isKongRobbing, failMessage);
    }
}
