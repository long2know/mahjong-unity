using System.Net;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Mahjong.Autotable.Api.Tests.TestInfrastructure.LobbyRepairFixture;

namespace Mahjong.Autotable.Api.Tests.Chat;

public sealed class ChatBackfillEndpointTests : IAsyncLifetime
{
    private LobbyRepairFixture _host = null!;
    private LobbyPlayer _player = null!;
    private LobbyRoom _room = null!;

    public async Task InitializeAsync()
    {
        _host = new LobbyRepairFixture();
        _player = await _host.PlayerAsync();
        _room = await _host.CreateRoomAsync(_player, 0);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatBackfill_UnknownGame_Returns404()
    {
        var before = await _host.StoredRuntimeIdsAsync();
        using var response = await _player.Http.GetAsync($"/api/games/unknown-{Guid.NewGuid():N}/chat");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await _host.StoredRuntimeIdsAsync());
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatBackfill_Response_HasMessagesArray()
    {
        var sent = await ChatAsync(_player, _room.Alias, "An actual authenticated room message");
        var received = Assert.Single(await HistoryAsync(_player, _room.Alias));
        Assert.Equal(sent.GetProperty("id").GetString(), received.GetProperty("id").GetString());
        Assert.Equal(_player.Id, received.GetProperty("senderPlayerId").GetString());
        Assert.Equal("An actual authenticated room message", received.GetProperty("body").GetString());
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatBackfill_LimitParameter_Honoured_OrClamped()
    {
        _ = await ChatAsync(_player, _room.Alias, "The real send route is present");
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Historical rows isolate pagination from the independent six-send quota.
            db.ChatMessages.AddRange(Enumerable.Range(0, 204).Select(index => new ChatMessage
            {
                GameId = _room.RuntimeId, PlayerId = _player.Id, Channel = "table",
                Body = $"Historical row {index}", At = DateTime.UtcNow.AddMinutes(-10).AddSeconds(index),
            }));
            await db.SaveChangesAsync();
        }
        Assert.Equal(205, (await _host.StoredChatAsync()).Length);
        Assert.Equal(200, (await HistoryAsync(_player, _room.Alias, "?limit=10000")).Length);
        Assert.Single(await HistoryAsync(_player, _room.Alias, "?limit=0"));
        Assert.Equal(3, (await HistoryAsync(_player, _room.Alias, "?limit=3")).Length);
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatBackfill_SinceParameter_AcceptsIsoTimestamp()
    {
        var first = await ChatAsync(_player, _room.Alias, "Before cursor");
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await ChatAsync(_player, _room.Alias, "After cursor");
        var since = Uri.EscapeDataString(first.GetProperty("sentUtc").GetString()!);
        var page = Assert.Single(await HistoryAsync(_player, _room.Alias, "?since=" + since + "&limit=10"));
        Assert.Equal(second.GetProperty("id").GetString(), page.GetProperty("id").GetString());
        Assert.Equal("After cursor", page.GetProperty("body").GetString());
    }
}
