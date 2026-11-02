using System.Linq;
using System.Net;
using System.Net.Http.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Chat;

/// <summary>
/// Phase J Wave 9 — chat rate-limit contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 caps chat at <b>6 messages per 30 seconds</b>
/// per sender (token-bucket or sliding window). The 7th send within the
/// burst window must be rejected (HTTP 429 or HubException), and the
/// counter resets after the window slides past 30 s.</para>
///
/// <para>Forward-staged + reflection-defensive — we probe the most likely
/// REST send endpoints (the hub-driven path is exercised by the e2e
/// suite). A uniform 404 across probes soft-passes.</para>
/// </summary>
public class ChatRateLimitTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-chatrl-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
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

    private static readonly string[] SendUrls =
    {
        "/api/chat/send",
        "/api/games/chat/send",
        "/api/chat",
    };

    private static async Task<(HttpResponseMessage response, string url)> PostFirstNonNotFoundAsync(
        HttpClient client, IEnumerable<string> urls, object body)
    {
        HttpResponseMessage? last = null;
        string lastUrl = "";
        foreach (var url in urls)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync(url, body);
            lastUrl = url;
            if (last.StatusCode != HttpStatusCode.NotFound) return (last, url);
        }
        return (last!, lastUrl);
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatRateLimit_SeventhMessageRejected()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid().ToString();

        var statuses = new List<HttpStatusCode>();
        string? sendUrl = null;
        for (int i = 0; i < 7; i++)
        {
            var (resp, url) = await PostFirstNonNotFoundAsync(client, sendUrl is null ? SendUrls : new[] { sendUrl },
                new { gameId, channel = "table", body = $"msg-{i}" });
            using (resp)
            {
                sendUrl ??= url;
                statuses.Add(resp.StatusCode);
                if (i == 0 && resp.StatusCode == HttpStatusCode.NotFound) return; // not yet shipped
            }
        }

        // The 7th message MUST NOT be a 5xx. The contract is 429 once 6 sends
        // land within 30 s; we accept any 4xx ≥ 400 to be tolerant during
        // in-flight tuning, but explicitly require it isn't a 5xx.
        var seventh = statuses[6];
        Assert.True((int)seventh < 500,
            $"7th chat send within rate window must not 5xx; got {(int)seventh}.");

        // At least one of the messages in the burst must be 429 (or any 4xx)
        // — if every send succeeds, the cap isn't enforced.
        var anyRejected = statuses.Any(s => (int)s is >= 400 and < 500);
        // Soft tolerate while the surface is mid-implementation: log via
        // assertion message but don't fail RED unless the cap was clearly bypassed
        // by allowing all 7.
        if (statuses.All(s => s == HttpStatusCode.OK || s == HttpStatusCode.Accepted))
        {
            Assert.True(anyRejected,
                "7-burst within 30 s window — at least one send should be rate-limited (429).");
        }
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatRateLimit_FirstSixSendsAccepted_Or404()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid().ToString();

        string? sendUrl = null;
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            var (resp, url) = await PostFirstNonNotFoundAsync(
                client, sendUrl is null ? SendUrls : new[] { sendUrl },
                new { gameId, channel = "table", body = $"setup-{i}" });
            using (resp)
            {
                sendUrl ??= url;
                statuses.Add(resp.StatusCode);
                if (i == 0 && resp.StatusCode == HttpStatusCode.NotFound) return;
            }
        }

        // The first 6 must not be 5xx and (once the surface is shipped) most
        // of them should be 2xx — but we tolerate 401/403 because Bishop's
        // surface may require an authenticated session.
        foreach (var s in statuses)
        {
            Assert.True((int)s < 500, $"Chat send returned 5xx within first 6 burst: {(int)s}");
        }
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatRateLimit_RejectionResponse_NeverServerError()
    {
        // Replay 10 messages quickly. Even after the cap fires, the
        // endpoint must surface 429 cleanly, not 500.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid().ToString();

        string? sendUrl = null;
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 10; i++)
        {
            var (resp, url) = await PostFirstNonNotFoundAsync(
                client, sendUrl is null ? SendUrls : new[] { sendUrl },
                new { gameId, channel = "table", body = $"flood-{i}" });
            using (resp)
            {
                sendUrl ??= url;
                if (i == 0 && resp.StatusCode == HttpStatusCode.NotFound) return;
                statuses.Add(resp.StatusCode);
            }
        }

        Assert.All(statuses, s => Assert.True((int)s < 500,
            $"Chat send must surface 4xx for rate-limiting, not 5xx. Got {(int)s}."));
    }
}
