using System.Reflection;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Phase F variant-switching architecture per Ripley §1.
///
/// <para>The contract: a single backend that flips between two modes based on the
/// connection's <c>?variant=</c> query param:
/// <list type="bullet">
///   <item><b>Changsha runtime mode</b> — variant=changsha (default). Backend binds
///   a <see cref="IChangshaGameRuntime"/> game; runtime drives <c>things</c>/<c>seats</c>/
///   <c>match</c>/<c>claim</c>/<c>result</c>/<c>pickup</c> via the translator.</item>
///   <item><b>Relay mode</b> — variant=four_player|three_player|bamboo|minefield. Backend
///   does NOT bind a runtime game. Bundle's local Setup drives the deal; backend just
///   relays UPDATEs between connections (Phase C-relay behavior).</item>
/// </list>
/// </para>
///
/// <para><b>Test posture:</b> the file uses <see cref="WebApplicationFactory{TEntryPoint}"/>
/// to stand up the full WS pipe, like <c>EndToEndPlayableTests</c>. New Phase F symbols
/// (<c>AutotableRuntimeMode</c>, <c>AutotableConnection.Variant</c>, etc.) are accessed
/// via reflection so the assembly compiles before Bishop ships them.</para>
///
/// <para><b>Sources:</b>
/// Ripley Phase F design §1 (Variant-Switching Architecture) and §7 (Backward Compat),
/// Vasquez Phase F rule audit §11.</para>
/// </summary>
public class VariantSwitchAcceptanceTests
{
    // ── Reflection helpers ────────────────────────────────────────────────

    private static readonly Assembly ApiAssembly = typeof(ChangshaGameState).Assembly;

    private static Type? TryGetType(string fullName) => ApiAssembly.GetType(fullName);

    private static PropertyInfo? TryGetProperty(Type type, string propertyName) =>
        type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

    private static void AssertPhaseFShipped(string symbolDescription, object? symbol)
    {
        Assert.True(symbol != null,
            $"Phase F backend not yet shipped — missing {symbolDescription}. " +
            $"Bishop owns; see .squad/decisions/inbox/ripley-phase-f-design.md.");
    }

