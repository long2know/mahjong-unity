using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Mahjong.Autotable.Api.Tests.TestInfrastructure;

internal sealed class LobbyRepairFixture : IAsyncDisposable
{
    private readonly string _database;
    private readonly string _key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private readonly List<IAsyncDisposable> _transports = [];
    private readonly List<HttpClient> _clients = [];
    private WebApplicationFactory<Program> _factory;

    public LobbyRepairClock Clock { get; } = new();
    public IServiceProvider Services => _factory.Services;
    public IChangshaGameRuntime Runtime => Services.GetRequiredService<IChangshaGameRuntime>();
    public AutotableConnectionManager Manager => Services.GetRequiredService<AutotableConnectionManager>();

    public LobbyRepairFixture()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "lobby-repair");
        Directory.CreateDirectory(directory);
        _database = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        _factory = Build();
    }

    private WebApplicationFactory<Program> Build() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Persistence:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_database};Pooling=False");
            builder.UseSetting("Authentication:JwtSigningKeys:0", _key);
            // Capacity tests exercise the shared chat/invite quota, not the independent HTTP-IP quota.
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.Configure<ChangshaRuntimeOptions>(options =>
                {
                    options.PersistSnapshots = true;
                    options.BotTurnDelayMs = 350;
                    options.BotClaimDelayMs = 250;
                    options.BotPickupDelayMs = 100;
                    options.DealBatchDelayMs = 0;
                });
            });
        });

    public HttpClient Http(string? cookie = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        _clients.Add(client);
        return client;
    }

    public async Task<LobbyPlayer> PlayerAsync()
    {
        var http = Http();
        using var response = await http.PostAsync("/api/identity", null);
        var identity = await JsonAsync(response);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(PlayerIdentityService.CookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        var player = new LobbyPlayer(identity.GetProperty("playerId").GetString()!,
            identity.GetProperty("displayName").GetString()!, cookie, http);
        Assert.NotEmpty(player.Id);
        Assert.NotEqual(PlayerIdentityService.CookieName + "=" + player.Id, cookie);
        return player;
    }

    public LobbyPlayer ReopenHttp(LobbyPlayer player) => player with { Http = Http(player.Cookie) };

    public async Task<LobbyHubPeer> HubAsync(LobbyPlayer? player, bool joinLobby = true)
    {
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/changsha"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (player is not null) options.Headers.Add("Cookie", player.Cookie);
            }).Build();
        var peer = new LobbyHubPeer(connection);
        _transports.Add(peer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.StartAsync(timeout.Token);
        if (joinLobby) _ = await peer.LobbyAsync();
        return peer;
    }

    public async Task<LobbyWsPeer> SocketAsync(LobbyPlayer player, string roomId, string query, string command = "JOIN",
        string variant = "changsha")
    {
        var server = _factory.Server;
        var client = server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Cookie"] = player.Cookie;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var socket = await client.ConnectAsync(new Uri(server.BaseAddress,
            $"autotable/ws?variant={Uri.EscapeDataString(variant)}&gameId={Uri.EscapeDataString(roomId)}&{query}"), timeout.Token);
        var peer = new LobbyWsPeer(socket);
        _transports.Add(peer);
        await peer.SendAsync(command == "NEW" ? new { type = "NEW" } : (object)new { type = "JOIN", gameId = roomId });
        return peer;
    }

    public async Task<LobbyRoom> CreateRoomAsync(
        LobbyPlayer owner, int? botCount = 0, int seat = 0, string extraQuery = "", string dealMode = "manual",
        string? roomId = null)
    {
        var alias = roomId ?? $"hudson-lobby-{Guid.NewGuid():N}";
        var count = botCount.HasValue ? $"&botCount={botCount.Value}" : "";
        var peer = await SocketAsync(owner, alias,
            $"seat={seat}&dealMode={dealMode}&seed=20260917&handCount=4&baseUnit=7&botDifficulty=Easy{count}{extraQuery}",
            command: "NEW");
        await peer.WaitAsync(frame => Type(frame) == "JOINED");
        if (seat >= 0)
            await peer.UpdateAsync(["seats", owner.Id, new { seat }]);
        await UntilAsync(() => Manager.GetRuntimeGameIdBoundTo(alias) is not null,
            "A normal NEW/seat flow did not bind the requested alias.");
        var runtimeId = Manager.GetRuntimeGameIdBoundTo(alias)!;
        Assert.NotEqual(alias, runtimeId);
        Assert.True(Guid.TryParse(runtimeId, out _));
        if (seat >= 0)
            await UntilAsync(() => Runtime.TryGetSeatForPlayer(runtimeId, owner.Id) == seat,
                "The creator's normal seat request was not granted.");
        return new LobbyRoom(alias, runtimeId, owner, peer);
    }

    public async Task<LobbyWsPeer> JoinAsync(LobbyPlayer player, LobbyRoom room, string extraQuery = "")
    {
        var peer = await SocketAsync(player, room.Alias, "join=1" + extraQuery);
        await peer.WaitAsync(frame => Type(frame) == "JOINED");
        await peer.WaitAsync(IsFull);
        return peer;
    }

    public async Task<LobbyWsPeer> ObserveAsync(LobbyPlayer player, LobbyRoom room)
    {
        var peer = await SocketAsync(player, room.Alias, "seat=-1");
        await peer.WaitAsync(frame => Type(frame) == "JOINED");
        await peer.WaitAsync(IsFull);
        return peer;
    }

    public async Task<ChangshaGameState> StateAsync(LobbyRoom room) =>
        await Runtime.TryGetSnapshotCopyAsync(room.RuntimeId)
        ?? throw new Xunit.Sdk.XunitException("Expected the original bound runtime to exist.");

    public static IEnumerable<string> AssignedHumanIds(ChangshaGameState state) =>
        state.Seats.Where(seat => !seat.IsBot && !string.IsNullOrEmpty(seat.PlayerId)
            && !string.Equals(seat.PlayerId, $"human-{seat.SeatIndex}", StringComparison.Ordinal))
            .Select(seat => seat.PlayerId);

    public static void AssertHumanIdentitySet(ChangshaGameState state, IEnumerable<string> playerIds)
    {
        var expected = playerIds.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(id => id), AssignedHumanIds(state).OrderBy(id => id));
        foreach (var seat in state.Seats.Where(seat => !seat.IsBot && !expected.Contains(seat.PlayerId)))
            Assert.Equal($"human-{seat.SeatIndex}", seat.PlayerId);
    }

    public async Task<LobbyRoom> RestoreWithReservedSeatsAsync(LobbyRoom room, params int[] seats)
    {
        var before = await StateAsync(room);
        Assert.False(before.IsPublic);
        var reservedOwners = new Dictionary<int, string>();
        foreach (var seat in seats)
        {
            Assert.False(before.Seats[seat].IsBot);
            var player = await PlayerAsync();
            var hub = await HubAsync(player);
            var granted = await hub.InvokeAsync("TakeSeat", room.RuntimeId, seat);
            Assert.True(granted.GetProperty("success").GetBoolean());
            Assert.Equal(seat, granted.GetProperty("seatIndex").GetInt32());
            Assert.Equal(player.Id, (await StoredStateAsync(room)).Seats[seat].PlayerId);
            reservedOwners.Add(seat, player.Id);
        }
        Assert.Equal(ChangshaPhase.Seating, (await StateAsync(room)).Phase);
        await RestartAsync();
        var owner = ReopenHttp(room.Owner);
        var rejoined = await JoinAsync(owner, room);
        var restored = room with { Owner = owner, Creator = rejoined };
        var metadata = await MetadataAsync(owner, room.Alias);
        Assert.Equal("Seating", metadata.GetProperty("phase").GetString());
        Assert.Equal(1, metadata.GetProperty("seatedCount").GetInt32());
        Assert.Equal(before.Seats.Count(seat => !seat.IsBot) - 1 - seats.Length,
            metadata.GetProperty("openHumanSeats").GetInt32());
        Assert.Equal(room.RuntimeId, Manager.GetRuntimeGameIdBoundTo(room.Alias));
        var after = await StateAsync(restored);
        var stored = await StoredStateAsync(restored);
        foreach (var (seat, playerId) in reservedOwners)
        {
            Assert.Equal(playerId, after.Seats[seat].PlayerId);
            Assert.Equal(playerId, stored.Seats[seat].PlayerId);
            Assert.DoesNotContain(ViewerAuthorityAssertions.Connections(Manager),
                connection => connection.PlayerId == playerId);
        }
        return restored;
    }

    public Task<JsonElement> MetadataAsync(LobbyPlayer player, string roomId, string suffix = "") =>
        GetJsonAsync(player.Http, $"/api/games/{Uri.EscapeDataString(roomId)}{suffix}");

    public async Task<JsonElement[]> LobbyAsync(LobbyPlayer player) =>
        (await GetJsonAsync(player.Http, "/api/matchmaking/lobby")).GetProperty("games").EnumerateArray().ToArray();

    public static async Task<JsonElement> GetJsonAsync(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path);
        return await JsonAsync(response);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}; got {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> ChatAsync(
        LobbyPlayer player, string roomId, string body, string channel = "table", string? recipient = null)
    {
        using var response = await player.Http.PostAsJsonAsync($"/api/games/{Uri.EscapeDataString(roomId)}/chat",
            new { channel, recipientPlayerId = recipient, body });
        return await JsonAsync(response);
    }

    public static async Task<JsonElement[]> HistoryAsync(LobbyPlayer player, string roomId, string query = "") =>
        (await GetJsonAsync(player.Http, $"/api/games/{Uri.EscapeDataString(roomId)}/chat{query}"))
            .GetProperty("messages").EnumerateArray().ToArray();

    public async Task<ChatMessage[]> StoredChatAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatMessages.AsNoTracking().ToArrayAsync();
    }

    public async Task<string[]> StoredRuntimeIdsAsync()
    {
        using var scope = Services.CreateScope();
        var ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangshaGames.AsNoTracking()
            .Select(game => game.Id).ToArrayAsync();
        return ids.Select(id => id.ToString()).OrderBy(id => id).ToArray();
    }

    public async Task<ChangshaGameState> StoredStateAsync(LobbyRoom room)
    {
        using var scope = Services.CreateScope();
        var id = Guid.Parse(room.RuntimeId);
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangshaGames.AsNoTracking()
            .SingleAsync(game => game.Id == id);
        return JsonSerializer.Deserialize<ChangshaGameState>(row.StateJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new Xunit.Sdk.XunitException("The announced room has no persisted state.");
    }

    public async Task RestartAsync()
    {
        await CloseTransportsAsync();
        await _factory.DisposeAsync();
        _factory = Build();
        _ = _factory.Server;
    }

    private async Task CloseTransportsAsync()
    {
        foreach (var transport in _transports.AsEnumerable().Reverse()) await transport.DisposeAsync();
        _transports.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseTransportsAsync();
        foreach (var client in _clients) client.Dispose();
        await _factory.DisposeAsync();
        foreach (var path in new[] { _database, _database + "-wal", _database + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }

    public static async Task UntilAsync(Func<bool> predicate, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate() && !timeout.IsCancellationRequested)
        {
            try { await Task.Delay(20, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }
        Assert.True(predicate(), message);
    }

    public static string? Type(JsonElement frame) =>
        frame.TryGetProperty("type", out var type) ? type.GetString() : null;

    public static bool IsFull(JsonElement frame) =>
        Type(frame) == "UPDATE" && frame.TryGetProperty("full", out var full) && full.GetBoolean();

    public static IEnumerable<JsonElement> Entries(JsonElement frame) =>
        frame.TryGetProperty("entries", out var entries) ? entries.EnumerateArray().ToArray() : [];
}

internal sealed record LobbyPlayer(string Id, string DisplayName, string Cookie, HttpClient Http);
internal sealed record LobbyRoom(string Alias, string RuntimeId, LobbyPlayer Owner, LobbyWsPeer Creator);

internal sealed class LobbyRepairClock : TimeProvider
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => _start.AddTicks(Interlocked.Read(ref _ticks));
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

internal sealed class LobbyHubPeer : IAsyncDisposable
{
    private bool _disposed;
    public HubConnection Connection { get; }
    public ConcurrentQueue<JsonElement> Invites { get; } = new();
    public ConcurrentQueue<JsonElement> Rosters { get; } = new();

    public LobbyHubPeer(HubConnection connection)
    {
        Connection = connection;
        connection.On<JsonElement>("TableInviteReceived", invite => Invites.Enqueue(invite.Clone()));
        connection.On<JsonElement>("LobbyPlayersChanged", roster => Rosters.Enqueue(roster.Clone()));
    }

    public async Task<JsonElement> InvokeAsync(string method, params object?[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await Connection.InvokeCoreAsync<JsonElement>(method, arguments, timeout.Token);
    }

    public Task<JsonElement> LobbyAsync() => InvokeAsync("JoinLobby");
    public Task<JsonElement> InviteAsync(string roomId, string recipient) => InvokeAsync("SendTableInvite", roomId, recipient);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Connection.DisposeAsync();
    }
}

internal sealed class LobbyWsPeer : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<JsonElement> _updates = Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim _send = new(1);
    private readonly Task _reader;
    private bool _disposed;
    public ConcurrentQueue<JsonElement> Frames { get; } = new();
    public WebSocketCloseStatus? CloseStatus { get; private set; }

    public LobbyWsPeer(WebSocket socket)
    {
        _socket = socket;
        _reader = ReadAsync();
    }

    public async Task SendAsync(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _send.WaitAsync(_stop.Token);
        try { await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _stop.Token); }
        finally { _send.Release(); }
    }

    public Task UpdateAsync(object[] entry) => SendAsync(new { type = "UPDATE", entries = new[] { entry } });

    public async Task<JsonElement> WaitAsync(Func<JsonElement, bool> predicate)
    {
        foreach (var frame in Frames) if (predicate(frame)) return frame;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await _updates.Reader.WaitToReadAsync(timeout.Token))
            while (_updates.Reader.TryRead(out var frame))
                if (predicate(frame)) return frame;
        throw new Xunit.Sdk.XunitException("Socket closed before the expected authoritative frame.");
    }

    private async Task ReadAsync()
    {
        try
        {
            var buffer = new byte[32 * 1024];
            while (!_stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CloseStatus = result.CloseStatus;
                        if (_socket.State == WebSocketState.CloseReceived)
                        {
                            try
                            {
                                await _socket.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                    "acknowledged", CancellationToken.None);
                            }
                            catch (IOException ex) when (ex.InnerException is ObjectDisposedException)
                            {
                                // TestServer can dispose its peer after delivering the policy-close frame.
                            }
                        }
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                var frame = document.RootElement.Clone();
                Frames.Enqueue(frame);
                _updates.Writer.TryWrite(frame);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (WebSocketException) when (_disposed) { }
        finally { _updates.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
            await _reader.WaitAsync(timeout.Token);
        }
        finally
        {
            _stop.Cancel();
            _socket.Dispose();
            _stop.Dispose();
            _send.Dispose();
        }
    }
}
