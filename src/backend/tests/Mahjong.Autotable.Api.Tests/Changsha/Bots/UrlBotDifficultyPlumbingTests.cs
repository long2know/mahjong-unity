// Bishop W25 — autonomy + multi-game audit. Verifies the
// `?botDifficulty=` URL plumbing introduced in this wave: the autotable
// WS endpoint must forward the query-supplied difficulty into the
// runtime so that subsequent bot ticks dispatch on the requested
// strategy (Easy / Medium / Hard / Master) instead of the runtime-wide
// default (Medium). Pre-W25 the connection captured BotDifficulty but
// never invoked SetBotStrategyAsync, so every URL difficulty silently
// degraded to Medium — this suite is the regression bar.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Bots;

/// <summary>
/// Bishop W25 — exercises the `?botDifficulty=` query-string parameter
/// end-to-end: WS connect → relay→runtime binding → per-game strategy
/// override on <see cref="ChangshaGameInstance.BotStrategy"/>. The
/// spectator (<c>?seat=-1&amp;botCount=4</c>) code path is used because
/// it deterministically auto-binds the runtime game without requiring a
/// seat-take round-trip, which keeps the assertion focused on the
/// difficulty plumbing rather than the seating handshake.
///
/// <para>Asserts the strategy resolution rules documented on
/// <see cref="ChangshaBotEngine.Resolve"/>: case-insensitive match for
/// known difficulties, fall-back to Medium for unknown values, and a
/// distinct singleton per tier. Each test uses a unique relay gameId so
/// the underlying runtime games are isolated even though they share the
/// same <see cref="WebApplicationFactory{TEntryPoint}"/> host (which is
/// itself a regression guard for the multi-game-routing W25 audit).</para>
/// </summary>
public class UrlBotDifficultyPlumbingTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-urlbotdiff-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    // Drive every wait-state to its minimum so the
                    // spectator auto-deal hook completes inside the
                    // 2-second WaitFor budget without flaking under
                    // shared-runner contention.
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    [Theory, Trait("Category", "PhaseC-Relay"), Trait("Wave", "W25-Bishop")]
    [InlineData("Easy",   "easy")]
    [InlineData("Medium", "medium")]
    [InlineData("Hard",   "hard")]
    [InlineData("Master", "master")]
    public async Task UrlBotDifficulty_IsForwardedTo_RuntimeStrategy(string urlValue, string expectedDiscriminator)
    {
        var endpoint = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        var relayGameId = $"diff-{urlValue}-{Guid.NewGuid():N}";

        await using var spectator = await OpenSpectatorAsync(relayGameId, urlValue);

        // The spectator auto-deal hook needs to land the runtime binding
        // and call SetBotStrategyAsync. WaitFor polls so the test doesn't
        // race the async TryAutoDealForSpectatorAsync continuation.
        await WaitForAsync(() => endpoint.GetRuntimeGameIdBoundTo(relayGameId) is not null, timeoutMs: 2000);
        var runtimeGameId = endpoint.GetRuntimeGameIdBoundTo(relayGameId);
        Assert.NotNull(runtimeGameId);

        await WaitForAsync(() => runtime.GetActiveBotDifficulty(runtimeGameId!) == expectedDiscriminator, timeoutMs: 2000);

        var observed = runtime.GetActiveBotDifficulty(runtimeGameId!);
        Assert.Equal(expectedDiscriminator, observed);
    }

    [Theory, Trait("Category", "PhaseC-Relay"), Trait("Wave", "W25-Bishop")]
    [InlineData("EASY",     "easy")]
    [InlineData("HARD",     "hard")]
    [InlineData("MaStEr",   "master")]
    public async Task UrlBotDifficulty_IsCaseInsensitive(string urlValue, string expectedDiscriminator)
    {
        var endpoint = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        var relayGameId = $"diff-case-{Guid.NewGuid():N}";

        await using var spectator = await OpenSpectatorAsync(relayGameId, urlValue);

        await WaitForAsync(() => endpoint.GetRuntimeGameIdBoundTo(relayGameId) is not null, timeoutMs: 2000);
        var runtimeGameId = endpoint.GetRuntimeGameIdBoundTo(relayGameId);
        Assert.NotNull(runtimeGameId);

        await WaitForAsync(() => runtime.GetActiveBotDifficulty(runtimeGameId!) == expectedDiscriminator, timeoutMs: 2000);
        Assert.Equal(expectedDiscriminator, runtime.GetActiveBotDifficulty(runtimeGameId!));
    }

    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "W25-Bishop")]
    public async Task UrlBotDifficulty_UnknownValue_FallsBackToMedium()
    {
        // ChangshaBotEngine.Resolve returns the Medium strategy for any
        // value it doesn't recognise. This is intentional UX so a typo
        // in the URL doesn't crash the table — the W25 audit memo flags
        // this as a "soft fallback" that should remain observable.
        var endpoint = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        var relayGameId = $"diff-unknown-{Guid.NewGuid():N}";

        await using var spectator = await OpenSpectatorAsync(relayGameId, "GalaxyBrain");

        await WaitForAsync(() => endpoint.GetRuntimeGameIdBoundTo(relayGameId) is not null, timeoutMs: 2000);
        var runtimeGameId = endpoint.GetRuntimeGameIdBoundTo(relayGameId);
        Assert.NotNull(runtimeGameId);

        await WaitForAsync(() => runtime.GetActiveBotDifficulty(runtimeGameId!) == "medium", timeoutMs: 2000);
        Assert.Equal("medium", runtime.GetActiveBotDifficulty(runtimeGameId!));
    }

    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "W25-Bishop")]
    public async Task UrlBotDifficulty_TwoGames_AreIsolated()
    {
        // Multi-game isolation regression: spinning up two parallel
        // spectator games with different difficulties must NOT cross-bleed.
        // Pre-W25 the runtime had a single process-scoped _strategy field,
        // so binding game B to Hard would also flip game A to Hard. The
        // per-game BotStrategy override fix keeps the two readings stable.
        var endpoint = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();

        var gameEasy = $"diff-iso-easy-{Guid.NewGuid():N}";
        var gameMaster = $"diff-iso-master-{Guid.NewGuid():N}";

        await using var specEasy = await OpenSpectatorAsync(gameEasy, "Easy");
        await using var specMaster = await OpenSpectatorAsync(gameMaster, "Master");

        await WaitForAsync(() => endpoint.GetRuntimeGameIdBoundTo(gameEasy) is not null
                              && endpoint.GetRuntimeGameIdBoundTo(gameMaster) is not null,
            timeoutMs: 3000);

        var runtimeEasy = endpoint.GetRuntimeGameIdBoundTo(gameEasy)!;
        var runtimeMaster = endpoint.GetRuntimeGameIdBoundTo(gameMaster)!;
        Assert.NotEqual(runtimeEasy, runtimeMaster);

        await WaitForAsync(() => runtime.GetActiveBotDifficulty(runtimeEasy) == "easy"
                              && runtime.GetActiveBotDifficulty(runtimeMaster) == "master",
            timeoutMs: 3000);

        Assert.Equal("easy", runtime.GetActiveBotDifficulty(runtimeEasy));
        Assert.Equal("master", runtime.GetActiveBotDifficulty(runtimeMaster));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private async Task<SpectatorSession> OpenSpectatorAsync(string relayGameId, string botDifficulty)
    {
        // The spectator (seat=-1, botCount=4) flow auto-binds the runtime
        // game when JOIN lands, then auto-deals. We only need the binding
        // to land — the deal is asynchronous but the SetBotStrategyAsync
        // call happens BEFORE the deal so the test assertions race only
        // against the binding-lock window, not the deal itself.
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var uri = new Uri(server.BaseAddress,
            $"autotable/ws?seat=-1&botCount=4&botDifficulty={Uri.EscapeDataString(botDifficulty)}&gameId={Uri.EscapeDataString(relayGameId)}");
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var session = new SpectatorSession(ws);
        await session.SendJoinAsync(relayGameId);

        // Drain JOINED + initial UPDATE so the server-side handler has
        // finished TryAutoDealForSpectatorAsync (which is invoked from
        // HandleJoinAsync after sending the snapshot).
        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        _ = await session.ReadEnvelopeAsync(); // initial UPDATE

        return session;
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }

    private sealed class SpectatorSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public SpectatorSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var json = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<JsonElement> ReadEnvelopeAsync(int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None); }
                catch { }
            }
            _ws.Dispose();
        }
    }
}
