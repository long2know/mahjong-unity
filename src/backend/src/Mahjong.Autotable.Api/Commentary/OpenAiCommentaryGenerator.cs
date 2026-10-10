using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 8 — Bishop. Production <see cref="ICommentaryGenerator"/>
/// implementation that calls the OpenAI / Azure OpenAI Chat
/// Completions API to generate per-turn Mahjong commentary.
///
/// <list type="bullet">
///   <item>System prompt frames the model as a play-by-play
///         commentator and instructs it to return JSON-formatted
///         <see cref="CommentaryRecord"/> objects.</item>
///   <item>Per-turn streaming is exposed via
///         <see cref="StreamRecordsAsync"/> as an
///         <see cref="IAsyncEnumerable{CommentaryRecord}"/>.</item>
///   <item>Rate-limited at 1 generation per game per
///         <see cref="CommentaryOptions.RateLimitPerGameSeconds"/>
///         seconds; the second call within the window returns the
///         cached previous output unchanged.</item>
///   <item>Fail-open: any provider error (network timeout, 5xx,
///         JSON parse failure) returns a single
///         "[commentary unavailable]" record so the consumer never
///         sees a hard error.</item>
///   <item>Token usage is reported to
///         <see cref="ICommentaryUsageMeter"/> so operators can
///         track per-game + monthly consumption against the
///         configured cap.</item>
/// </list>
///
/// <para>The provider switch (OpenAI vs Azure) lives in
/// <see cref="CommentaryOptions.Provider"/> + the
/// <see cref="CommentaryOptions.Endpoint"/> base URI. Azure
/// endpoints already include the deployment path; OpenAI endpoints
/// terminate at the API root and we append
/// <c>/chat/completions</c>.</para>
/// </summary>
public sealed class OpenAiCommentaryGenerator : ICommentaryGenerator, IDisposable
{
    public const string FailOpenMessage = "[commentary unavailable]";
    public const string SystemPrompt =
        "You are a Mahjong play-by-play commentator. Given a turn " +
        "snapshot in JSON, return 1-3 CommentaryRecord objects as a " +
        "JSON array with fields: turnNumber (int), phase " +
        "(draw|discard|claim|win), speaker (play-by-play|color|analyst), " +
        "text (string), emotionIntensity (0.0..1.0), tileReferences " +
        "(string[]). Return ONLY the JSON array, no commentary outside it.";

    private readonly CommentaryOptions _options;
    private readonly HttpClient _http;
    private readonly ICommentaryUsageMeter _meter;
    private readonly ILogger<OpenAiCommentaryGenerator> _logger;
    private readonly ConcurrentDictionary<Guid, GenerationCacheEntry> _cache = new();
    private readonly bool _ownsHttpClient;

    public OpenAiCommentaryGenerator(
        IOptions<CommentaryOptions> options,
        ICommentaryUsageMeter meter,
        ILogger<OpenAiCommentaryGenerator> logger,
        HttpClient? http = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (http is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = http;
            _ownsHttpClient = false;
        }
    }

    /// <summary>Identifier surfaced via <c>CommentaryReplay.Generator</c>.
    /// Differentiates audit rows between providers.</summary>
    public string GeneratorId => string.Equals(_options.Provider, "Azure", StringComparison.OrdinalIgnoreCase)
        ? "azure-openai"
        : "openai";

    public async Task<CommentaryReplay> GenerateAsync(Guid gameId, CancellationToken ct = default)
    {
        var records = await GetRecordsAsyncInternal(gameId, regenerate: true, ct);
        return BuildEnvelope(gameId, records);
    }

    public async Task<CommentaryReplay> GetAsync(Guid gameId, CancellationToken ct = default)
    {
        var records = await GetRecordsAsyncInternal(gameId, regenerate: false, ct);
        return BuildEnvelope(gameId, records);
    }

    public async Task<IReadOnlyList<CommentaryRecord>> GetRecordsAsync(Guid gameId, CancellationToken ct = default) =>
        await GetRecordsAsyncInternal(gameId, regenerate: false, ct);

