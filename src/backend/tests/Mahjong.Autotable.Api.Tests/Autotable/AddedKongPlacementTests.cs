using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using Mahjong.Autotable.Api.Tests.RulesQualification;

namespace Mahjong.Autotable.Api.Tests.Autotable;

public sealed class AddedKongPlacementTests
{
    private static readonly byte[] PublicTestSecret =
        Encoding.UTF8.GetBytes("public-layout-test-material-not-a-deployed-key");

    public static IEnumerable<object[]> LocationsAndCopies()
    {
        for (var seat = 0; seat < 4; seat++)
        for (var meld = 0; meld < 4; meld++)
        for (var copy = 0; copy < 4; copy++)
            yield return [seat, meld, copy];
    }

    public static IEnumerable<object[]> OwnersAndCopies()
    {
        for (var seat = 0; seat < 4; seat++)
        for (var copy = 0; copy < 4; copy++)
            yield return [seat, copy];
    }

    [Theory]
    [MemberData(nameof(LocationsAndCopies))]
    public void RealDrawAndPromotion_PreservePungIds_StackExactAddedCopy_InEveryFullViewerSnapshot(
        int seat, int meldIndex, int copy)
    {
        var (state, added) = ReadyCandidate(seat, meldIndex, copy, robbing: false);
        var pung = state.Hands[seat].Melds[meldIndex];
        var originalIds = pung.TileIds.ToArray();
        var before = PublicMeld(state, seat, meldIndex, seat);
        Assert.Equal(MeldKind.Pung, pung.Kind);
        Assert.Contains(added, ChangshaGameStateMachine.GetAddedKongCandidates(state, seat));
        var replacement = state.Wall[^1];

        var events = ChangshaGameStateMachine.DeclareAddedKong(state, seat, added);
        Assert.Null(state.ClaimWindow);
        Assert.Contains(events, e => e.EventType == "added-kong" && e.TileId == added);
        Assert.Equal(MeldKind.AddedKong, state.Hands[seat].Melds[meldIndex].Kind);
        Assert.Equal(replacement, state.Hands[seat].ConcealedTiles[^1]);
        HudsonOwnTurnWsFixture.AssertInventory(state);

        foreach (int? viewer in new int?[] { null, 0, 1, 2, 3 })
        {
            var after = PublicMeld(state, seat, meldIndex, viewer);
            Assert.Equal(4, after.Count);
            Assert.Equal($"meld.{meldIndex}.4@{seat}", after[added].SlotName);
            Assert.Equal(0, after[added].RotationIndex);
            foreach (var id in originalIds)
                Assert.Equal(before[id].SlotName, after[id].SlotName);
            Assert.DoesNotContain(after.Values, info => info.SlotName == $"meld.{meldIndex}.3@{seat}");
            Assert.Equal(4, after.Values.Select(info => info.SlotName).Distinct().Count());
            Assert.Equal(after.Select(pair => (pair.Key, pair.Value.SlotName)).OrderBy(pair => pair.Key),
                PublicMeld(RoundTrip(state), seat, meldIndex, viewer)
                    .Select(pair => (pair.Key, pair.Value.SlotName)).OrderBy(pair => pair.Key));
        }
    }

    [Theory]
    [MemberData(nameof(OwnersAndCopies))]
    public void PendingAndRobbedKong_NeverPublishCommittedUpperTile(int seat, int copy)
    {
        var (state, added) = ReadyCandidate(seat, 0, copy, robbing: true);
        var before = PublicMeld(state, seat, 0, null);
        ChangshaGameStateMachine.DeclareAddedKong(state, seat, added);
        Assert.True(state.ClaimWindow!.IsKongRobbing);
        Assert.Equal(MeldKind.Pung, Assert.Single(state.Hands[seat].Melds).Kind);
        foreach (int? viewer in new int?[] { null, 0, 1, 2, 3 })
        {
            var pending = PublicMeld(state, seat, 0, viewer);
            Assert.Equal(3, pending.Count);
            Assert.DoesNotContain(pending.Values, info => info.SlotName.EndsWith($".4@{seat}"));
            Assert.DoesNotContain(added, pending.Keys);
        }
        var robber = (seat + 1) % 4;
        Assert.Contains(state.ClaimWindow.Opportunities,
            opportunity => opportunity.SeatIndex == robber && opportunity.ClaimType == TableClaimType.Hu);
        ChangshaGameStateMachine.ResolveClaim(state, robber, TableClaimType.Hu);
        Assert.True(state.CurrentWin!.IsRobbedKong);
        HudsonOwnTurnWsFixture.AssertInventory(state);
        var after = PublicMeld(RoundTrip(state), seat, 0, null);
        Assert.Equal(before.Select(p => (p.Key, p.Value.SlotName)).OrderBy(p => p.Key),
            after.Select(p => (p.Key, p.Value.SlotName)).OrderBy(p => p.Key));
    }

