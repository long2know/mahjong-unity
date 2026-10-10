using System.Net.WebSockets;
using System.Text.Json;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Xunit.Abstractions;
using static Mahjong.Autotable.Api.Tests.TestInfrastructure.LobbyRepairFixture;

namespace Mahjong.Autotable.Api.Tests.Autotable;

public sealed class ViewerAuthorityContractTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "ViewerAuthorityC1")]
    public async Task JoinedFullIncrementalAndReleaseFramesCarryTheExactRecipientsGrant()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var observer = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var peer = room.Creator;
        var watcher = await host.ObserveAsync(observer, room);
        var joined = peer.Frames.First(frame => Type(frame) == "JOINED");
        var revision = ViewerAuthorityAssertions.Expect(joined, room.Alias, 0);
        Assert.Equal(owner.Id, joined.GetProperty("playerId").GetString());
        foreach (var frame in peer.Frames) revision = ViewerAuthorityAssertions.Expect(frame, room.Alias, 0, revision);
        foreach (var frame in watcher.Frames) ViewerAuthorityAssertions.Expect(frame, room.Alias, null);

        var marker = Guid.NewGuid().ToString("N");
        var start = watcher.Frames.Count;
        await peer.UpdateAsync(["mouse", marker, new { x = 1, y = 2, z = 3 }]);
        var incremental = await AfterAsync(watcher, start, frame => Entries(frame)
            .Any(entry => entry[0].GetString() == "mouse" && entry[1].GetString() == marker));
        Assert.False(incremental.GetProperty("full").GetBoolean());
        ViewerAuthorityAssertions.Expect(incremental, room.Alias, null);

        start = peer.Frames.Count;
        await peer.UpdateAsync(["seats", owner.Id, new { seat = (int?)null }]);
        var released = await AfterAsync(peer, start, frame => IsFull(frame)
            && frame.GetProperty("viewer").GetProperty("seat").ValueKind == JsonValueKind.Null);
        var releasedRevision = ViewerAuthorityAssertions.Expect(released, room.Alias, null, revision + 1);
        Assert.Null(host.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        start = peer.Frames.Count;
        await peer.UpdateAsync(["seats", owner.Id, new { seat = 2 }]);
        var taken = await AfterAsync(peer, start, frame => IsFull(frame)
            && frame.GetProperty("viewer").GetProperty("seat").ValueKind == JsonValueKind.Number);
        ViewerAuthorityAssertions.Expect(taken, room.Alias, 2, releasedRevision + 1);
        Assert.Equal(2, host.Runtime.TryGetSeatForPlayer(room.RuntimeId, owner.Id));
        foreach (var frame in watcher.Frames) ViewerAuthorityAssertions.Expect(frame, room.Alias, null);
        Assert.Equal(0, host.Manager.GetStoredEntryCount(room.Alias, "viewer"));
    }

    [Fact, Trait("Category", "ViewerAuthorityC1")]
    public async Task ForgedEnvelopeAndCollectionCannotGrantAuthorityOrAlterTheOwnedHand()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 3, dealMode: "auto");
        await UntilAsync(() => host.Runtime.TryGetSnapshot(room.RuntimeId, out var state)
            && state!.Hands[0].ConcealedTiles.Count == 14, "The real automatic deal must provide the owner hand.");
        var duplicate = await host.JoinAsync(owner, room);
        var initial = duplicate.Frames.First(frame => Type(frame) == "JOINED");
        var revision = ViewerAuthorityAssertions.Expect(initial, room.Alias, null);
        var before = await host.StateAsync(room);
        var tileId = before.Hands[0].ConcealedTiles[0];
        var marker = duplicate.Frames.Count;
        var forged = new { roomId = room.Alias, revision = 999_999, seat = 0 };
        await duplicate.SendAsync(new
        {
            type = "UPDATE",
            viewer = forged,
            entries = new object[][] {
                ["viewer", "current", forged],
                ["discard", 0, new { tileId }],
            },
        });
        var rejected = await AfterAsync(duplicate, marker, frame => Entries(frame)
            .Any(entry => entry[0].GetString() == "actionRejected"));
        var entry = Assert.Single(Entries(rejected), item => item[0].GetString() == "actionRejected");
        Assert.Equal("discard", entry[2].GetProperty("action").GetString());
        Assert.Equal("connection-owns-no-seat", entry[2].GetProperty("reason").GetString());
        Assert.Equal(0, entry[2].GetProperty("requestedSeat").GetInt32());
        Assert.Equal(JsonValueKind.Null, entry[2].GetProperty("ownedSeat").ValueKind);
        ViewerAuthorityAssertions.Expect(rejected, room.Alias, null, revision);
        var resync = await AfterAsync(duplicate, marker, IsFull);
        ViewerAuthorityAssertions.Expect(resync, room.Alias, null, revision);
        AssertOpaqueHands(resync, before);
        Assert.Contains(Entries(resync), item => item[0].GetString() == "seats"
            && item[1].GetString() == owner.Id && item[2].GetProperty("seat").GetInt32() == 0);
        Assert.Equal(0, host.Manager.GetStoredEntryCount(room.Alias, "viewer"));
        Assert.DoesNotContain(duplicate.Frames.SelectMany(Entries), item => item[0].GetString() == "viewer");
        Assert.DoesNotContain(room.Creator.Frames.SelectMany(Entries), item => item[0].GetString() == "viewer");
        var after = await host.StateAsync(room);
        Assert.Equal(before.StateVersion, after.StateVersion);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Contains(tileId, after.Hands[0].ConcealedTiles);
        var ownerFull = room.Creator.Frames.Last(IsFull);
        ViewerAuthorityAssertions.Expect(ownerFull, room.Alias, 0);
        AssertOwnedHand(ownerFull, before, 0);
    }

    [Fact, Trait("Category", "ViewerAuthorityC1")]
    public async Task RapidRoomSwitchAndRepeatedJoinNeverLabelOldPrivateProjectionAsNewAuthority()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var other = await host.PlayerAsync();
        var source = await host.CreateRoomAsync(owner, 3, dealMode: "auto");
        var target = await host.CreateRoomAsync(other, 3, dealMode: "auto");
        await UntilAsync(() => host.Runtime.TryGetSnapshot(source.RuntimeId, out var state)
            && state!.Hands[0].ConcealedTiles.Count == 14, "Source must have a real private hand.");
        await UntilAsync(() => host.Runtime.TryGetSnapshot(target.RuntimeId, out var state)
            && state!.Hands[0].ConcealedTiles.Count == 14, "Destination must have a real private hand.");
        var sourceState = await host.StateAsync(source);
        var targetState = await host.StateAsync(target);
        var peer = source.Creator;
        var oldRevision = peer.Frames.Last().GetProperty("viewer").GetProperty("revision").GetInt64();
        var mark = peer.Frames.Count;
        var destinations = new[] { target.Alias, source.Alias, source.Alias, target.Alias, source.Alias };
        foreach (var alias in destinations) await peer.SendAsync(new { type = "JOIN", gameId = alias });
        await UntilAsync(() => peer.Frames.Skip(mark).Count(frame => Type(frame) == "JOINED") == destinations.Length,
            "Every accepted JOIN must acknowledge a fresh authority revision.");
        await peer.UpdateAsync(["discard", 1, new { tileId = sourceState.Hands[1].ConcealedTiles[0] }]);
        var rejection = await AfterAsync(peer, mark, frame => Entries(frame)
            .Any(entry => entry[0].GetString() == "actionRejected"));
        var rejectedIndex = Array.FindIndex(peer.Frames.ToArray(), frame => frame.GetRawText() == rejection.GetRawText());
        await AfterAsync(peer, rejectedIndex + 1, IsFull);
        string? currentRoom = null;
        long revision = oldRevision;
        var joins = 0;
        foreach (var frame in peer.Frames.Skip(mark))
        {
            if (Type(frame) == "JOINED")
            {
                currentRoom = destinations[joins++];
                Assert.Equal(currentRoom, frame.GetProperty("gameId").GetString());
                revision = ViewerAuthorityAssertions.Expect(frame, currentRoom,
                    currentRoom == source.Alias ? 0 : null, revision + 1);
            }
            else if (currentRoom is not null)
            {
                ViewerAuthorityAssertions.Expect(frame, currentRoom, currentRoom == source.Alias ? 0 : null, revision);
                Assert.Equal(revision, frame.GetProperty("viewer").GetProperty("revision").GetInt64());
                if (!IsFull(frame)) continue;
                if (currentRoom == source.Alias) AssertOwnedHand(frame, sourceState, 0);
                else AssertOpaqueHands(frame, targetState);
            }
        }
        Assert.Equal(destinations.Length, joins);
        Assert.Equal(source.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(source.Alias));
        Assert.Equal(target.RuntimeId, host.Manager.GetRuntimeGameIdBoundTo(target.Alias));
    }

    [Fact, Trait("Category", "ViewerAuthorityC1")]
    public async Task SolePublicOwnerReleaseRevokesObserverAuthorityWithoutAReplacementRoom()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var watcherPlayer = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var watcher = await host.ObserveAsync(watcherPlayer, room);
        var hub = await host.HubAsync(owner);
        _ = await hub.InvokeAsync("SetGamePublic", room.Alias, true, "C1 terminal room");
        var previous = watcher.Frames.Last().GetProperty("viewer").GetProperty("revision").GetInt64();
        var mark = watcher.Frames.Count;
        await room.Creator.UpdateAsync(["seats", owner.Id, new { seat = (int?)null }]);
        await UntilAsync(() => host.Runtime.GameCount == 0, "The real explicit leave must remove the sole-owner room.");
        JsonElement terminal;
        try
        {
            terminal = await AfterAsync(watcher, mark, frame => frame.TryGetProperty("viewer", out var viewer)
                && viewer.GetProperty("roomId").ValueKind == JsonValueKind.Null);
        }
        catch (Xunit.Sdk.XunitException)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                room = room.Alias,
                runtime = room.RuntimeId,
                gameCount = host.Runtime.GameCount,
                runtimeStillExists = host.Runtime.TryGetSnapshot(room.RuntimeId, out _),
                connectionBinding = host.Manager.GetRuntimeGameIdBoundTo(room.Alias),
                observerCloseStatus = watcher.CloseStatus?.ToString(),
                connections = ViewerAuthorityAssertions.Connections(host.Manager).Select(connection => new
                {
                    connection.Id, connection.PlayerId, connection.HasJoined,
                    socket = connection.Socket.State.ToString(),
                    connection.ViewerSeat,
                    authority = connection.Authority,
                }),
                deliveredAfterRelease = watcher.Frames.Skip(mark).Select(frame => new
                {
                    type = Type(frame),
                    full = IsFull(frame),
                    viewer = frame.TryGetProperty("viewer", out var viewer) ? viewer : (JsonElement?)null,
                    kinds = Entries(frame).Select(entry => entry[0].GetString()).Distinct().ToArray(),
                }),
            }));
            throw;
        }
        ViewerAuthorityAssertions.Expect(terminal, null, null, previous + 1);
        await UntilAsync(() => host.Runtime.GameCount == 0, "R1 removal must retire the same room.");
        Assert.Null(host.Manager.GetRuntimeGameIdBoundTo(room.Alias));
        var frames = watcher.Frames.ToArray();
        var terminalIndex = Array.FindIndex(frames, frame => frame.GetRawText() == terminal.GetRawText());
        foreach (var frame in frames.Skip(terminalIndex))
            ViewerAuthorityAssertions.Expect(frame, null, null, previous + 1);
        Assert.Empty(await host.LobbyAsync(owner));
    }

    [Fact, Trait("Category", "ViewerAuthorityC1")]
    public async Task PreJoinPolicyRejectionCarriesExplicitNullAuthorityAndCreatesNothing()
    {
        await using var host = new LobbyRepairFixture();
        var player = await host.PlayerAsync();
        var alias = $"c1-missing-{Guid.NewGuid():N}";
        var peer = await host.SocketAsync(player, alias, "join=1");
        var rejected = await peer.WaitAsync(frame => Entries(frame).Any(entry => entry[0].GetString() == "actionRejected"));
        ViewerAuthorityAssertions.Expect(rejected, null, null);
        await UntilAsync(() => peer.CloseStatus.HasValue, "Missing join must policy-close.");
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, peer.CloseStatus);
        Assert.DoesNotContain(peer.Frames, frame => Type(frame) == "JOINED");
        Assert.Null(host.Manager.GetRuntimeGameIdBoundTo(alias));
        Assert.Equal(0, host.Runtime.GameCount);
    }

    [Theory, Trait("Category", "ViewerAuthorityC1")]
    [InlineData("four_player")]
    [InlineData("three_player")]
    [InlineData("bamboo")]
    [InlineData("minefield")]
    public async Task ExplicitRelayKeepsItsUnmodifiedEnvelopeAndSeatPassthrough(string variant)
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var bob = await host.PlayerAsync();
        var alias = $"c1-relay-{Guid.NewGuid():N}";
        var first = await host.SocketAsync(alice, alias, "seat=-1", variant: variant);
        await first.WaitAsync(IsFull);
        var second = await host.SocketAsync(bob, alias, "seat=-1", variant: variant);
        await second.WaitAsync(IsFull);
        var mark = second.Frames.Count;
        await first.UpdateAsync(["seats", alice.Id, new { seat = 2 }]);
        var confirmed = await AfterAsync(second, mark, frame => Entries(frame).Any(entry =>
            entry[0].GetString() == "seats" && entry[1].GetString() == alice.Id));
        Assert.Equal(2, Assert.Single(Entries(confirmed))[2].GetProperty("seat").GetInt32());
        Assert.All(first.Frames.Concat(second.Frames), frame => Assert.False(frame.TryGetProperty("viewer", out _)));
        Assert.Null(host.Manager.GetRuntimeGameIdBoundTo(alias));
    }

    private static async Task<JsonElement> AfterAsync(LobbyWsPeer peer, int mark, Func<JsonElement, bool> predicate)
    {
        await UntilAsync(() => peer.Frames.Skip(mark).Any(predicate), "Expected a new authoritative frame after the command.");
        return peer.Frames.Skip(mark).First(predicate);
    }

    private static IEnumerable<JsonElement> HandEntries(JsonElement frame, int seat) =>
        Entries(frame).Where(entry => entry[0].GetString() == "things" && entry[2].ValueKind == JsonValueKind.Object
            && entry[2].TryGetProperty("slotName", out var slot) && slot.GetString()!.StartsWith("hand.", StringComparison.Ordinal)
            && slot.GetString()!.EndsWith($"@{seat}", StringComparison.Ordinal));

    private static void AssertOpaqueHands(JsonElement frame, Mahjong.Autotable.Api.Changsha.ChangshaGameState state)
    {
        foreach (var hand in state.Hands)
        {
            var entries = HandEntries(frame, hand.SeatIndex).ToArray();
            Assert.Equal(hand.ConcealedTiles.Count, entries.Length);
            foreach (var entry in entries)
            {
                Assert.Equal(JsonValueKind.String, entry[1].ValueKind);
                Assert.StartsWith("h_", entry[1].GetString());
                Assert.Equal(2, entry[2].GetProperty("rotationIndex").GetInt32());
                Assert.True(!entry[2].TryGetProperty("face", out var face) || face.ValueKind == JsonValueKind.Null);
            }
        }
    }

    private static void AssertOwnedHand(JsonElement frame, Mahjong.Autotable.Api.Changsha.ChangshaGameState state, int seat)
    {
        var entries = HandEntries(frame, seat).ToArray();
        Assert.Equal(state.Hands[seat].ConcealedTiles.OrderBy(tile => tile),
            entries.Select(entry => entry[1].GetInt32()).OrderBy(tile => tile));
        Assert.All(entries, entry => Assert.Equal(1, entry[2].GetProperty("rotationIndex").GetInt32()));
    }
}
