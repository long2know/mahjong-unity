using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Hub;

/// <summary>
/// Spins up a WebApplicationFactory<Program>, connects a SignalR client to /hubs/changsha
/// over the in-memory test server, and exposes helpers for capturing events.
/// </summary>
internal sealed class ChangshaHubTestHarness : IAsyncDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    private readonly List<HubConnection> _connections = new();
    public ConcurrentBag<RecordedEvent> Recorded { get; } = new();

    public ChangshaHubTestHarness()
    {
        // Use a per-instance temp sqlite file inside the project's `data/` folder to avoid
        // collisions between parallel test runs (and to satisfy the workspace /tmp restriction).
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = Path.Combine(dataDir, $"mahjong-e2e-{Guid.NewGuid():N}.db");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
            builder.ConfigureServices(services =>
            {
                services.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = false;
                });
            });
        });
        _tempDbPath = tempDb;
        _ = Factory.Server;
    }

    private readonly string _tempDbPath;

    public async Task<HubConnection> ConnectAsync(string? playerId = null)
    {
        var server = Factory.Server;
        var conn = new HubConnectionBuilder()
            .WithUrl(server.BaseAddress + "hubs/changsha", o =>
            {
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                if (playerId is not null)
                {
                    var identity = Factory.Services.GetRequiredService<PlayerIdentityService>();
                    o.Headers["Cookie"] = $"{PlayerIdentityService.CookieName}={identity.Protect(playerId)}";
                }
            })
            .Build();

        // Wire up universal recorder for known event names.
        foreach (var name in new[]
        {
            "GameCreated","PlayerSeated","GameStarted","DiceRolled","BreakPointSet","TilesDealt",
            "TurnStarted","TileDrawn","TileDiscarded","ClaimWindowOpen","ClaimMade",
            "KongReplacementDrawn","WinDeclared","ScoringComplete","BankerRotated",
            "RoundChanged","HandFinished","GameEnded","FullState"
        })
        {
            var captured = name;
            conn.On<object>(captured, payload => Recorded.Add(new RecordedEvent(captured, payload)));
        }

        await conn.StartAsync();
        _connections.Add(conn);
        return conn;
    }

    public IEnumerable<RecordedEvent> EventsOfType(string type) =>
        Recorded.Where(e => e.Type == type);

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
        Factory.Dispose();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }
}

internal sealed record RecordedEvent(string Type, object? Payload);