    /// <summary>
    /// Phase K Wave 8 — per-turn streaming surface. Yields records
    /// as the LLM returns them (or, on fail-open, yields a single
    /// "[commentary unavailable]" record). The current
    /// implementation aggregates the full response then yields the
    /// records one-by-one; a future Phase L iteration may stream
    /// the underlying SSE event stream directly.
    /// </summary>
    public async IAsyncEnumerable<CommentaryRecord> StreamRecordsAsync(
        Guid gameId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var records = await GetRecordsAsyncInternal(gameId, regenerate: true, ct);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    private async Task<IReadOnlyList<CommentaryRecord>> GetRecordsAsyncInternal(
        Guid gameId,
        bool regenerate,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(gameId, out var cached))
        {
            // If a regenerate call comes in inside the rate-limit
            // window, hand back the cached records (no fresh LLM hit).
            var since = now - cached.GeneratedAt;
            if (!regenerate
                || since.TotalSeconds < _options.RateLimitPerGameSeconds)
            {
                return cached.Records;
            }
        }

        if (_meter.ExceedsMonthlyCap(_options.MonthlyTokenCap, now.UtcDateTime))
        {
            _logger.LogWarning(
                "Monthly commentary token cap hit ({Cap}); returning fail-open record for gameId={GameId}",
                _options.MonthlyTokenCap, gameId);
            // Phase K Wave 9 — Bishop. When the operator opts into
            // hard 429s on cap-exceed, throw the
            // UsageCapExceededException so the controller can map
            // it to HTTP 429. Otherwise emit the fail-open envelope
            // (W8 contract).
            if (_options.ThrowOnMonthlyCap)
            {
                throw new UsageCapExceededException(
                    $"Monthly commentary token cap ({_options.MonthlyTokenCap}) exceeded.");
            }
            var capRecords = new[] { BuildFailOpenRecord(gameId, "monthly-token-cap") };
            _cache[gameId] = new GenerationCacheEntry(capRecords, now);
            return capRecords;
        }

        IReadOnlyList<CommentaryRecord> result;
        try
        {
            result = await CallLlmAsync(gameId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Commentary LLM call failed for gameId={GameId}; emitting fail-open record.", gameId);
            result = new[] { BuildFailOpenRecord(gameId, "llm-error") };
        }

        _cache[gameId] = new GenerationCacheEntry(result, now);
        return result;
    }

    private async Task<IReadOnlyList<CommentaryRecord>> CallLlmAsync(Guid gameId, CancellationToken ct)
    {
        var key = _options.ResolveApiKey();
        if (string.IsNullOrEmpty(key))
        {
            // Without an API key we can't call the provider — emit
            // fail-open and let operators see the missing-key kind
            // in the logs.
            _logger.LogWarning(
                "Commentary provider {Provider} has no API key configured; emitting fail-open record.",
                _options.Provider);
            return new[] { BuildFailOpenRecord(gameId, "missing-api-key") };
        }

        var url = BuildEndpointUrl();
        var body = BuildRequestBody(gameId);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (string.Equals(_options.Provider, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Add("api-key", key);
        }
        else
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var resp = await _http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Commentary provider returned non-success: status={Status} body={Body}",
                (int)resp.StatusCode, Truncate(respBody, 256));
            return new[] { BuildFailOpenRecord(gameId, $"http-{(int)resp.StatusCode}") };
        }

        return ParseProviderResponse(gameId, respBody);
    }