    [Theory]
    [MemberData(nameof(OwnersAndCopies))]
    public void AllPass_CommitsUpperTileOnlyAfterTheRealRobbingWindow(int seat, int copy)
    {
        var (state, added) = ReadyCandidate(seat, 0, copy, robbing: true);
        ChangshaGameStateMachine.DeclareAddedKong(state, seat, added);
        Assert.True(state.ClaimWindow!.IsKongRobbing);
        Assert.Equal(3, PublicMeld(state, seat, 0, null).Count);
        ChangshaGameStateMachine.PassClaim(state);
        Assert.Null(state.ClaimWindow);
        Assert.Equal(MeldKind.AddedKong, Assert.Single(state.Hands[seat].Melds).Kind);
        Assert.Equal($"meld.0.4@{seat}", PublicMeld(RoundTrip(state), seat, 0, null)[added].SlotName);
        HudsonOwnTurnWsFixture.AssertInventory(state);
    }

    [Fact]
    public void NormalKongKindsKeepFlatSlotsAndConcealedPrivacy()
    {
        var (state, added) = ReadyCandidate(0, 0, 0, robbing: false);
        ChangshaGameStateMachine.DeclareAddedKong(state, 0, added);
        foreach (var kind in new[] { MeldKind.ExposedKong, MeldKind.ConcealedKong })
        {
            var otherKind = RoundTrip(state);
            var meld = otherKind.Hands[0].Melds[0];
            otherKind.Hands[0].Melds[0] = new Meld
            {
                Kind = kind, TileIds = meld.TileIds, ClaimedFromSeatIndex = meld.ClaimedFromSeatIndex
            };
            foreach (int? viewer in new int?[] { null, 0, 1, 2, 3 })
            {
                var entries = Snapshot(otherKind, viewer).Where(entry =>
                    entry.Value is ThingInfo info && info.SlotName.StartsWith("meld.0.", StringComparison.Ordinal)
                        && info.SlotName.EndsWith("@0", StringComparison.Ordinal)).ToList();
                Assert.Equal(4, entries.Count);
                Assert.Equal(Enumerable.Range(0, 4).Select(i => $"meld.0.{i}@0"),
                    entries.Select(entry => ((ThingInfo)entry.Value!).SlotName).Order());
                foreach (var entry in entries)
                {
                    var info = (ThingInfo)entry.Value!;
                    Assert.Equal(kind == MeldKind.ConcealedKong ? 2 : 0, info.RotationIndex);
                    if (kind == MeldKind.ConcealedKong && viewer != 0)
                        Assert.IsType<string>(entry.Key);
                    else
                        Assert.IsType<int>(entry.Key);
                }
            }
        }
    }

    [Fact]
    public void LegacyHistorylessSnapshotsUseStableCopy_AndPriorHandEventsAreIgnored()
    {
        var (state, added) = ReadyCandidate(0, 0, 0, robbing: false);
        ChangshaGameStateMachine.DeclareAddedKong(state, 0, added);
        state.EventLog.Clear();
        var legacy = PublicMeld(RoundTrip(state), 0, 0, null);
        Assert.Equal("meld.0.4@0", legacy[19].SlotName);
        state.EventLog.Add(new ChangshaEvent { EventType = "added-kong", SeatIndex = 0, TileId = 16 });
        state.EventLog.Add(new ChangshaEvent { EventType = "tiles-dealt" });
        Assert.Equal("meld.0.4@0", PublicMeld(RoundTrip(state), 0, 0, null)[19].SlotName);
        Assert.Equal(4, legacy.Keys.Distinct().Count());
    }

    [Fact]
    public void InvalidAddedMeldCannotSilentlyDropOrDuplicateItsFourthTile()
    {
        var (state, _) = ReadyCandidate(0, 0, 3, robbing: false);
        foreach (var ids in new List<int>[] { [16, 17, 18], [16, 17, 18, 18], [16, 17, 18, 19, 20] })
        {
            state.Hands[0].Melds[0] = new Meld { Kind = MeldKind.AddedKong, TileIds = ids };
            Assert.Throws<InvalidOperationException>(() => Snapshot(state, null));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => AutotableSlotMap.MeldSlot(0, 0, 4));
    }

    private static IReadOnlyList<CollectionEntry> Snapshot(ChangshaGameState state, int? viewer)
    {
        var player = $"public-layout-viewer-{viewer?.ToString() ?? "spectator"}";
        var entries = ChangshaToAutotableTranslator.Translate(state, viewer, player,
            privacy: ChangshaPrivacyProjector.Create(PublicTestSecret, state.GameId, player));
        var things = entries.Where(entry => entry.Kind == "things").ToList();
        Assert.Equal(108, things.Count);
        Assert.Equal(108, things.Select(entry => entry.Key).Distinct().Count());
        foreach (var entry in things)
        {
            var info = Assert.IsType<ThingInfo>(entry.Value);
            Assert.Null(info.ClaimedBy);
            Assert.Null(info.ShiftSlotName);
            if (info.SlotName.StartsWith("wall.", StringComparison.Ordinal)
                || (info.SlotName.StartsWith("hand.", StringComparison.Ordinal)
                    && !info.SlotName.EndsWith($"@{viewer}", StringComparison.Ordinal)))
                Assert.IsType<string>(entry.Key);
        }
        return things;
    }

