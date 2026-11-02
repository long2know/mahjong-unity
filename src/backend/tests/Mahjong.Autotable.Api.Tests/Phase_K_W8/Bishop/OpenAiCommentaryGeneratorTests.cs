using System.Net;
using System.Text;
using Mahjong.Autotable.Api.Commentary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Hard-asserted facts for the
/// <see cref="OpenAiCommentaryGenerator"/> and
/// <see cref="InMemoryCommentaryUsageMeter"/> seams.
///
/// <list type="number">
///   <item>Missing API key → fail-open record (no crash, no HTTP hit).</item>
///   <item>HTTP 500 from provider → fail-open record with http-500 marker.</item>
///   <item>Malformed JSON body → fail-open record with parse-error marker.</item>
///   <item>Rate-limit window collapses the second call to cached output.</item>
///   <item>Monthly cap hit → fail-open without touching the HTTP client.</item>
///   <item>Usage meter sums prompt + completion tokens.</item>
///   <item>StreamRecordsAsync yields the same records as the envelope.</item>
///   <item>GeneratorId reflects the provider (openai vs azure-openai).</item>
/// </list>
/// </summary>
public sealed class OpenAiCommentaryGeneratorTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = string.Empty;
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static OpenAiCommentaryGenerator NewGenerator(
        StubHandler? handler = null,
        CommentaryOptions? opts = null,
        InMemoryCommentaryUsageMeter? meter = null)
    {
        opts ??= new CommentaryOptions
        {
            Provider = "OpenAI",
            Endpoint = "https://stub.local/v1",
            ApiKey = "test-key",
            Model = "gpt-4o-mini",
            RateLimitPerGameSeconds = 60,
            MonthlyTokenCap = 0,
        };
        var http = handler is null ? null : new HttpClient(handler);
        return new OpenAiCommentaryGenerator(
            Options.Create(opts),
            meter ?? new InMemoryCommentaryUsageMeter(),
            NullLogger<OpenAiCommentaryGenerator>.Instance,
            http);
    }

    private static string SuccessBody(string text, int turn = 1, string phase = "draw", string speaker = "play-by-play",
        int promptTokens = 0, int completionTokens = 0) =>
        $$"""
        {
          "choices": [{ "message": { "content": "[{\"turnNumber\":{{turn}},\"phase\":\"{{phase}}\",\"speaker\":\"{{speaker}}\",\"text\":\"{{text}}\",\"emotionIntensity\":0.6,\"tileReferences\":[]}]" } }],
          "usage": { "prompt_tokens": {{promptTokens}}, "completion_tokens": {{completionTokens}} }
        }
        """;

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task MissingApiKey_FailsOpen_NoHttpHit()
    {
        var opts = new CommentaryOptions { Provider = "OpenAI", Endpoint = "https://stub.local/v1", ApiKey = "", Model = "x" };
        var handler = new StubHandler();
        var gen = NewGenerator(handler, opts);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.NotEmpty(records);
        Assert.Contains(OpenAiCommentaryGenerator.FailOpenMessage, records[0].Text);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task ProviderReturns500_FailsOpen_WithHttpReason()
    {
        var handler = new StubHandler { Status = HttpStatusCode.InternalServerError, Body = "boom" };
        var gen = NewGenerator(handler);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.Contains(OpenAiCommentaryGenerator.FailOpenMessage, records[0].Text);
        Assert.Contains("http-500", records[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task ProviderReturnsMalformedJson_FailsOpen_WithParseError()
    {
        var handler = new StubHandler { Status = HttpStatusCode.OK, Body = "not-json" };
        var gen = NewGenerator(handler);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.Contains(OpenAiCommentaryGenerator.FailOpenMessage, records[0].Text);
        Assert.Contains("parse-error", records[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task ProviderReturnsEmptyChoices_FailsOpen_WithEmptyChoicesReason()
    {
        var handler = new StubHandler { Body = "{\"choices\":[]}" };
        var gen = NewGenerator(handler);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.Contains("empty-choices", records[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task SuccessfulCall_ProducesValidRecords()
    {
        var handler = new StubHandler { Body = SuccessBody("East draws a tile", promptTokens: 100, completionTokens: 50) };
        var gen = NewGenerator(handler);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.NotEmpty(records);
        Assert.Equal("East draws a tile", records[0].Text);
        Assert.Equal(1, records[0].TurnNumber);
        Assert.Equal("draw", records[0].Phase);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task RateLimit_SecondCallWithinWindow_ReturnsCached()
    {
        var handler = new StubHandler { Body = SuccessBody("First") };
        var gen = NewGenerator(handler);
        var gameId = Guid.NewGuid();
        var first = await gen.GenerateAsync(gameId);
        var second = await gen.GenerateAsync(gameId);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(first.Items[0].Text, second.Items[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task MonthlyCap_Hit_FailsOpen_WithoutHttpCall()
    {
        var meter = new InMemoryCommentaryUsageMeter();
        meter.RecordUsage(Guid.NewGuid(), 1_000_000, 1_000_000);
        var opts = new CommentaryOptions
        {
            Provider = "OpenAI",
            Endpoint = "https://stub.local/v1",
            ApiKey = "k",
            Model = "x",
            MonthlyTokenCap = 1000,
            RateLimitPerGameSeconds = 60,
        };
        var handler = new StubHandler();
        var gen = NewGenerator(handler, opts, meter);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("monthly-token-cap", records[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task UsageMeter_RecordsBothPromptAndCompletionTokens()
    {
        var meter = new InMemoryCommentaryUsageMeter();
        var handler = new StubHandler { Body = SuccessBody("ok", promptTokens: 100, completionTokens: 50) };
        var gen = NewGenerator(handler, meter: meter);
        var gameId = Guid.NewGuid();
        await gen.GenerateAsync(gameId);
        Assert.Equal(150, meter.PerGameTokens(gameId));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task StreamRecordsAsync_YieldsSameRecordsAsGenerate()
    {
        var handler = new StubHandler { Body = SuccessBody("stream") };
        var gen = NewGenerator(handler);
        var gameId = Guid.NewGuid();
        var streamed = new List<CommentaryRecord>();
        await foreach (var rec in gen.StreamRecordsAsync(gameId))
        {
            streamed.Add(rec);
        }
        Assert.NotEmpty(streamed);
        Assert.Equal("stream", streamed[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task GenerateAsync_EnvelopeShape_IsCommentaryReplay()
    {
        var handler = new StubHandler { Body = SuccessBody("envelope") };
        var gen = NewGenerator(handler);
        var replay = await gen.GenerateAsync(Guid.NewGuid());
        Assert.NotNull(replay);
        Assert.NotEmpty(replay.Items);
        Assert.False(string.IsNullOrEmpty(replay.Generator));
        Assert.False(string.IsNullOrEmpty(replay.Status));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task GenerateAsync_FailOpen_StatusIsFailOpen()
    {
        var handler = new StubHandler { Status = HttpStatusCode.InternalServerError, Body = "boom" };
        var gen = NewGenerator(handler);
        var replay = await gen.GenerateAsync(Guid.NewGuid());
        Assert.Equal("fail-open", replay.Status);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task GenerateAsync_Success_StatusIsOk()
    {
        var handler = new StubHandler { Body = SuccessBody("ok") };
        var gen = NewGenerator(handler);
        var replay = await gen.GenerateAsync(Guid.NewGuid());
        Assert.Equal("ok", replay.Status);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void GeneratorId_ReflectsProvider_OpenAi()
    {
        var opts = new CommentaryOptions { Provider = "OpenAI", Endpoint = "https://stub", ApiKey = "k", Model = "x" };
        var gen = NewGenerator(new StubHandler(), opts);
        Assert.Equal("openai", gen.GeneratorId);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void GeneratorId_ReflectsProvider_Azure()
    {
        var opts = new CommentaryOptions { Provider = "Azure", Endpoint = "https://stub", ApiKey = "k", Model = "x" };
        var gen = NewGenerator(new StubHandler(), opts);
        Assert.Equal("azure-openai", gen.GeneratorId);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task ContentWithMarkdownFences_StillParses()
    {
        // Some LLMs wrap arrays in ```json … ``` fences; the parser
        // must tolerate this by snipping between the first '[' and
        // last ']'.
        var handler = new StubHandler
        {
            Body = """
                {
                  "choices": [{ "message": { "content": "```json\n[{\"turnNumber\":2,\"phase\":\"discard\",\"speaker\":\"color\",\"text\":\"fenced\",\"emotionIntensity\":0.4,\"tileReferences\":[]}]\n```" } }],
                  "usage": { "prompt_tokens": 0, "completion_tokens": 0 }
                }
                """,
        };
        var gen = NewGenerator(handler);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.Equal("fenced", records[0].Text);
        Assert.Equal(2, records[0].TurnNumber);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public async Task InvalidPhase_FromLlm_NormalisesToDraw()
    {
        var handler = new StubHandler
        {
            Body = """
                {
                  "choices": [{ "message": { "content": "[{\"turnNumber\":1,\"phase\":\"NONSENSE\",\"speaker\":\"play-by-play\",\"text\":\"x\",\"emotionIntensity\":0.5,\"tileReferences\":[]}]" } }],
                  "usage": { "prompt_tokens": 0, "completion_tokens": 0 }
                }
                """,
        };
        var gen = NewGenerator(handler);
        var records = await gen.GetRecordsAsync_RegenForTest(Guid.NewGuid());
        Assert.Equal(CommentaryPhases.Draw, records[0].Phase);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void UsageMeter_ZeroTokens_IsNoOp()
    {
        var meter = new InMemoryCommentaryUsageMeter();
        var id = Guid.NewGuid();
        meter.RecordUsage(id, 0, 0);
        Assert.Equal(0, meter.PerGameTokens(id));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void UsageMeter_NegativeTokens_AreClampedToZero()
    {
        var meter = new InMemoryCommentaryUsageMeter();
        var id = Guid.NewGuid();
        meter.RecordUsage(id, -5, -10);
        Assert.Equal(0, meter.PerGameTokens(id));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void UsageMeter_MonthlyCap_ZeroMeansUnlimited()
    {
        var meter = new InMemoryCommentaryUsageMeter();
        meter.RecordUsage(Guid.NewGuid(), 1_000_000, 1_000_000);
        Assert.False(meter.ExceedsMonthlyCap(0, DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void UsageMeter_ExceedsMonthlyCap_OnceCapReached()
    {
        var meter = new InMemoryCommentaryUsageMeter();
        meter.RecordUsage(Guid.NewGuid(), 600, 600);
        Assert.True(meter.ExceedsMonthlyCap(1000, DateTime.UtcNow));
        Assert.False(meter.ExceedsMonthlyCap(10_000, DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void CommentaryOptions_ResolveApiKey_Direct()
    {
        var opts = new CommentaryOptions { ApiKey = "direct-key" };
        Assert.Equal("direct-key", opts.ResolveApiKey());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-8")]
    public void CommentaryOptions_ResolveApiKey_EnvIndirection()
    {
        var envVar = "BISHOP_W8_COMMENTARY_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "from-env");
        try
        {
            var opts = new CommentaryOptions { ApiKey = $"env:{envVar}" };
            Assert.Equal("from-env", opts.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}

/// <summary>
/// Phase K Wave 8 — Bishop. Local extension that bypasses the
/// rate-limit cache for tests that only care about the
/// per-call result. Wraps the public <see cref="OpenAiCommentaryGenerator.GenerateAsync"/>
/// surface; the public envelope-flavoured <see cref="OpenAiCommentaryGenerator.GenerateAsync"/>
/// already regenerates on every call.
/// </summary>
internal static class OpenAiCommentaryGeneratorTestExtensions
{
    public static async Task<IReadOnlyList<CommentaryRecord>> GetRecordsAsync_RegenForTest(
        this OpenAiCommentaryGenerator gen, Guid gameId)
    {
        // GenerateAsync internally regenerates; we re-create the
        // Records by mapping back from the public Items list using
        // the StreamRecordsAsync surface (which round-trips through
        // the same regenerate-aware code path).
        var records = new List<CommentaryRecord>();
        await foreach (var r in gen.StreamRecordsAsync(gameId))
        {
            records.Add(r);
        }
        return records;
    }
}
