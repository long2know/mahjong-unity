using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.WebUtilities;
using static Mahjong.Autotable.Api.Tests.TestInfrastructure.LobbyRepairFixture;

namespace Mahjong.Autotable.Api.Tests.Chat;

public sealed class TableInvitationTests
{
    [Fact, Trait("Category", "LobbyRepair")]
    public async Task RecipientOnlyDeliveryAndInbox_DeduplicateAcrossTabsAndReconnect_WithoutChatRows()
    {
        await using var host = new LobbyRepairFixture();
        var sender = await host.PlayerAsync();
        var recipient = await host.PlayerAsync();
        var third = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(sender, 0);
        _ = await host.CreateRoomAsync(recipient, 0);
        var senderHub = await host.HubAsync(sender);
        var senderSecondTab = await host.HubAsync(sender);
        var recipientHub = await host.HubAsync(recipient);
        var recipientSecondTab = await host.HubAsync(recipient);
        var thirdHub = await host.HubAsync(third);
        var results = await Task.WhenAll(
            senderHub.InviteAsync(room.Alias, recipient.Id),
            senderSecondTab.InviteAsync(room.RuntimeId, recipient.Id));
        Assert.All(results, result => Assert.True(result.GetProperty("success").GetBoolean()));
        var invite = results[0].GetProperty("invite");
        AssertInvite(invite, room, sender, recipient);
        var id = invite.GetProperty("inviteId").GetString();
        Assert.Equal(id, results[1].GetProperty("invite").GetProperty("inviteId").GetString());
        await UntilAsync(() => recipientHub.Invites.Count == 1 && recipientSecondTab.Invites.Count == 1,
            "Only the intended recipient's subscribed tabs should receive the notification.");
        foreach (var peer in new[] { recipientHub, recipientSecondTab })
        {
            Assert.Equal(id, Assert.Single(peer.Invites).GetProperty("inviteId").GetString());
            Assert.Equal(id, Assert.Single(Inbox(await peer.LobbyAsync())).GetProperty("inviteId").GetString());
        }
        Assert.Empty(Inbox(await thirdHub.LobbyAsync()));
        Assert.Empty(thirdHub.Invites);
        Assert.Empty(senderHub.Invites);
        Assert.Empty(await host.StoredChatAsync());
        await recipientHub.DisposeAsync();
        var reconnected = await host.HubAsync(recipient);
        Assert.Equal(id, Assert.Single(Inbox(await reconnected.LobbyAsync())).GetProperty("inviteId").GetString());
        var again = await senderHub.InviteAsync(room.Alias, recipient.Id);
        Assert.Equal(id, again.GetProperty("invite").GetProperty("inviteId").GetString());
        _ = await recipientSecondTab.LobbyAsync();
        Assert.Single(recipientSecondTab.Invites);
        Assert.Empty(reconnected.Invites);
    }