    private static Dictionary<int, ThingInfo> PublicMeld(ChangshaGameState state, int seat, int index, int? viewer) =>
        Snapshot(state, viewer).Where(entry => entry.Value is ThingInfo info
            && info.SlotName.StartsWith($"meld.{index}.", StringComparison.Ordinal)
            && info.SlotName.EndsWith($"@{seat}", StringComparison.Ordinal))
            .ToDictionary(entry => Assert.IsType<int>(entry.Key), entry => (ThingInfo)entry.Value!);

    private static ChangshaGameState RoundTrip(ChangshaGameState state) =>
        JsonSerializer.Deserialize<ChangshaGameState>(
            JsonSerializer.Serialize(state, AutotableJson.Options), AutotableJson.Options)!;

    private static (ChangshaGameState State, int Added) ReadyCandidate(int owner, int meldIndex, int copy, bool robbing)
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 17);
        foreach (var hand in state.Hands) { hand.ConcealedTiles.Clear(); hand.Melds.Clear(); }
        int[][] padding = [[0, 4, 8], [24, 28, 32], [72, 76, 80]];
        for (var index = 0; index < meldIndex; index++)
            state.Hands[owner].Melds.Add(new Meld
            {
                Kind = MeldKind.Chow, TileIds = padding[index].ToList(), ClaimedFromSeatIndex = (owner + 3) % 4
            });
        var added = 16 + copy;
        state.Hands[owner].Melds.Add(new Meld
        {
            Kind = MeldKind.Pung, TileIds = Enumerable.Range(16, 4).Where(id => id != added).ToList(),
            ClaimedFromSeatIndex = (owner + 2) % 4
        });
        int[] filler = [40, 41, 42, 44, 45, 46, 48, 49, 50, 52];
        state.Hands[owner].ConcealedTiles.AddRange(filler.Take(10 - 3 * meldIndex));
        var used = state.Hands[owner].ConcealedTiles
            .Concat(state.Hands[owner].Melds.SelectMany(meld => meld.TileIds)).Append(added).Append(107).ToHashSet();
        if (robbing)
        {
            int[] waiting = [0, 1, 2, 24, 25, 26, 60, 61, 62, 88, 89, 8, 12];
            foreach (var tile in waiting) Assert.True(used.Add(tile));
            state.Hands[(owner + 1) % 4].ConcealedTiles.AddRange(waiting);
        }
        int[] kinds = robbing
            ? [1, 2, 3, 5, 7, 8, 9, 13, 14, 16, 17, 18, 19]
            : [0, 1, 2, 3, 5, 6, 7, 8, 9, 14, 15, 16, 17];
        foreach (var hand in state.Hands.Where(hand => hand.SeatIndex != owner && hand.ConcealedTiles.Count == 0))
        {
            foreach (var kind in kinds)
            {
                var tile = Enumerable.Range(kind * 4, 4).First(id => !used.Contains(id));
                Assert.True(used.Add(tile));
                hand.ConcealedTiles.Add(tile);
            }
        }
        var previous = (owner + 3) % 4;
        state.Hands[previous].ConcealedTiles.Add(107);
        state.Wall = new[] { added }.Concat(Enumerable.Range(0, 108).Where(id => !used.Contains(id))).ToList();
        state.WallDrawIndex = state.WallBackDrawn = 0;
        state.WallBackIndex = state.Wall.Count - 1;
        state.DiscardPile.Clear();
        state.ClaimWindow = null;
        state.CurrentWin = null;
        state.MissedWinSeats.Clear();
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.ActiveSeatIndex = previous;
        HudsonOwnTurnWsFixture.AssertInventory(state);
        Assert.Empty(new ClaimAdjudicator().GetOpportunities(previous, 107, state.Hands));
        ChangshaGameStateMachine.Discard(state, previous, 107);
        Assert.Equal(owner, state.ActiveSeatIndex);
        ChangshaGameStateMachine.DrawTile(state);
        Assert.Equal(added, state.Hands[owner].ConcealedTiles[^1]);
        Assert.Equal(owner, state.LastDrawSeatIndex);
        Assert.Contains(added, ChangshaGameStateMachine.GetAddedKongCandidates(state, owner));
        HudsonOwnTurnWsFixture.AssertInventory(state);
        return (state, added);
    }
}