    private string BuildEndpointUrl()
    {
        var endpoint = (_options.Endpoint ?? string.Empty).TrimEnd('/');
        if (string.Equals(_options.Provider, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            // Azure endpoints carry the deployment in the path; we
            // just append the chat-completions sub-route and the
            // api-version query parameter the Azure shape requires.
            return $"{endpoint}/chat/completions?api-version=2024-02-15-preview";
        }
        return $"{endpoint}/chat/completions";
    }

    private string BuildRequestBody(Guid gameId)
    {
        // Wave-8 keeps the body minimal — the prompt carries the
        // schema; a real turn snapshot would be passed as the user
        // content. Phase L runtime hook will substitute the actual
        // turn JSON; for now we hand the LLM the game id so the
        // response is meaningful for smoke testing.
        var payload = new
        {
            model = _options.Model,
            temperature = 0.7,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = $"Game id: {gameId:N}. " +
                    "Generate a play-by-play burst for the latest turn." },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private IReadOnlyList<CommentaryRecord> ParseProviderResponse(Guid gameId, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage))
            {
                var prompt = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                var completion = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
                // Phase K Wave 9 — Bishop. Fire-and-forget the async
                // recorder so the EF-backed meter applies row-version
                // concurrency on the write. The sync RecordUsage path
                // remains for tests that wire the in-memory meter.
                _ = _meter.RecordUsageAsync(gameId, prompt, completion);
            }
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return new[] { BuildFailOpenRecord(gameId, "empty-choices") };
            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return new[] { BuildFailOpenRecord(gameId, "empty-content") };

            return ParseRecordsArray(gameId, content);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Commentary provider returned non-JSON or malformed body: {Body}",
                Truncate(body, 256));
            return new[] { BuildFailOpenRecord(gameId, "parse-error") };
        }
    }

    private static IReadOnlyList<CommentaryRecord> ParseRecordsArray(Guid gameId, string content)
    {
        // The LLM returns a JSON array of records; the content may
        // be wrapped in additional whitespace or Markdown fences in
        // some models. Trim to the first '[' and last ']' so the
        // happy path is robust to incidental formatting.
        var first = content.IndexOf('[');
        var last = content.LastIndexOf(']');
        if (first < 0 || last < 0 || last <= first)
            return new[] { BuildFailOpenRecord(gameId, "no-array-found") };
        var jsonSlice = content.Substring(first, last - first + 1);

        using var doc = JsonDocument.Parse(jsonSlice);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new[] { BuildFailOpenRecord(gameId, "expected-array") };

        var records = new List<CommentaryRecord>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            try
            {
                var turn = item.TryGetProperty("turnNumber", out var t) ? t.GetInt32() : 0;
                var phase = item.TryGetProperty("phase", out var ph) ? (ph.GetString() ?? "draw") : "draw";
                var speaker = item.TryGetProperty("speaker", out var sp) ? (sp.GetString() ?? "play-by-play") : "play-by-play";
                var text = item.TryGetProperty("text", out var tx) ? (tx.GetString() ?? string.Empty) : string.Empty;
                var intensity = item.TryGetProperty("emotionIntensity", out var ei) && ei.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(ei.GetDouble(), 0.0, 1.0)
                    : 0.5;
                var tiles = new List<TileReference>();
                if (item.TryGetProperty("tileReferences", out var tr) && tr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in tr.EnumerateArray())
                    {
                        // Phase K Wave 10 — Bishop. Accept both the
                        // legacy bare-string wire ("man5") AND the new
                        // typed shape ({tileId,suit,rank}). LLM prompt
                        // tuning aside, models occasionally regress to
                        // the older format mid-stream.
                        if (entry.ValueKind == JsonValueKind.String)
                        {
                            var parsed = TileReference.Parse(entry.GetString());
                            if (parsed != TileReference.Unknown) tiles.Add(parsed);
                        }
                        else if (entry.ValueKind == JsonValueKind.Object)
                        {
                            var id = entry.TryGetProperty("tileId", out var tid) ? tid.GetString() : null;
                            var parsed = TileReference.Parse(id);
                            if (parsed != TileReference.Unknown) tiles.Add(parsed);
                        }
                    }
                }
                if (!CommentaryPhases.All.Contains(phase)) phase = CommentaryPhases.Draw;
                if (!CommentarySpeakers.All.Contains(speaker)) speaker = CommentarySpeakers.PlayByPlay;
                records.Add(new CommentaryRecord(
                    GameId: gameId.ToString("N"),
                    TurnNumber: turn,
                    Phase: phase,
                    Speaker: speaker,
                    Text: text,
                    EmotionIntensity: intensity,
                    TileReferences: tiles,
                    GeneratedAt: DateTimeOffset.UtcNow));
            }
            catch
            {
                // Skip malformed records but keep the ones we
                // successfully parsed.
            }
        }
        if (records.Count == 0) return new[] { BuildFailOpenRecord(gameId, "no-valid-records") };
        return records;
    }

    private static CommentaryRecord BuildFailOpenRecord(Guid gameId, string reason) =>
        new(
            GameId: gameId.ToString("N"),
            TurnNumber: 0,
            Phase: CommentaryPhases.Draw,
            Speaker: CommentarySpeakers.PlayByPlay,
            Text: $"{FailOpenMessage} ({reason})",
            EmotionIntensity: 0.0,
            TileReferences: Array.Empty<TileReference>(),
            GeneratedAt: DateTimeOffset.UtcNow);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength);

    private CommentaryReplay BuildEnvelope(Guid gameId, IReadOnlyList<CommentaryRecord> records)
    {
        var items = records.Select((r, i) => new CommentaryItem(
            Sequence: i,
            Text: r.Text,
            RoundOrdinal: r.TurnNumber > 0 ? r.TurnNumber : null,
            Tone: r.Speaker)).ToArray();
        return new CommentaryReplay(
            GameId: gameId,
            Generator: GeneratorId,
            Status: items.Length > 0 && items[0].Text.StartsWith(FailOpenMessage, StringComparison.Ordinal)
                ? "fail-open"
                : "ok",
            Items: items);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private sealed record GenerationCacheEntry(IReadOnlyList<CommentaryRecord> Records, DateTimeOffset GeneratedAt);
}