    [Theory, Trait("Category", "LobbyRepair")]
    [InlineData("room-not-found")]
    [InlineData("room-not-seating")]
    [InlineData("room-full")]
    [InlineData("recipient-offline")]
    [InlineData("ws-only-recipient")]
    [InlineData("self-invite")]
    [InlineData("not-allowed")]
    public async Task InvalidInvitationHasAConcreteReasonAndNoDeliveryOrHistory(string reason)
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var recipient = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, reason == "room-not-seating" ? 3 : 0);
        if (reason == "room-not-seating")
            await UntilAsync(() => host.Runtime.TryGetSnapshot(room.RuntimeId, out var ready)
                && ready!.Phase != ChangshaPhase.Seating, "Precondition: invitation source has started.");
        if (reason == "room-full")
        {
            room = await host.RestoreWithReservedSeatsAsync(room, 1, 2, 3);
            owner = room.Owner;
            recipient = host.ReopenHttp(recipient);
        }
        var caller = reason == "not-allowed" ? await host.PlayerAsync() : owner;
        var senderHub = await host.HubAsync(caller);
        if (reason == "ws-only-recipient") _ = await host.CreateRoomAsync(recipient, 0);
        var recipientHub = reason is "recipient-offline" or "ws-only-recipient" ? null : await host.HubAsync(recipient);
        var result = await senderHub.InviteAsync(reason == "room-not-found" ? "unknown-room" : room.Alias,
            reason == "self-invite" ? caller.Id : recipient.Id);
        AssertDenied(result, reason == "ws-only-recipient" ? "recipient-offline" : reason);
        if (recipientHub is not null)
        {
            Assert.Empty(Inbox(await recipientHub.LobbyAsync()));
            Assert.Empty(recipientHub.Invites);
        }
        Assert.Empty(await host.StoredChatAsync());
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task UnverifiedSenderFails_AndPublicRoomAllowsAConnectedNoncreatorHuman()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var seatedHuman = await host.PlayerAsync();
        var recipient = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        _ = await host.JoinAsync(seatedHuman, room);
        var ownerHub = await host.HubAsync(owner);
        var humanHub = await host.HubAsync(seatedHuman);
        var recipientHub = await host.HubAsync(recipient);
        var unsigned = await host.HubAsync(null, joinLobby: false);
        AssertDenied(await unsigned.InviteAsync(room.Alias, recipient.Id), "identity-required");
        AssertDenied(await humanHub.InviteAsync(room.Alias, recipient.Id), "not-allowed");
        Assert.Empty(Inbox(await recipientHub.LobbyAsync()));
        _ = await ownerHub.InvokeAsync("SetGamePublic", room.Alias, true, "Public invitations");
        var result = await humanHub.InviteAsync(room.Alias, recipient.Id);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(seatedHuman.Id, result.GetProperty("invite").GetProperty("senderPlayerId").GetString());
        Assert.Single(Inbox(await recipientHub.LobbyAsync()));
    }

    [Theory, Trait("Category", "LobbyRepair")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvitationsAndOrdinaryChatShareSixSends_DuplicatesDoNotSpendQuota(bool inviteFirst)
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var recipient = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var senderHub = await host.HubAsync(owner);
        _ = await host.HubAsync(recipient);
        JsonElement? first = null;
        if (inviteFirst)
        {
            first = await senderHub.InviteAsync(room.Alias, recipient.Id);
            Assert.True(first.Value.GetProperty("success").GetBoolean());
        }
        for (var i = 0; i < (inviteFirst ? 5 : 6); i++)
            _ = await ChatAsync(owner, room.Alias, $"Shared quota message {i}");
        if (inviteFirst)
        {
            var duplicate = await senderHub.InviteAsync(room.RuntimeId, recipient.Id);
            Assert.True(duplicate.GetProperty("success").GetBoolean());
            Assert.Equal(first!.Value.GetProperty("invite").GetProperty("inviteId").GetString(),
                duplicate.GetProperty("invite").GetProperty("inviteId").GetString());
            using var seventh = await owner.Http.PostAsJsonAsync($"/api/games/{room.Alias}/chat",
                new { channel = "table", body = "Seventh combined send" });
            Assert.Equal(HttpStatusCode.TooManyRequests, seventh.StatusCode);
        }
        else
        {
            AssertDenied(await senderHub.InviteAsync(room.Alias, recipient.Id), "rate-limited");
        }
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        _ = await ChatAsync(owner, room.Alias, "Window has actually advanced");
        if (!inviteFirst)
            Assert.True((await senderHub.InviteAsync(room.Alias, recipient.Id)).GetProperty("success").GetBoolean());
    }

    [Fact, Trait("Category", "LobbyRepair")]
    public async Task ExpirationPrunesInboxAndAllowsANewInviteInsteadOfReturningStaleDeduplication()
    {
        await using var host = new LobbyRepairFixture();
        var owner = await host.PlayerAsync();
        var recipient = await host.PlayerAsync();
        var room = await host.CreateRoomAsync(owner, 0);
        var senderHub = await host.HubAsync(owner);
        var receiver = await host.HubAsync(recipient);
        var first = (await senderHub.InviteAsync(room.Alias, recipient.Id)).GetProperty("invite");
        Assert.Equal(TimeSpan.FromMinutes(5),
            first.GetProperty("expiresUtc").GetDateTimeOffset() - first.GetProperty("createdUtc").GetDateTimeOffset());
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(Inbox(await receiver.LobbyAsync()));
        var second = (await senderHub.InviteAsync(room.Alias, recipient.Id)).GetProperty("invite");
        Assert.NotEqual(first.GetProperty("inviteId").GetString(), second.GetProperty("inviteId").GetString());
        Assert.Equal(second.GetProperty("inviteId").GetString(),
            Assert.Single(Inbox(await receiver.LobbyAsync())).GetProperty("inviteId").GetString());
    }

    [Fact, Trait("Category", "LobbyRepairCapacity")]
    public async Task RecipientCapacityIsFifty_AndExpiredEntriesArePrunedBeforeTheCapacityCheck()
    {
        await using var host = new LobbyRepairFixture();
        var recipient = await host.PlayerAsync();
        var receiver = await host.HubAsync(recipient);
        for (var i = 0; i < 51; i++)
        {
            var owner = await host.PlayerAsync();
            var room = await host.CreateRoomAsync(owner, 0);
            var sender = await host.HubAsync(owner);
            var result = await sender.InviteAsync(room.Alias, recipient.Id);
            if (i < 50)
            {
                Assert.True(result.GetProperty("success").GetBoolean());
            }
            else
            {
                AssertDenied(result, "inbox-full");
                Assert.Equal(50, Inbox(await receiver.LobbyAsync()).Length);
                host.Clock.Advance(TimeSpan.FromMinutes(5));
                Assert.True((await sender.InviteAsync(room.Alias, recipient.Id)).GetProperty("success").GetBoolean());
                Assert.Single(Inbox(await receiver.LobbyAsync()));
            }
        }
        Assert.Empty(await host.StoredChatAsync());
    }

    [Fact, Trait("Category", "LobbyRepairCapacity")]
    public async Task GlobalCapacityIsOneThousandWithoutMisreportingRecipientCapacityOrSendQuota()
    {
        await using var host = new LobbyRepairFixture();
        var recipients = new List<(LobbyPlayer Player, LobbyHubPeer Hub)>();
        for (var i = 0; i < 51; i++)
        {
            var player = await host.PlayerAsync();
            recipients.Add((player, await host.HubAsync(player)));
        }
        var senders = new List<(LobbyRoom Room, LobbyHubPeer Hub)>();
        for (var i = 0; i < 20; i++)
        {
            var owner = await host.PlayerAsync();
            senders.Add((await host.CreateRoomAsync(owner, 0), await host.HubAsync(owner)));
        }
        for (var recipientIndex = 0; recipientIndex < 50; recipientIndex++)
        {
            if (recipientIndex > 0 && recipientIndex % 6 == 0) host.Clock.Advance(TimeSpan.FromSeconds(31));
            foreach (var sender in senders)
            {
                var result = await sender.Hub.InviteAsync(sender.Room.Alias, recipients[recipientIndex].Player.Id);
                Assert.True(result.GetProperty("success").GetBoolean());
            }
        }
        var total = 0;
        foreach (var recipient in recipients.Take(50))
        {
            var inbox = Inbox(await recipient.Hub.LobbyAsync());
            Assert.Equal(20, inbox.Length);
            total += inbox.Length;
        }
        Assert.Equal(1000, total);
        var overflow = await senders[0].Hub.InviteAsync(senders[0].Room.Alias, recipients[50].Player.Id);
        AssertDenied(overflow, "inbox-full");
        Assert.Empty(Inbox(await recipients[50].Hub.LobbyAsync()));
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await senders[0].Hub.InviteAsync(senders[0].Room.Alias, recipients[50].Player.Id))
            .GetProperty("success").GetBoolean());
        Assert.Single(Inbox(await recipients[50].Hub.LobbyAsync()));
        Assert.Empty(Inbox(await recipients[0].Hub.LobbyAsync()));
    }

    private static JsonElement[] Inbox(JsonElement lobby) => lobby.GetProperty("invites").EnumerateArray().ToArray();

    private static void AssertDenied(JsonElement result, string reason)
    {
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(reason, result.GetProperty("reason").GetString());
        Assert.False(result.TryGetProperty("invite", out _));
    }

    private static void AssertInvite(JsonElement invite, LobbyRoom room, LobbyPlayer sender, LobbyPlayer recipient)
    {
        Assert.Equal(new[] {
            "createdUtc", "expiresUtc", "gameId", "inviteId", "joinUrl", "publicName", "recipientPlayerId",
            "senderAvatarColor", "senderDisplayName", "senderPlayerId",
        }, invite.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.True(Guid.TryParse(invite.GetProperty("inviteId").GetString(), out _));
        Assert.Equal(sender.Id, invite.GetProperty("senderPlayerId").GetString());
        Assert.Equal(sender.DisplayName, invite.GetProperty("senderDisplayName").GetString());
        Assert.Equal(recipient.Id, invite.GetProperty("recipientPlayerId").GetString());
        Assert.Equal(room.Alias, invite.GetProperty("gameId").GetString());
        var relative = invite.GetProperty("joinUrl").GetString()!;
        Assert.StartsWith("/autotable/?", relative);
        var uri = new Uri(new Uri("https://table.invalid"), relative);
        Assert.Equal("table.invalid", uri.Host);
        Assert.Empty(uri.Fragment);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(new[] { "gameId", "join", "variant" }, query.Keys.OrderBy(key => key));
        Assert.All(query.Values, value => Assert.Single(value));
        Assert.Equal(room.Alias, query["gameId"].ToString());
        Assert.Equal("changsha", query["variant"].ToString());
        Assert.Equal("1", query["join"].ToString());
        Assert.Equal(TimeSpan.Zero, invite.GetProperty("createdUtc").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.Zero, invite.GetProperty("expiresUtc").GetDateTimeOffset().Offset);
    }
}