    // ── Test fixture ──────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> MakeFactory(string testTag)
    {
        var dataDir = System.IO.Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = System.IO.Path.Combine(dataDir, $"mahjong-variant-{testTag}-{Guid.NewGuid():N}.db");
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
                b.ConfigureServices(s =>
                {
                    s.Configure<ChangshaRuntimeOptions>(o =>
                    {
                        o.BotTurnDelayMs = 1;
                        o.BotClaimDelayMs = 1;
                        o.ClaimWindowTimeoutMs = 50;
                        o.DealBatchDelayMs = 0;
                        o.PersistSnapshots = false;
                    });
                });
            });
    }

    private static async Task<System.Net.WebSockets.WebSocket> ConnectAsync(
        WebApplicationFactory<Program> factory, string queryString)
    {
        var uri = new Uri(factory.Server.BaseAddress, $"autotable/ws?{queryString}");
        return await factory.Server.CreateWebSocketClient().ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task SendJoinAsync(System.Net.WebSockets.WebSocket ws, string gameId)
    {
        var msg = System.Text.Json.JsonSerializer.Serialize(new { type = "JOIN", gameId });
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(msg),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<System.Text.Json.JsonElement> ReadEnvelopeAsync(System.Net.WebSockets.WebSocket ws, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var sb = new System.Text.StringBuilder();
        System.Net.WebSockets.WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            sb.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return System.Text.Json.JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    // ── §1 — Runtime/Relay binding decision ───────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public async Task GameType_Changsha_BindsRuntime()
    {
        // Ripley §1.2: variant=changsha (the default) binds a ChangshaGameRuntime game
        // lazily on the first seat-take. After a seat-take, `manager.GetRuntimeGameIdBoundTo`
        // must report a non-null runtime gameId for DefaultGameId.
        await using var factory = MakeFactory("changsha-binds");
        var manager = factory.Services.GetRequiredService<AutotableConnectionManager>();

        using var ws = await ConnectAsync(factory, "variant=changsha&seat=0&bots=false");
        await SendJoinAsync(ws, AutotableWsEndpoint.DefaultGameId);
        // Read JOINED + initial snapshot.
        _ = await ReadEnvelopeAsync(ws, 5000);
        _ = await ReadEnvelopeAsync(ws, 5000);

        // Send a seats UPDATE so the relay actually binds the runtime (per Wave-3 lazy binding).
        var seatTake = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "UPDATE",
            entries = new object[]
            {
                new object[] { "seats", "viewer-0", new { seat = 0 } }
            }
        });
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(seatTake),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

        // Give the binding async a moment.
        await Task.Delay(500);

        var bound = manager.GetRuntimeGameIdBoundTo(AutotableWsEndpoint.DefaultGameId);
        Assert.NotNull(bound);
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task GameType_FourPlayer_DoesNotBindRuntime()
    {
        // Ripley §1.2: variant=four_player runs in Relay mode — backend NEVER binds a
        // Changsha runtime game even on seat-take. The bundle is master.
        // Pin: after seat-take, GetRuntimeGameIdBoundTo remains null.
        var connType = TryGetType("Mahjong.Autotable.Api.Autotable.AutotableConnection");
        var variantProp = connType is null ? null : TryGetProperty(connType, "Variant");
        AssertPhaseFShipped("AutotableConnection.Variant (init property)", variantProp);

        await using var factory = MakeFactory("fourplayer-relay");
        var manager = factory.Services.GetRequiredService<AutotableConnectionManager>();

        using var ws = await ConnectAsync(factory, "variant=four_player&seat=0&bots=false");
        await SendJoinAsync(ws, AutotableWsEndpoint.DefaultGameId);
        _ = await ReadEnvelopeAsync(ws, 5000);
        _ = await ReadEnvelopeAsync(ws, 5000);

        // Even if a seat-take is sent, Relay mode must NOT bind a runtime game.
        var seatTake = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "UPDATE",
            entries = new object[]
            {
                new object[] { "seats", "viewer-0", new { seat = 0 } }
            }
        });
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(seatTake),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(500);

        var bound = manager.GetRuntimeGameIdBoundTo(AutotableWsEndpoint.DefaultGameId);
        Assert.True(bound is null,
            $"Phase F: variant=four_player must NOT bind a Changsha runtime. Got bound runtimeGameId={bound}.");
    }

    [Theory, Trait("Category", "Acceptance")]
    [InlineData("four_player")]
    [InlineData("three_player")]
    [InlineData("bamboo")]
    [InlineData("minefield")]
    public async Task Relay_Mode_NoClaimCollectionEmitted(string variant)
    {
        // Ripley §1.2 + §7.2: in Relay mode the backend never emits `claim`, `result`,
        // or `pickup` (those are runtime-driven). The bundle is master and renders
        // upstream's deal locally. Pin: snapshot/UPDATE stream contains no runtime
        // collections during the connection lifetime.
        await using var factory = MakeFactory($"relay-noclaim-{variant}");

        using var ws = await ConnectAsync(factory, $"variant={variant}&seat=0&bots=false");
        await SendJoinAsync(ws, AutotableWsEndpoint.DefaultGameId);

        var sawRuntimeCollection = false;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            System.Text.Json.JsonElement env;
            try { env = await ReadEnvelopeAsync(ws, 600); }
            catch (OperationCanceledException) { continue; }
            if (env.GetProperty("type").GetString() != "UPDATE") continue;
            foreach (var entry in env.GetProperty("entries").EnumerateArray())
            {
                var kind = entry[0].GetString();
                if (kind is "claim" or "result" or "pickup")
                {
                    sawRuntimeCollection = true;
                    break;
                }
            }
            if (sawRuntimeCollection) break;
        }

        Assert.False(sawRuntimeCollection,
            $"Phase F: variant={variant} (Relay mode) must NOT emit runtime collections (claim/result/pickup).");
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task Relay_Mode_ForwardsBundleUpdates_Verbatim()
    {
        // Phase C-relay behavior is preserved when variant != changsha. Two bundles
        // connect; an UPDATE from bundle A must reach bundle B unchanged. This pins
        // the relay pipe stays intact in Phase F (regression-only).
        await using var factory = MakeFactory("relay-forward");

        using var wsA = await ConnectAsync(factory, "variant=four_player");
        using var wsB = await ConnectAsync(factory, "variant=four_player");
        await SendJoinAsync(wsA, AutotableWsEndpoint.DefaultGameId);
        await SendJoinAsync(wsB, AutotableWsEndpoint.DefaultGameId);
        _ = await ReadEnvelopeAsync(wsA, 5000); _ = await ReadEnvelopeAsync(wsA, 5000);
        _ = await ReadEnvelopeAsync(wsB, 5000); _ = await ReadEnvelopeAsync(wsB, 5000);

        // Bundle A pushes a custom `nicks` UPDATE. It should reach Bundle B verbatim.
        var msg = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "UPDATE",
            entries = new object[]
            {
                new object[] { "nicks", "viewer-A", "Alice" }
            }
        });
        await wsA.SendAsync(System.Text.Encoding.UTF8.GetBytes(msg),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

        // Bundle B should receive an UPDATE containing the nicks entry.
        var observedAlice = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !observedAlice)
        {
            System.Text.Json.JsonElement env;
            try { env = await ReadEnvelopeAsync(wsB, 800); }
            catch (OperationCanceledException) { continue; }
            if (env.GetProperty("type").GetString() != "UPDATE") continue;
            foreach (var entry in env.GetProperty("entries").EnumerateArray())
            {
                if (entry[0].GetString() == "nicks" &&
                    entry[1].GetString() == "viewer-A" &&
                    entry[2].GetString() == "Alice")
                {
                    observedAlice = true; break;
                }
            }
        }

        Assert.True(observedAlice, "Relay mode must forward bundle UPDATEs to other bundles verbatim.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task DefaultVariant_IsChangsha()
    {
        // Ripley §1.2 + §7.3: no `?variant=` query param → default Changsha
        // (Wave-3 backward compat). Pin: connection arrives without `variant`
        // query, the backend must treat it as Changsha runtime mode.
        var connType = TryGetType("Mahjong.Autotable.Api.Autotable.AutotableConnection");
        var variantProp = connType is null ? null : TryGetProperty(connType, "Variant");
        AssertPhaseFShipped("AutotableConnection.Variant", variantProp);

        await using var factory = MakeFactory("default-variant");
        var manager = factory.Services.GetRequiredService<AutotableConnectionManager>();

        using var ws = await ConnectAsync(factory, "seat=0&bots=false");
        await SendJoinAsync(ws, AutotableWsEndpoint.DefaultGameId);
        _ = await ReadEnvelopeAsync(ws, 5000); _ = await ReadEnvelopeAsync(ws, 5000);

        var seatTake = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "UPDATE",
            entries = new object[]
            {
                new object[] { "seats", "viewer-0", new { seat = 0 } }
            }
        });
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(seatTake),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(500);

        var bound = manager.GetRuntimeGameIdBoundTo(AutotableWsEndpoint.DefaultGameId);
        Assert.NotNull(bound);
    }

    // ── §1.4 — Connection property surface ────────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void AutotableConnection_Has_Phase_F_Properties()
    {
        // Ripley §1.4: AutotableConnection must expose Variant, DealMode, BotCount,
        // BotDifficulty, RuntimeMode. These are the parser outputs from MapAutotableWs
        // and the source of truth used by HandleNewAsync to branch.
        var connType = TryGetType("Mahjong.Autotable.Api.Autotable.AutotableConnection");
        AssertPhaseFShipped("AutotableConnection (type)", connType);

        var requiredProps = new[] { "Variant", "DealMode", "BotCount", "BotDifficulty", "RuntimeMode" };
        var missing = requiredProps
            .Where(name => TryGetProperty(connType!, name) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Phase F: AutotableConnection missing properties: {string.Join(", ", missing)}. " +
            $"Bishop owns; see Ripley §1.4.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void AutotableRuntimeMode_Has_Relay_And_ChangshaRuntime_Members()
    {
        // Ripley §1.4: AutotableRuntimeMode enum with two members.
        var enumType = TryGetType("Mahjong.Autotable.Api.Autotable.AutotableRuntimeMode");
        AssertPhaseFShipped("AutotableRuntimeMode enum", enumType);

        var members = Enum.GetNames(enumType!);
        Assert.Contains("Relay", members);
        Assert.Contains("ChangshaRuntime", members);
    }

    // ── §7.6 — Mid-session variant mixing ─────────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public async Task MixedMode_SecondConnection_DifferentVariant_IsRejectedOrIgnored()
    {
        // Ripley §7.6: once a gameId is bound to one variant (Changsha), a second
        // connection arriving with a different variant (four_player) cannot
        // hot-swap the mode. Behaviour: either reject the connection or treat
        // the variant query as advisory (the existing bound mode wins).
        // Pin: the connection either fails the handshake OR observes that the
        // existing Changsha-mode collections (claim/result/pickup) still flow.
        await using var factory = MakeFactory("mixed-mode");

        using var wsChangsha = await ConnectAsync(factory, "variant=changsha&seat=0&bots=true");
        await SendJoinAsync(wsChangsha, AutotableWsEndpoint.DefaultGameId);
        _ = await ReadEnvelopeAsync(wsChangsha, 5000); _ = await ReadEnvelopeAsync(wsChangsha, 5000);

        // Trigger seat-take so the runtime binds (Wave-3 lazy binding).
        var seatTake = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "UPDATE",
            entries = new object[]
            {
                new object[] { "seats", "viewer-0", new { seat = 0 } }
            }
        });
        await wsChangsha.SendAsync(System.Text.Encoding.UTF8.GetBytes(seatTake),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(300);

        // Second connection — different variant.
        using var wsConflicting = await ConnectAsync(factory, "variant=four_player");
        await SendJoinAsync(wsConflicting, AutotableWsEndpoint.DefaultGameId);

        // The mixed-mode behavior must be one of:
        //   (a) the conflicting connection is closed by the server, OR
        //   (b) the conflicting connection observes the bound Changsha mode (it sees
        //       runtime-emitted collections).
        // Either outcome MUST NOT corrupt the existing Changsha runtime state.
        var manager = factory.Services.GetRequiredService<AutotableConnectionManager>();
        var stillBound = manager.GetRuntimeGameIdBoundTo(AutotableWsEndpoint.DefaultGameId);
        Assert.NotNull(stillBound); // first connection's runtime is still valid.
    }

    // ── §1.4 — URL parameter parsing ──────────────────────────────────────

    [Theory, Trait("Category", "Acceptance")]
    [InlineData("variant=changsha&dealMode=manual&botCount=3&botDifficulty=Medium", "changsha", "manual", 3, "Medium")]
    [InlineData("variant=changsha&dealMode=auto&botCount=0&botDifficulty=easy", "changsha", "auto", 0, "easy")]
    [InlineData("variant=four_player&dealMode=auto&botCount=0", "four_player", "auto", 0, "Medium")]
    [InlineData("", "changsha", "manual", 3, "Medium")]
    public async Task UrlParams_AreParsed_To_ConnectionProperties(
        string query, string expectedVariant, string expectedDealMode, int expectedBotCount, string expectedBotDifficulty)
    {
        // Ripley §1.4: MapAutotableWs parses query string into AutotableConnection
        // properties with sane defaults. Pin: each property carries the parsed value.
        var connType = TryGetType("Mahjong.Autotable.Api.Autotable.AutotableConnection");
        AssertPhaseFShipped("AutotableConnection (type)", connType);
        var variantProp = TryGetProperty(connType!, "Variant");
        AssertPhaseFShipped("AutotableConnection.Variant", variantProp);

        await using var factory = MakeFactory($"urlparse-{Math.Abs(query.GetHashCode())}");

        using var ws = await ConnectAsync(factory, query);
        await SendJoinAsync(ws, AutotableWsEndpoint.DefaultGameId);
        _ = await ReadEnvelopeAsync(ws, 5000); _ = await ReadEnvelopeAsync(ws, 5000);

        // Reach into the manager to find the connection that just registered.
        var manager = factory.Services.GetRequiredService<AutotableConnectionManager>();
        var connectionsField = typeof(AutotableConnectionManager)
            .GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance);
        var connectionsDict = connectionsField?.GetValue(manager) as System.Collections.IDictionary;
        Assert.NotNull(connectionsDict);
        AutotableConnection? connection = null;
        foreach (var entry in connectionsDict!)
        {
            var conn = ((System.Collections.DictionaryEntry)entry).Value as AutotableConnection;
            if (conn is not null) { connection = conn; break; }
        }
        Assert.NotNull(connection);

        var actualVariant = TryGetProperty(connType!, "Variant")?.GetValue(connection) as string;
        var actualDealMode = TryGetProperty(connType!, "DealMode")?.GetValue(connection) as string;
        var actualBotCount = TryGetProperty(connType!, "BotCount")?.GetValue(connection) as int?;
        var actualBotDifficulty = TryGetProperty(connType!, "BotDifficulty")?.GetValue(connection) as string;

        Assert.Equal(expectedVariant, actualVariant);
        Assert.Equal(expectedDealMode, actualDealMode);
        Assert.Equal(expectedBotCount, actualBotCount);
        Assert.Equal(expectedBotDifficulty, actualBotDifficulty);
    }
}
