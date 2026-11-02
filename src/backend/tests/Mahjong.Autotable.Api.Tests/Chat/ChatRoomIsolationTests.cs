using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Mahjong.Autotable.Api.Tests.TestInfrastructure.LobbyRepairFixture;

namespace Mahjong.Autotable.Api.Tests.Chat;

public sealed class ChatRoomIsolationTests
{
    [Fact, Trait("Category", "LobbyRepair")]
    public async Task ActualSendAndBackfillUseCanonicalDtoAndRuntimeStorage_ForSignedGuests()
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var bob = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(alice, 0);
        _ = await host.JoinAsync(bob, room);
        var sent = await ChatAsync(alice, room.Alias, "A real room message");
        AssertMessage(sent, room, alice, "table", null, "A real room message");
        var row = Assert.Single(await host.StoredChatAsync());
        Assert.Equal(room.RuntimeId, row.GameId);
        Assert.Equal(Guid.Parse(sent.GetProperty("id").GetString()!), row.Id);
        var fromAlias = Assert.Single(await HistoryAsync(bob, room.Alias));
        var fromRuntime = Assert.Single(await HistoryAsync(bob, room.RuntimeId));
        AssertMessage(fromAlias, room, alice, "table", null, "A real room message");
        AssertMessage(fromRuntime, room, alice, "table", null, "A real room message");
        Assert.Equal(sent.GetProperty("id").GetString(), fromAlias.GetProperty("id").GetString());
        Assert.Equal(sent.GetProperty("id").GetString(), fromRuntime.GetProperty("id").GetString());
        using var legacy = await bob.Http.PostAsJsonAsync("/api/chat/send",
            new { gameId = room.Alias, channel = "table", body = "Legacy adapter message" });
        var adapted = await JsonAsync(legacy);
        AssertMessage(adapted, room, bob, "table", null, "Legacy adapter message");
        var freshReader = host.ReopenHttp(alice);
        Assert.Equal(2, (await HistoryAsync(freshReader, room.Alias)).Length);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task ThirdPartyPrivateHistoryIsFilteredBeforeLimitAndSince_ForBothPostRoutes()
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var bob = await host.PlayerAsync();
        var carol = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(alice, 0);
        _ = await host.JoinAsync(bob, room);
        _ = await host.JoinAsync(carol, room);
        var visible = await ChatAsync(alice, room.Alias, "Visible before private traffic");
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var secret = await ChatAsync(alice, room.Alias, "Only Alice and Bob", "private", bob.Id);
        AssertMessage(secret, room, alice, "private", bob.Id, "Only Alice and Bob");
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        using var legacy = await alice.Http.PostAsJsonAsync("/api/chat/send",
            new { gameId = room.Alias, channel = "private:" + bob.Id, body = "Legacy private traffic" });
        var legacyPrivate = await JsonAsync(legacy);
        AssertMessage(legacyPrivate, room, alice, "private", bob.Id, "Legacy private traffic");
        var carolPage = Assert.Single(await HistoryAsync(carol, room.Alias, "?limit=1"));
        Assert.Equal(visible.GetProperty("id").GetString(), carolPage.GetProperty("id").GetString());
        foreach (var party in new[] { alice, bob })
        {
            var history = await HistoryAsync(party, room.Alias);
            Assert.Contains(history, message => message.GetProperty("id").GetString() == secret.GetProperty("id").GetString());
            Assert.Contains(history, message => message.GetProperty("id").GetString() == legacyPrivate.GetProperty("id").GetString());
        }
        var since = Uri.EscapeDataString(visible.GetProperty("sentUtc").GetString()!);
        Assert.Empty(await HistoryAsync(carol, room.Alias, "?limit=1&since=" + since));
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task SpectatorAndPrivateMembershipAreEnforcedWithoutAccountLogin_AndAcrossLegacyAdapter()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var viewer = await host.PlayerAsync();
        var secondViewer = await host.PlayerAsync();
        var outsider = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        _ = await host.ObserveAsync(viewer, room);
        _ = await host.ObserveAsync(secondViewer, room);
        _ = await host.CreateRoomAsync(outsider, 0);
        var spectator = await ChatAsync(viewer, room.Alias, "Spectators only", "spectators");
        AssertMessage(spectator, room, viewer, "spectators", null, "Spectators only");
        Assert.Single(await HistoryAsync(secondViewer, room.Alias));
        Assert.Empty(await HistoryAsync(owner, room.Alias));
        using var oldSpectator = await viewer.Http.PostAsJsonAsync("/api/chat/send",
            new { gameId = room.Alias, channel = "spectator", body = "Legacy spectator channel" });
        Assert.Equal("spectators", (await JsonAsync(oldSpectator)).GetProperty("channel").GetString());
        using var seatedSpectator = await owner.Http.PostAsJsonAsync($"/api/games/{room.Alias}/chat",
            new { channel = "spectators", body = "A seated player must not enter this channel" });
        Assert.Equal(HttpStatusCode.Forbidden, seatedSpectator.StatusCode);
        using var outsiderRead = await outsider.Http.GetAsync($"/api/games/{room.Alias}/chat");
        Assert.Equal(HttpStatusCode.Forbidden, outsiderRead.StatusCode);
        using var outsiderSend = await outsider.Http.PostAsJsonAsync("/api/chat/send",
            new { gameId = room.Alias, channel = "table", body = "Cross-room injection" });
        Assert.Equal(HttpStatusCode.Forbidden, outsiderSend.StatusCode);
        using var privateOutsider = await owner.Http.PostAsJsonAsync($"/api/games/{room.Alias}/chat",
            new { channel = "private", recipientPlayerId = outsider.Id, body = "Recipient is not a room member" });
        Assert.Equal(HttpStatusCode.Forbidden, privateOutsider.StatusCode);
        using var anonymous = await host.Http().PostAsJsonAsync($"/api/games/{room.Alias}/chat",
            new { channel = "table", body = "Unverified" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(2, (await host.StoredChatAsync()).Length);
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task ActualRoomSwitchRevokesOldMembership_AndLegacyAliasRowsAreReadWithoutRewriting()
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var bob = await host.PlayerAsync();
        var first = await host.CreateRoomAsync(alice, 0);
        var second = await host.CreateRoomAsync(bob, 0);
        var oldId = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatMessages.Add(new ChatMessage
            {
                Id = oldId, GameId = first.Alias, PlayerId = alice.Id, Channel = "table",
                Body = "Persisted by the pre-fix alias writer", At = DateTime.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }
        _ = await ChatAsync(alice, first.Alias, "Written under the runtime ID");
        var history = await HistoryAsync(alice, first.Alias);
        Assert.Equal(2, history.Length);
        Assert.Equal(2, history.Select(message => message.GetProperty("id").GetString()).Distinct().Count());
        Assert.All(history, message => Assert.Equal(first.Alias, message.GetProperty("gameId").GetString()));
        Assert.Equal(first.Alias, Assert.Single(await host.StoredChatAsync(), row => row.Id == oldId).GameId);
        await first.Creator.SendAsync(new { type = "JOIN", gameId = second.Alias });
        await first.Creator.WaitAsync(frame => Type(frame) == "JOINED"
            && frame.GetProperty("gameId").GetString() == second.Alias);
        using var staleHistory = await alice.Http.GetAsync($"/api/games/{first.Alias}/chat");
        Assert.Equal(HttpStatusCode.Forbidden, staleHistory.StatusCode);
        using var staleSend = await alice.Http.PostAsJsonAsync("/api/chat/send",
            new { gameId = first.Alias, channel = "table", body = "Old room is no longer joined" });
        Assert.Equal(HttpStatusCode.Forbidden, staleSend.StatusCode);
        var newRoomMessage = await ChatAsync(bob, second.Alias, "Different room, independent history");
        Assert.Equal(newRoomMessage.GetProperty("id").GetString(),
            Assert.Single(await HistoryAsync(alice, second.Alias)).GetProperty("id").GetString());
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task PreFixAliasPrivateRowsAreAccessFilteredBeforePaginationAndAreNeverRewritten()
    {
        await using var host = new LobbyRepairFixture();
        var alice = await host.PlayerAsync();
        var bob = await host.PlayerAsync();
        var carol = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(alice, 0);
        _ = await host.JoinAsync(bob, room);
        _ = await host.JoinAsync(carol, room);
        var beginning = DateTime.UtcNow.AddHours(-1);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatMessages.AddRange(
                new ChatMessage { GameId = room.Alias, PlayerId = alice.Id, Channel = "table", Body = "Old visible row", At = beginning },
                new ChatMessage { GameId = room.Alias, PlayerId = alice.Id, Channel = "private:" + bob.Id, Body = "Old private row one", At = beginning.AddSeconds(1) },
                new ChatMessage { GameId = room.Alias, PlayerId = bob.Id, Channel = "private:" + alice.Id, Body = "Old private row two", At = beginning.AddSeconds(2) });
            await db.SaveChangesAsync();
        }
        Assert.Equal("Old visible row", Assert.Single(await HistoryAsync(carol, room.Alias, "?limit=1"))
            .GetProperty("body").GetString());
        foreach (var party in new[] { alice, bob })
            Assert.Equal(3, (await HistoryAsync(party, room.RuntimeId)).Length);
        Assert.All(await host.StoredChatAsync(), row => Assert.Equal(room.Alias, row.GameId));
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task NativeHubJoinAndSeatChangesUpdateActualChatMembership()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var visitor = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var visitorHub = await host.HubAsync(visitor);
        using var beforeJoin = await visitor.Http.GetAsync($"/api/games/{room.Alias}/chat");
        Assert.Equal(HttpStatusCode.Forbidden, beforeJoin.StatusCode);
        Assert.True((await visitorHub.InvokeAsync("JoinTable", room.RuntimeId)).GetProperty("success").GetBoolean());
        var spectator = await ChatAsync(visitor, room.Alias, "Native hub spectator", "spectators");
        Assert.Equal("spectators", spectator.GetProperty("channel").GetString());
        Assert.True((await visitorHub.InvokeAsync("TakeSeat", room.RuntimeId, 3)).GetProperty("success").GetBoolean());
        _ = await ChatAsync(visitor, room.Alias, "Now a seated native participant");
        using var noLongerSpectating = await visitor.Http.PostAsJsonAsync($"/api/games/{room.Alias}/chat",
            new { channel = "spectators", body = "Seating changes channel entitlement" });
        Assert.Equal(HttpStatusCode.Forbidden, noLongerSpectating.StatusCode);
        Assert.Equal("Now a seated native participant",
            Assert.Single(await HistoryAsync(owner, room.Alias)).GetProperty("body").GetString());
        Assert.Equal(3, host.Runtime.TryGetSeatForPlayer(room.RuntimeId, visitor.Id));
    }

    private static void AssertMessage(JsonElement message, LobbyRoom room, LobbyPlayer sender,
        string channel, string? recipient, string body)
    {
        Assert.True(Guid.TryParse(message.GetProperty("id").GetString(), out _));
        Assert.Equal(room.Alias, message.GetProperty("gameId").GetString());
        Assert.Equal(sender.Id, message.GetProperty("senderPlayerId").GetString());
        Assert.Equal(sender.DisplayName, message.GetProperty("senderDisplayName").GetString());
        Assert.True(message.TryGetProperty("senderAvatarColor", out _));
        Assert.Equal(recipient, message.GetProperty("recipientPlayerId").GetString());
        Assert.Equal(channel, message.GetProperty("channel").GetString());
        Assert.Equal(body, message.GetProperty("body").GetString());
        Assert.Equal(TimeSpan.Zero, message.GetProperty("sentUtc").GetDateTimeOffset().Offset);
        Assert.Equal(sender.Id, message.GetProperty("playerId").GetString());
        Assert.Equal(message.GetProperty("sentUtc").GetDateTimeOffset(), message.GetProperty("at").GetDateTimeOffset());
    }
}
