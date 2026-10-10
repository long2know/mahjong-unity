using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase J Wave 6 — persistent player identity tests (Vasquez).
///
/// <para>Bishop's Wave-6 work decouples <c>PlayerId</c> from
/// <c>ConnectionId</c>: a long-lived <c>mahjong_pid</c> cookie minted via
/// <c>POST /api/identity</c> rides every subsequent SignalR negotiate /
/// autotable-WS upgrade handshake so the backend can resume a returning
/// browser's <see cref="PlayerProfile"/> + <see cref="PlayerStats"/>.
/// Vasquez's Wave-5 memo carryover #1 ("reconnect = new profile in v1")
/// is what this wave closes; these four facts pin the contract end-to-end.</para>
///
/// <para><b>Cookie shape (Bishop's memo):</b>
/// <code>
/// Set-Cookie: mahjong_pid=&lt;32-hex&gt;;
///             HttpOnly;
///             Secure;             (IsHttps only)
///             SameSite=Lax;
///             Max-Age=31536000;   (1 year)
///             Path=/
/// </code></para>
///
/// <para><b>Response shape (Bishop's memo):</b>
/// <code>
/// {
///   "playerId":    "&lt;32-hex&gt;",
///   "displayName": "Player-XXXXXX",
///   "avatarColor": "#RRGGBB",
///   "createdAt":   "2026-…Z",
///   "lastSeenAt":  "2026-…Z"
/// }
/// </code></para>
///
/// <para><b>Test strategy.</b> All four facts share a
/// <see cref="WebApplicationFactory{TEntryPoint}"/> per test instance with
/// the standard per-test temp-SQLite + snapshot-off ChangshaRuntime
/// configuration. Cookie persistence is asserted via
/// <see cref="HttpClientHandler.CookieContainer"/> (the
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateDefaultClient(System.Net.Http.DelegatingHandler[])"/>
/// path bypasses the static factory cookie strip). For the SignalR fact
/// we hand-craft the <c>Cookie</c> request header on the negotiate
/// handshake — Kestrel reads <c>Cookie</c> off the HTTP context exposed to
/// the hub via <c>HubCallerContext.GetHttpContext()</c>, which is what
/// <see cref="PlayerIdentityExtensions.GetPlayerId"/> consults when
/// <c>Context.Items["playerId"]</c> hasn't been populated yet.</para>
/// </summary>
public class PersistentPlayerIdTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-identity-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Development → RateLimiting:Enabled=false (Apone's gate), so
            // these identity tests aren't sharing a token bucket with the
            // Wave-6 RateLimitingTests when the suite runs in parallel.
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
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
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Cold POST /api/identity mints a fresh player + writes the cookie
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-6")]
    public async Task PostIdentity_NoCookie_MintsNewPlayer_AndSetsCookie()
    {
        // Bishop's contract: a request without the mahjong_pid cookie
        // results in a freshly-minted opaque playerId, a Set-Cookie header
        // pinning the value to the browser, and a 200 JSON body carrying
        // the full PlayerProfile shape (playerId / displayName /
        // avatarColor / createdAt / lastSeenAt). All five fields are
        // asserted so a future refactor can't silently drop one (the
        // frontend's `identity.ts:normalizeIdentity` keys off all of them).
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        // Strip any auto-attached cookie so this really is the no-cookie
        // path. CreateClient() returns a fresh HttpClient without persistent
        // cookies, but defence in depth — explicitly drop the header.
        client.DefaultRequestHeaders.Remove("Cookie");

        using var response = await client.PostAsync("/api/identity", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Set-Cookie must include mahjong_pid + the canonical attribute set.
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookieValues),
            "POST /api/identity must emit a Set-Cookie header on cookie-less calls.");
        var setCookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(
            $"{PlayerIdentityService.CookieName}=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(setCookie),
            $"Set-Cookie must carry {PlayerIdentityService.CookieName}=…; saw: " +
            string.Join(" | ", setCookieValues ?? Array.Empty<string>()));
        // Canonical attribute set (Bishop's memo). HttpOnly + SameSite=Lax
        // + Max-Age=31536000 + Path=/ are present on every mint/refresh.
        // (Secure depends on IsHttps; under TestServer the request is
        // plain HTTP so Secure is intentionally absent — we don't assert it.)
        Assert.Contains("httponly", setCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=31536000", setCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie!, StringComparison.OrdinalIgnoreCase);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Wire-shape contract — all five fields the frontend reads must
        // be present and well-typed. JsonValueKind checks catch contract
        // drift where the field name survives but the type flips.
        Assert.Equal(JsonValueKind.String, root.GetProperty("playerId").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("avatarColor").ValueKind);
        Assert.True(root.TryGetProperty("createdAt", out _),
            "POST /api/identity body must carry createdAt.");
        Assert.True(root.TryGetProperty("lastSeenAt", out _),
            "POST /api/identity body must carry lastSeenAt.");

        var playerId = root.GetProperty("playerId").GetString();
        Assert.False(string.IsNullOrEmpty(playerId), "playerId must be non-empty.");
        Assert.True(PlayerIdentityService.IsValidPlayerId(playerId),
            $"playerId '{playerId}' must satisfy IsValidPlayerId (URL-safe token, 1..128 chars).");

        // Burke — the cookie no longer restates the (PUBLIC) playerId: it carries a SIGNED
        // credential that VERIFIES BACK to the body's playerId. Equality would mean anyone who
        // read the id off the wire could replay it as their own cookie.
        var cookieValue = ExtractCookieValue(setCookie!, PlayerIdentityService.CookieName);
        Assert.NotEqual(playerId, cookieValue);
        Assert.StartsWith(PlayerIdentityTokenProtector.SchemePrefix + ".", cookieValue);
        var verdict = _factory!.Services
            .GetRequiredService<PlayerIdentityTokenProtector>().Unprotect(cookieValue);
        Assert.True(verdict.IsValid, "The issued cookie must verify against an active key.");
        Assert.Equal(playerId, verdict.PlayerId);

        // displayName/avatarColor sanity — the service defaults to the
        // PlayerProfileService.DefaultDisplayName / DefaultAvatarColor
        // shapes. We don't pin the exact value (those are FNV-1a hashed
        // off playerId), but the wire shapes are stable: 7-char
        // "Player-XXXXXX" name and 7-char "#RRGGBB" colour.
        var displayName = root.GetProperty("displayName").GetString() ?? string.Empty;
        Assert.StartsWith("Player-", displayName, StringComparison.Ordinal);
        var avatarColor = root.GetProperty("avatarColor").GetString() ?? string.Empty;
        Assert.Equal(7, avatarColor.Length);
        Assert.StartsWith("#", avatarColor, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Subsequent POST with the same cookie returns the same identity
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-6")]
    public async Task PostIdentity_WithExistingCookie_ReturnsSameProfile()
    {
        // Bishop's contract: when the caller already has a mahjong_pid
        // cookie on the jar, POST /api/identity is idempotent — the same
        // playerId/displayName/avatarColor come back, and the cookie is
        // refreshed (Max-Age slides forward) but its VALUE doesn't change.
        // This is the property that makes "browser reload = same career
        // stats" actually work. Without it, every reload would mint a new
        // identity and the stats panel would zero out.
        Assert.NotNull(_factory);

        // CookieContainer persists Set-Cookie responses across requests.
        // The factory's WebApplicationFactoryClientOptions has
        // HandleCookies=true by default, which would also do this, but
        // building our own client + container makes the assertion
        // unambiguous (we own the jar; nothing else mutates it).
        // We pass the cookie back manually rather than relying on
        // CookieContainer, because the TestServer host name `localhost`
        // is what RFC-6265-compliant containers may reject when the
        // Domain attribute is absent. Manually forwarding Set-Cookie →
        // Cookie is unambiguous: we read first.playerId, attach it as a
        // Cookie header on second, and assert second.playerId == first.
        using var client = _factory!.CreateClient();

        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/api/identity");
        using var first = await client.SendAsync(firstReq);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var firstId = JsonDocument.Parse(firstBody).RootElement
            .GetProperty("playerId").GetString();
        Assert.False(string.IsNullOrEmpty(firstId));

        // Burke — echo back the CREDENTIAL the server issued (not the public playerId, which
        // is no longer a valid cookie value).
        Assert.True(first.Headers.TryGetValues("Set-Cookie", out var firstSetCookie));
        var firstCookieValue = firstSetCookie!
            .Select(v => ExtractCookieValueOrNull(v, PlayerIdentityService.CookieName))
            .First(v => v is not null)!;

        // Same client, second POST with the Cookie header echoed.
        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/api/identity");
        secondReq.Headers.Add("Cookie", $"{PlayerIdentityService.CookieName}={firstCookieValue}");
        using var second = await client.SendAsync(secondReq);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondBody = await second.Content.ReadAsStringAsync();
        var secondDoc = JsonDocument.Parse(secondBody).RootElement;
        var secondId = secondDoc.GetProperty("playerId").GetString();
        Assert.Equal(firstId, secondId);

        // The Set-Cookie on the second response must echo the same id
        // (refreshed Max-Age, but identical value). Without this assertion
        // an "always mint and overwrite" regression would slip past
        // because second's body would still happen to use the request
        // cookie when the controller's order-of-operations is intact.
        Assert.True(second.Headers.TryGetValues("Set-Cookie", out var secondSetCookie),
            "POST /api/identity must rewrite the cookie on every call so Max-Age slides forward.");
        var secondCookieValue = secondSetCookie!
            .Select(v => ExtractCookieValueOrNull(v, PlayerIdentityService.CookieName))
            .FirstOrDefault(v => v is not null);
        // Deterministic under one key, so the refreshed credential is byte-identical AND still
        // verifies to the same identity.
        Assert.Equal(firstCookieValue, secondCookieValue);
        Assert.Equal(firstId, _factory!.Services
            .GetRequiredService<PlayerIdentityTokenProtector>().Unprotect(secondCookieValue).PlayerId);

        // The displayName + avatarColor are deterministic functions of
        // playerId (FNV-1a hashed), so cookie-stable id ⇒ stable name/colour.
        // Pinning these guards against an accidental "regenerate defaults
        // on every refresh" regression.
        Assert.Equal(
            JsonDocument.Parse(firstBody).RootElement.GetProperty("displayName").GetString(),
            secondDoc.GetProperty("displayName").GetString());
        Assert.Equal(
            JsonDocument.Parse(firstBody).RootElement.GetProperty("avatarColor").GetString(),
            secondDoc.GetProperty("avatarColor").GetString());
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. SignalR hub reads playerId from the cookie on the upgrade handshake
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-6")]
    public async Task HubConnection_ReadsPlayerIdFromCookie()
    {
        // Bishop's contract: ChangshaHub.OnConnectedAsync resolves the
        // mahjong_pid cookie from the negotiate handshake and stashes the
        // result on Context.Items["playerId"]. From then on every RPC
        // (CreateGame, TakeSeat, …) reads Context.GetPlayerId() to bind
        // the seat to the persistent identity rather than the volatile
        // ConnectionId. The observable side-effect we assert on is the
        // ProfileLoaded broadcast: it's sent to the caller immediately on
        // connect and carries the resolved playerId in its payload. If the
        // hub misses the cookie, the playerId would be a fresh server-side
        // mint (Bishop's defensive fallback) and would NOT match the
        // cookie value we set on the negotiate request.
        //
        // We use a LongPolling-only transport because the in-memory
        // TestServer's WebSocket support is brittle and HttpRequestHeaders
        // pre-populated on the HttpConnectionOptions ride the long-poll
        // handshake the same way they would the WS upgrade.
        Assert.NotNull(_factory);
        var expectedPlayerId = Guid.NewGuid().ToString("N");
        var hubBase = _factory!.Server.BaseAddress;

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(hubBase, "hubs/changsha"), opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory!.Server.CreateHandler();
                opts.WebSocketFactory = (_, _) => throw new InvalidOperationException(
                    "TestServer does not support WS upgrade in this assembly; use LongPolling.");
                opts.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                // Burke — the handshake must carry the SIGNED credential; a bare player id is
                // rejected (it is public) and the hub would mint a fresh identity instead.
                opts.Headers.Add("Cookie", SignedIdentityCookieHeader(expectedPlayerId));
            })
            .Build();

        // ProfileLoaded is the canonical Wave-5 lifecycle broadcast; Wave-6
        // re-uses it keyed by the persistent playerId rather than ConnectionId.
        // Bishop's BuildProfileDto echoes the resolved playerId verbatim.
        var profileLoaded = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ProfileLoaded", payload => profileLoaded.TrySetResult(payload));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var dto = await profileLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var hubPlayerId = dto.GetProperty("playerId").GetString();
        Assert.Equal(expectedPlayerId, hubPlayerId);

        await connection.StopAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Disconnect + reconnect with the same cookie restores the profile
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-6")]
    public async Task ReconnectAfterDisconnect_PreservesProfile()
    {
        // Bishop's contract: a player's mahjong_pid is the stable key for
        // PlayerProfile + PlayerStats. Closing the browser tab (or just
        // bouncing the SignalR connection) and reopening with the same
        // cookie must resume the same profile row — same displayName,
        // same avatarColor — without any client-side state. Phase J Wave 5
        // shipped reconnect that reset the profile every time (because the
        // SignalR ConnectionId rolled forward); Wave-6 closes that
        // regression. This test pins the closure.
        //
        // We pick a deterministic playerId, customise the profile via the
        // PlayerProfileService directly (mirrors how the UpdateProfile RPC
        // would have written through), then open + close + re-open the hub
        // with the same cookie and assert ProfileLoaded carries the
        // customised values.
        Assert.NotNull(_factory);
        var playerId = Guid.NewGuid().ToString("N");
        var customName = "Vasquez-W6";
        var customColor = "#43A047";

        // Seed the profile out-of-band so we don't depend on the
        // UpdateProfile RPC for this round-trip — that's covered by
        // PlayerProfileServiceTests.cs.
        var service = _factory!.Services.GetRequiredService<PlayerProfileService>();
        await service.GetOrCreateAsync(playerId);
        await service.UpdateDisplayNameAsync(playerId, customName);
        await service.UpdateAvatarColorAsync(playerId, customColor);

        var hubUri = new Uri(_factory!.Server.BaseAddress, "hubs/changsha");

        // First connect — should see the customised profile.
        var firstDto = await ConnectAndCaptureProfileAsync(hubUri, playerId);
        Assert.Equal(playerId, firstDto.GetProperty("playerId").GetString());
        Assert.Equal(customName, firstDto.GetProperty("displayName").GetString());
        Assert.Equal(customColor, firstDto.GetProperty("avatarColor").GetString());

        // Simulated disconnect + reconnect: the previous connection is
        // disposed (the helper does this in the using-scope), so the hub
        // sees a fresh ConnectionId on attempt #2. With the same cookie,
        // the resolved playerId must be unchanged, and the customised
        // profile must come back.
        var secondDto = await ConnectAndCaptureProfileAsync(hubUri, playerId);
        Assert.Equal(playerId, secondDto.GetProperty("playerId").GetString());
        Assert.Equal(customName, secondDto.GetProperty("displayName").GetString());
        Assert.Equal(customColor, secondDto.GetProperty("avatarColor").GetString());

        // Stats DTO is nested under `stats` (Wave-5 shape, Wave-6 same).
        // GamesPlayed should still be zero (we never completed a game) —
        // a non-zero value would mean RecordGameCompletedAsync somehow
        // fired on this isolated test (regression detection).
        Assert.Equal(0, secondDto.GetProperty("stats").GetProperty("gamesPlayed").GetInt32());
        Assert.Equal(0, secondDto.GetProperty("stats").GetProperty("gamesWon").GetInt32());
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> ConnectAndCaptureProfileAsync(Uri hubUri, string playerId)
    {
        Assert.NotNull(_factory);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUri, opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory!.Server.CreateHandler();
                opts.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                opts.Headers.Add("Cookie", SignedIdentityCookieHeader(playerId));
            })
            .Build();

        var profileLoaded = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ProfileLoaded", payload => profileLoaded.TrySetResult(payload));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var dto = await profileLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await connection.StopAsync();
        return dto;
    }

    /// <summary>
    /// Builds the <c>Cookie</c> header carrying a legitimately SIGNED identity credential for
    /// <paramref name="playerId"/>, minted through the host's own service.
    /// </summary>
    private string SignedIdentityCookieHeader(string playerId) =>
        $"{PlayerIdentityService.CookieName}="
        + _factory!.Services.GetRequiredService<PlayerIdentityService>().Protect(playerId);

    /// <summary>
    /// Extracts the <paramref name="cookieName"/> value from a raw
    /// <c>Set-Cookie</c> header. Tolerant of any attribute order; matches
    /// up to the first <c>;</c> after the name=value pair.
    /// </summary>
    private static string ExtractCookieValue(string setCookie, string cookieName)
    {
        var v = ExtractCookieValueOrNull(setCookie, cookieName);
        Assert.NotNull(v);
        return v!;
    }

    private static string? ExtractCookieValueOrNull(string setCookie, string cookieName)
    {
        var prefix = cookieName + "=";
        var start = setCookie.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = setCookie.IndexOf(';', start);
        return end < 0 ? setCookie[start..] : setCookie[start..end];
    }
}
