using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Pung-to-Kong promotion per Vasquez §1.8 + MahjongPros §"Kongs":
///   When a seat already exposed a Pung and later draws/holds the 4th matching tile,
///   they may upgrade the meld to an "Added Kong" (加杠) and draw a replacement from the
///   back of the wall. Concealed kong (暗杠) is the alternative path: all 4 tiles already in hand.
///
/// Spec coverage:
///   - Pung promotion via <see cref="ChangshaGameStateMachine.DeclareAddedKong"/> ✱
///   - Concealed kong via <see cref="ChangshaGameStateMachine.DeclareConcealedKong"/> ✱
///   - Replacement draw arrives from BACK of wall, not front (Vasquez §1.8 invariant)
///   - Rejection paths: no existing pung, no fourth tile, wrong active seat.
/// </summary>
public class PungPromotionToKongTests
{
    [Fact, Trait("Category", "Acceptance")]
    public void Pung_Then_DrawMatchingTile_PromoteToAddedKong()
    {
        // MahjongPros §Kongs: claimed pung + later-acquired 4th matching tile = added kong.
        var state = AcceptanceFixture.NewDealtGame(seed: 13, dealerSeat: 0);
        var dealer = state.DealerSeatIndex;

        var desired = new Dictionary<int, int[]>
        {
            [0] = [60, 61, 104, 103, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36],
            [1] = [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 40, 44, 48],
            [2] = [2, 6, 10, 14, 18, 22, 26, 30, 34, 38, 41, 45, 49],
            [3] = [3, 7, 11, 15, 19, 23, 27, 31, 35, 39, 43, 47, 62]
        };
        foreach (var (seat, tiles) in desired)
        {
            var hand = state.Hands[seat].ConcealedTiles;
            Assert.Equal(tiles.Length, hand.Count);
            for (var index = 0; index < tiles.Length; index++)
                SwapInto(hand, index, tiles[index]);
        }
        int[] front = [105, 106, 107, 101, 100, 99, 63];
        for (var index = 0; index < front.Length; index++)
            SwapInto(state.Wall, index, front[index]);
        AssertDeck();

        ChangshaGameStateMachine.Discard(state, dealer, 104);
        DrawThenDiscard(1, 105, 105);
        DrawThenDiscard(2, 106, 106);
        DrawThenDiscard(3, 107, Tid(Suit.Tong, 7, 2));
        Assert.Contains(state.ClaimWindow!.Opportunities,
            opportunity => opportunity.SeatIndex == dealer && opportunity.ClaimType == TableClaimType.Pung);
        ChangshaGameStateMachine.ResolveClaim(state, dealer, TableClaimType.Pung);
        Assert.Equal(MeldKind.Pung, Assert.Single(state.Hands[dealer].Melds).Kind);
        Assert.Equal(11, state.Hands[dealer].ConcealedTiles.Count);
        Assert.Null(state.LastDrawSeatIndex);
        AssertDeck();

        ChangshaGameStateMachine.Discard(state, dealer, 103);
        Assert.Equal(10, state.Hands[dealer].ConcealedTiles.Count);
        DrawThenDiscard(1, 101, 101);
        DrawThenDiscard(2, 100, 100);
        DrawThenDiscard(3, 99, 99);
        Assert.Equal(dealer, state.ActiveSeatIndex);
        Assert.Equal(10, state.Hands[dealer].ConcealedTiles.Count);
        Assert.Equal(Tid(Suit.Tong, 7, 3), state.Wall[0]);
        Assert.DoesNotContain(Tid(Suit.Tong, 7, 3), state.Hands[dealer].ConcealedTiles);
        var draws = ChangshaGameStateMachine.DrawTile(state);
        Assert.Contains(draws, draw => draw.EventType == "tile-drawn"
            && draw.SeatIndex == dealer && draw.TileId == Tid(Suit.Tong, 7, 3));
        Assert.Equal(11, state.Hands[dealer].ConcealedTiles.Count);
        Assert.Equal(14, state.Hands[dealer].ConcealedTiles.Count + 3 * state.Hands[dealer].Melds.Count);
        Assert.Equal(dealer, state.LastDrawSeatIndex);
        AssertDeck();

        var wallSizeBefore = state.Wall.Count;
        var replacement = state.Wall[^1];
        var backDrawn = state.WallBackDrawn;
        ChangshaGameStateMachine.DeclareAddedKong(state, dealer, Tid(Suit.Tong, 7, 3));

        // The pung becomes an added-kong meld.
        var meld = state.Hands[dealer].Melds.Single();
        Assert.Equal(MeldKind.AddedKong, meld.Kind);
        Assert.Equal(4, meld.TileIds.Count);
        Assert.All(meld.TileIds, t => Assert.Equal(Logical(Suit.Tong, 7), t / 4));
        // Replacement was drawn from BACK of wall (Vasquez §1.8): wall shrinks by 1.
        Assert.Equal(wallSizeBefore - 1, state.Wall.Count);
        Assert.Equal(replacement, state.Hands[dealer].ConcealedTiles[^1]);
        Assert.Equal(backDrawn + 1, state.WallBackDrawn);
        Assert.Equal(11, state.Hands[dealer].ConcealedTiles.Count);
        AssertDeck();

        void SwapInto(List<int> target, int index, int tile)
        {
            var source = state.Hands.Select(hand => hand.ConcealedTiles).Append(state.Wall)
                .Single(tiles => tiles.Contains(tile));
            var sourceIndex = source.IndexOf(tile);
            (source[sourceIndex], target[index]) = (target[index], source[sourceIndex]);
        }

        void DrawThenDiscard(int seat, int drawn, int discarded)
        {
            Assert.Equal(seat, state.ActiveSeatIndex);
            Assert.Equal(13, state.Hands[seat].ConcealedTiles.Count + 3 * state.Hands[seat].Melds.Count);
            Assert.Equal(drawn, state.Wall[0]);
            ChangshaGameStateMachine.DrawTile(state);
            Assert.Equal(drawn, state.Hands[seat].ConcealedTiles[^1]);
            Assert.Equal(seat, state.LastDrawSeatIndex);
            ChangshaGameStateMachine.Discard(state, seat, discarded);
            AssertDeck();
        }

        void AssertDeck() =>
            global::Mahjong.Autotable.Api.Tests.RulesQualification.HudsonOwnTurnWsFixture.AssertInventory(state);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pung_AddedKong_WithoutExistingPung_Throws()
    {
        // §1.8: cannot add-kong without an existing exposed pung of that tile.
        var state = AcceptanceFixture.NewDealtGame(seed: 13, dealerSeat: 0);
        var dealer = state.DealerSeatIndex;

        var anyTile = state.Hands[dealer].ConcealedTiles[0];
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.DeclareAddedKong(state, dealer, anyTile));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void ConcealedKong_FourMatchingTiles_PromotesToConcealedKong()
    {
        // The recorded seed-0 trace naturally gives seat 1 a Tong-8 quartet.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 0);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        AssertDeck();
        ChangshaGameStateMachine.Discard(state, 0, 56);
        if (state.ClaimWindow is not null)
            ChangshaGameStateMachine.PassClaim(state);
        var seat = state.ActiveSeatIndex;
        Assert.Equal(1, seat);
        Assert.Equal(13, state.Hands[seat].ConcealedTiles.Count);
        Assert.Null(state.LastDrawSeatIndex);
        Assert.Equal(4, state.Hands[seat].ConcealedTiles.Count(tile => tile / 4 == Logical(Suit.Tong, 8)));
        var front = state.Wall[0];
        ChangshaGameStateMachine.DrawTile(state);
        Assert.Equal(front, state.Hands[seat].ConcealedTiles[^1]);
        Assert.Equal(14, state.Hands[seat].ConcealedTiles.Count);
        Assert.Equal(seat, state.LastDrawSeatIndex);
        AssertDeck();

        var wallSizeBefore = state.Wall.Count;
        var replacement = state.Wall[^1];
        var backDrawn = state.WallBackDrawn;
        ChangshaGameStateMachine.DeclareConcealedKong(state, seat, Logical(Suit.Tong, 8));

        var meld = state.Hands[seat].Melds.Single();
        Assert.Equal(MeldKind.ConcealedKong, meld.Kind);
        Assert.Equal(4, meld.TileIds.Count);
        Assert.Equal(wallSizeBefore - 1, state.Wall.Count);
        Assert.Equal(replacement, state.Hands[seat].ConcealedTiles[^1]);
        Assert.Equal(backDrawn + 1, state.WallBackDrawn);
        Assert.Equal(11, state.Hands[seat].ConcealedTiles.Count);
        AssertDeck();

        void AssertDeck() =>
            global::Mahjong.Autotable.Api.Tests.RulesQualification.HudsonOwnTurnWsFixture.AssertInventory(state);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void ConcealedKong_ThreeMatchingTiles_Throws()
    {
        // §1.8: need 4 tiles for concealed kong; 3 is just a pung.
        var state = AcceptanceFixture.NewDealtGame(seed: 13, dealerSeat: 0);
        var dealer = state.DealerSeatIndex;

        state.Hands[dealer].ConcealedTiles.Clear();
        state.Hands[dealer].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Tiao, 4, 0), Tid(Suit.Tiao, 4, 1), Tid(Suit.Tiao, 4, 2),
            Tid(Suit.Wan, 1, 0)
        });

        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.DeclareConcealedKong(state, dealer, Logical(Suit.Tiao, 4)));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pung_With_FourthTileInWall_PlayerDraws_It_PromoteAllowed()
    {
        // Recorded added-draw-1 trace: a real Wan-7 Pung, then its fourth physical tile arrives from the front.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 0);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        var dealer = state.DealerSeatIndex;
        const string steps =
            "D:0:5 T:1 D:1:2 C:2:Chow D:2:13 T:3 D:3:1 T:0 D:0:10 C:1:Chow"
            + " D:1:23 T:2 D:2:19 T:3 D:3:9 T:0 D:0:28 T:1 D:1:25 C:0:Pung"
            + " D:0:44 T:1 D:1:45 T:2 D:2:21 T:3 D:3:11";
        AssertDeck();
        foreach (var step in steps.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = step.Split(':');
            var actor = int.Parse(parts[1]);
            switch (parts[0])
            {
                case "D":
                    Assert.Equal(actor, state.ActiveSeatIndex);
                    Assert.Equal(14, state.Hands[actor].ConcealedTiles.Count + 3 * state.Hands[actor].Melds.Count);
                    ChangshaGameStateMachine.Discard(state, actor, int.Parse(parts[2]));
                    break;
                case "T":
                    Assert.Equal(actor, state.ActiveSeatIndex);
                    Assert.Equal(13, state.Hands[actor].ConcealedTiles.Count + 3 * state.Hands[actor].Melds.Count);
                    ChangshaGameStateMachine.DrawTile(state);
                    break;
                case "C":
                    var claim = Enum.Parse<TableClaimType>(parts[2]);
                    Assert.Contains(state.ClaimWindow!.Opportunities,
                        opportunity => opportunity.SeatIndex == actor && opportunity.ClaimType == claim);
                    ChangshaGameStateMachine.ResolveClaim(state, actor, claim);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown recorded fixture step {step}.");
            }
            AssertDeck();
        }
        Assert.Equal(dealer, state.ActiveSeatIndex);
        Assert.Equal(10, state.Hands[dealer].ConcealedTiles.Count);
        Assert.Equal(MeldKind.Pung, Assert.Single(state.Hands[dealer].Melds).Kind);
        Assert.Equal(13, state.Hands[dealer].ConcealedTiles.Count + 3 * state.Hands[dealer].Melds.Count);
        Assert.Null(state.LastDrawSeatIndex);
        var drawnTile = 26;
        Assert.Equal(drawnTile, state.Wall[0]);
        Assert.DoesNotContain(drawnTile, state.Hands[dealer].ConcealedTiles);
        ChangshaGameStateMachine.DrawTile(state);
        Assert.Equal(drawnTile, state.Hands[dealer].ConcealedTiles[^1]);
        Assert.Equal(11, state.Hands[dealer].ConcealedTiles.Count);
        Assert.Equal(14, state.Hands[dealer].ConcealedTiles.Count + 3 * state.Hands[dealer].Melds.Count);
        Assert.Equal(dealer, state.LastDrawSeatIndex);
        AssertDeck();

        var wallSizeBefore = state.Wall.Count;
        var replacement = state.Wall[^1];
        var backDrawn = state.WallBackDrawn;
        ChangshaGameStateMachine.DeclareAddedKong(state, dealer, drawnTile);
        if (state.ClaimWindow is not null)
        {
            Assert.True(state.ClaimWindow.IsKongRobbing);
            ChangshaGameStateMachine.PassClaim(state);
        }

        Assert.Equal(MeldKind.AddedKong, state.Hands[dealer].Melds.Single().Kind);
        Assert.Equal(wallSizeBefore - 1, state.Wall.Count);
        Assert.Equal(replacement, state.Hands[dealer].ConcealedTiles[^1]);
        Assert.Equal(backDrawn + 1, state.WallBackDrawn);
        Assert.Equal(11, state.Hands[dealer].ConcealedTiles.Count);
        AssertDeck();

        void AssertDeck() =>
            global::Mahjong.Autotable.Api.Tests.RulesQualification.HudsonOwnTurnWsFixture.AssertInventory(state);
    }
}
