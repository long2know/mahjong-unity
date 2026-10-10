using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.RulesQualification;

internal sealed class HudsonOwnTurnWsFixture : IAsyncDisposable
{
    private readonly string _database;
    private readonly string _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private readonly bool _persist;
    private readonly ITestOutputHelper? _output;
    private WebApplicationFactory<Program> _factory;

    public HudsonOwnTurnWsFixture(bool persist = false, ITestOutputHelper? output = null)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "hudson-own-turn");
        Directory.CreateDirectory(directory);
        _database = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        _persist = persist;
        _output = output;
        _factory = CreateFactory();
    }

    public IChangshaGameRuntime Runtime => _factory.Services.GetRequiredService<IChangshaGameRuntime>();
    public AutotableConnectionManager Manager => _factory.Services.GetRequiredService<AutotableConnectionManager>();
    public IServiceProvider Services => _factory.Services;

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Persistence:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_database};Pooling=False");
            builder.UseSetting("Authentication:JwtSigningKeys:0", _signingKey);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => services.Configure<ChangshaRuntimeOptions>(options =>
            {
                options.PersistSnapshots = _persist;
                options.BotTurnDelayMs = 30_000;
                options.BotClaimDelayMs = 30_000;
                options.BotPickupDelayMs = 30_000;
                options.ClaimWindowTimeoutMs = 30_000;
                options.DealBatchDelayMs = 0;
            }));
        });

    public async Task RestartAsync()
    {
        await _factory.DisposeAsync();
        _factory = CreateFactory();
        _ = _factory.Server;
    }

    public async Task<WebSocket> OpenSocketAsync(string query, string? playerId = null)
    {
        playerId ??= $"hudson-action-{Guid.NewGuid():N}";
        var server = _factory.Server;
        var client = server.CreateWebSocketClient();
        var token = _factory.Services.GetRequiredService<PlayerIdentityService>().Protect(playerId);
        client.ConfigureRequest = request =>
            request.Headers["Cookie"] = $"{PlayerIdentityService.CookieName}={token}";
        var uri = new Uri(server.BaseAddress, $"autotable/ws?{query}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _output?.WriteLine($"WS connect player={playerId} query={query}");
        return await client.ConnectAsync(uri, cancellation.Token);
    }

    public async Task<HubConnection> ConnectHubAsync(string playerId)
    {
        var server = _factory.Server;
        var token = _factory.Services.GetRequiredService<PlayerIdentityService>().Protect(playerId);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/changsha"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add("Cookie", $"{PlayerIdentityService.CookieName}={token}");
            })
            .Build();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.StartAsync(timeout.Token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<WsPeer> ConnectAsync(
        string roomId, string? playerId = null, string extraQuery = "", params string[] ephemeralKinds)
    {
        playerId ??= $"hudson-action-{Guid.NewGuid():N}";
        var socket = await OpenSocketAsync(
            $"variant=changsha&bots=false&botCount=0&dealMode=manual&handCount=1&seed=20260912{extraQuery}", playerId);
        var peer = new WsPeer(socket, roomId, playerId, _output);
        try
        {
            await peer.BarrierAsync();
            var kinds = new[] { "claim", "pickup", "turn", "discard", "gameComplete" }
                .Concat(ephemeralKinds).Distinct().ToArray();
            await peer.UpdateAsync(kinds.Select(kind => new object[] { "ephemeral", kind, true }).ToArray());
            await peer.BarrierAsync();
            return peer;
        }
        catch
        {
            await peer.DisposeAsync();
            throw;
        }
    }

    public async Task<HumanTable> OpenHumanTableAsync(
        int seat = 0, string extraQuery = "", string? playerId = null, params string[] ephemeralKinds)
    {
        var room = $"hudson-own-{Guid.NewGuid():N}";
        var peer = await ConnectAsync(room, playerId, extraQuery, ephemeralKinds);
        try
        {
            await peer.UpdateAsync([new object[] { "seats", peer.PlayerId, new { seat } }]);
            await peer.BarrierAsync();
            var game = Manager.GetRuntimeGameIdBoundTo(room)
                ?? throw new InvalidOperationException("Normal authenticated seat did not bind a runtime.");
            Assert.Equal(seat, Runtime.TryGetSeatForPlayer(game, peer.PlayerId));
            Assert.True(Runtime.TryGetSnapshot(game, out var state));
            Assert.NotNull(state);
            Assert.All(state.Seats, s => Assert.False(s.IsBot));
            _output?.WriteLine($"Runtime room={room} gameId={game} owner={peer.PlayerId} seat={seat} baseUnit={state.BaseUnit}");
            return new HumanTable(room, game, seat, state, peer);
        }
        catch
        {
            await peer.DisposeAsync();
            throw;
        }
    }

    public async Task<ChangshaGameState> SnapshotAsync(HumanTable table) =>
        await Runtime.TryGetSnapshotCopyAsync(table.GameId)
        ?? throw new InvalidOperationException("Expected owned runtime snapshot.");

    public async Task AdvanceToDrawAsync(HumanTable table, DrawFixture fixture)
    {
        var previous = (table.Seat + 3) % 4;
        Assert.Empty(new ClaimAdjudicator().GetOpportunities(previous, 104, table.State.Hands));
        var before = table.State.Hands[table.Seat].ConcealedTiles.Count;
        await Runtime.DiscardAsync(table.GameId, previous, 104);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, table.State.Phase);
        Assert.Equal(table.Seat, table.State.ActiveSeatIndex);
        Assert.Equal(before + 1, table.State.Hands[table.Seat].ConcealedTiles.Count);
        Assert.Equal(fixture.DrawTile, table.State.Hands[table.Seat].ConcealedTiles[^1]);
        Assert.Equal(table.Seat, table.State.LastDrawSeatIndex);
        Assert.Contains(table.State.EventLog, e => e.EventType == "tile-drawn" && e.SeatIndex == table.Seat);
        AssertInventory(table.State);
    }

    public static DrawFixture ArrangeBeforeDraw(HumanTable table, string scenario)
    {
        var own = table.Seat;
        var previous = (own + 3) % 4;
        var state = table.State;
        int draw;
        int[] before;
        List<Meld> melds = [];
        int[]? robber = null;
        switch (scenario)
        {
            case "hu-14":
                draw = 0;
                before = [4, 8, 12, 16, 20, 24, 28, 32, 52, 53, 36, 40, 44];
                break;
            case "hu-11":
                draw = 20;
                before = [12, 16, 36, 40, 44, 72, 76, 80, 88, 89];
                melds.Add(new Meld { Kind = MeldKind.Chow, TileIds = [0, 4, 8], ClaimedFromSeatIndex = previous });
                break;
            case "hu-8":
                draw = 20;
                before = [12, 16, 36, 40, 44, 88, 89];
                melds.Add(new Meld { Kind = MeldKind.Chow, TileIds = [0, 4, 8], ClaimedFromSeatIndex = previous });
                melds.Add(new Meld { Kind = MeldKind.Chow, TileIds = [48, 52, 56], ClaimedFromSeatIndex = previous });
                break;
            case "concealed-kong":
                draw = 19;
                before = [16, 17, 18, 40, 41, 42, 44, 45, 46, 48, 49, 50, 52];
                break;
            case "pung-no-draw":
                draw = 19;
                before = [16, 17, 18, 40, 41, 42, 43, 44, 45, 46, 48, 49, 50];
                break;
            case "chow-options":
                draw = 19;
                before = [8, 12, 20, 24, 40, 41, 42, 44, 45, 46, 72, 76, 88];
                break;
            case "added-kong":
            case "added-kong-rob":
                draw = 19;
                before = [40, 41, 42, 44, 45, 46, 48, 49, 50, 52];
                melds.Add(new Meld { Kind = MeldKind.Pung, TileIds = [16, 17, 18], ClaimedFromSeatIndex = (own + 2) % 4 });
                if (scenario == "added-kong-rob")
                    robber = [0, 1, 2, 24, 25, 26, 60, 61, 62, 88, 89, 8, 12];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown fixed conserved scenario.");
        }
        foreach (var hand in state.Hands)
        {
            hand.ConcealedTiles.Clear();
            hand.Melds.Clear();
        }
        state.Hands[own].ConcealedTiles.AddRange(before);
        state.Hands[own].Melds.AddRange(melds);
        var used = before.Concat(melds.SelectMany(m => m.TileIds)).Append(draw).Append(104).ToHashSet();
        Assert.Equal(before.Length + melds.Sum(m => m.TileIds.Count) + 2, used.Count);
        if (robber is not null)
        {
            foreach (var tile in robber) Assert.True(used.Add(tile), $"Fixture duplicated physical tile {tile}");
            state.Hands[(own + 1) % 4].ConcealedTiles.AddRange(robber);
        }
        int[] ordinaryKinds = [0, 1, 2, 3, 5, 6, 7, 8, 9, 14, 15, 16, 17];
        int[] robberyPeerKinds = [1, 2, 3, 5, 7, 8, 9, 13, 14, 16, 17, 18, 19];
        foreach (var hand in state.Hands.Where(h => h.SeatIndex != own && h.ConcealedTiles.Count == 0))
        {
            foreach (var kind in robber is null ? ordinaryKinds : robberyPeerKinds)
            {
                var tile = Enumerable.Range(kind * 4, 4).First(id => !used.Contains(id));
                Assert.True(used.Add(tile));
                hand.ConcealedTiles.Add(tile);
            }
        }
        state.Hands[previous].ConcealedTiles.Add(104);
        state.Wall = new[] { draw }.Concat(Enumerable.Range(0, 108).Where(id => !used.Contains(id))).ToList();
        state.WallDrawIndex = state.WallBackDrawn = 0;
        state.WallBackIndex = state.Wall.Count - 1;
        state.DiscardPile.Clear();
        state.ClaimWindow = null;
        state.CurrentWin = null;
        state.CurrentScore = null;
        state.MissedWinSeats.Clear();
        state.LastDrawWasKongReplacement = false;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.ActiveSeatIndex = previous;
        state.DealerSeatIndex = own;
        foreach (var seat in state.Seats) seat.IsDealer = seat.SeatIndex == own;
        state.TurnNumber = 3;
        state.MaxHands = 1;
        state.IsGameComplete = false;
        state.BreakPoint = new BreakPointService().ComputeBreakPoint(5, own);
        Assert.Equal(13, before.Length + 3 * melds.Count);
        AssertInventory(state);
        return new DrawFixture(draw, state.Wall[^1], state.Wall.Count, robber is null ? null : (own + 1) % 4);
    }

    public static void ArrangeIncomingDiscard(HumanTable table, DrawFixture fixture)
    {
        var previous = table.State.Hands[(table.Seat + 3) % 4];
        Assert.True(previous.ConcealedTiles.Remove(104));
        Assert.True(table.State.Wall.Remove(fixture.DrawTile));
        previous.ConcealedTiles.Add(fixture.DrawTile);
        table.State.Wall.Add(104);
        AssertInventory(table.State);
    }

    public static void AssertInventory(ChangshaGameState state)
    {
        var actual = state.Wall.Concat(state.Hands.SelectMany(h =>
            h.ConcealedTiles.Concat(h.Melds.SelectMany(m => m.TileIds))))
            .Concat(state.DiscardPile.Select(d => d.TileId)).OrderBy(id => id).ToArray();
        Assert.Equal(Enumerable.Range(0, 108), actual);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        foreach (var file in new[] { _database, _database + "-wal", _database + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    public sealed record DrawFixture(int DrawTile, int BackTile, int WallCountBefore, int? RobberSeat);
    public sealed record HumanTable(string RoomId, string GameId, int Seat, ChangshaGameState State, WsPeer Peer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Peer.DisposeAsync();
    }

    public sealed class WsPeer(
        WebSocket socket, string roomId, string playerId, ITestOutputHelper? output = null) : IAsyncDisposable
    {
        private bool _disposed;
        public string RoomId { get; private set; } = roomId;
        public string PlayerId { get; } = playerId;
        public List<JsonElement> Frames { get; } = [];

        public async Task UpdateAsync(object[] entries)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "UPDATE", entries, full = false });
            output?.WriteLine($"WS update room={RoomId} player={PlayerId} entries={JsonSerializer.Serialize(entries)}");
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
        }

        public async Task<JsonElement> BarrierAsync(string? nextRoom = null)
        {
            RoomId = nextRoom ?? RoomId;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "JOIN", gameId = RoomId });
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
            var joined = false;
            while (true)
            {
                var message = await ReceiveAsync(timeout.Token);
                Frames.Add(message);
                if (message.TryGetProperty("entries", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray().Where(entry =>
                        entry[0].GetString() is "actionRejected" or "gameComplete"
                        || entry[0].GetString() is "ownTurn" or "claim" && entry[2].ValueKind != JsonValueKind.Null))
                    {
                        output?.WriteLine($"WS receive room={RoomId} player={PlayerId} entry={entry.GetRawText()}");
                    }
                }
                var type = message.GetProperty("type").GetString();
                if (type == "JOINED")
                {
                    Assert.Equal(RoomId, message.GetProperty("gameId").GetString());
                    Assert.Equal(PlayerId, message.GetProperty("playerId").GetString());
                    joined = true;
                }
                else if (joined && type == "UPDATE" &&
                         message.TryGetProperty("full", out var full) && full.GetBoolean())
                {
                    return message;
                }
            }
        }

        private async Task<JsonElement> ReceiveAsync(CancellationToken cancellation)
        {
            var buffer = new byte[64 * 1024];
            using var bytes = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                if (part.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"WS closed before a JOIN processing barrier: {socket.CloseStatus}");
                bytes.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            using var document = JsonDocument.Parse(bytes.ToArray());
            return document.RootElement.Clone();
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            socket.Abort();
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
