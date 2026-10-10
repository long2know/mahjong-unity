using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 8 — Bishop. Production
/// <see cref="SpectatorVoiceHub"/> variant that integrates with the
/// Janus Gateway HTTP API. The W6 surface returned a synthetic
/// <c>sfu://stub/&lt;tableId&gt;</c> placeholder; W8 returns a real
/// Janus session id + handle id + per-table mountpoint id so the
/// spectator client can open a Janus streaming-plugin subscription.
///
/// <list type="bullet">
///   <item><c>POST {endpoint}</c> with body
///         <c>{ janus: "create", transaction: &lt;tid&gt; }</c> mints
///         a Janus session id.</item>
///   <item><c>POST {endpoint}/{sessionId}</c> with body
///         <c>{ janus: "attach", plugin: "janus.plugin.streaming", transaction: &lt;tid&gt; }</c>
///         mints a per-spectator handle id.</item>
///   <item>The mountpoint id is a deterministic hash of the
///         supplied table id so every spectator on the same table
///         hits the same Janus mountpoint without an extra
///         coordination call.</item>
/// </list>
///
/// <para>The hub falls back to the deterministic W6 stub envelope
/// when the Janus call fails (timeout, 5xx, parse error) so a
/// transient Janus outage doesn't black-hole the spectator join.
/// The <see cref="SpectatorVoiceJoinResult.SfuEndpoint"/> field
/// stays populated; downstream metrics flag the fall-through via
/// the <c>peerId</c> prefix.</para>
/// </summary>
public sealed class JanusSpectatorVoiceHub : SpectatorVoiceHub
{
    public const string StubPeerIdPrefix = "stub-fallback-";

    private readonly VoiceOptions _options;
    private readonly PlayerIdentityService _identity;
    private readonly ILogger<JanusSpectatorVoiceHub> _logger;
    private readonly HttpClient _http;

    public JanusSpectatorVoiceHub(
        IOptions<VoiceOptions> options,
        PlayerIdentityService identity,
        ILogger<JanusSpectatorVoiceHub> logger,
        HttpClient? http = null)
        : base(options, identity, ResolveBaseLogger(logger))
    {
        _options = options.Value;
        _identity = identity;
        _logger = logger;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static ILogger<SpectatorVoiceHub> ResolveBaseLogger(ILogger<JanusSpectatorVoiceHub> logger)
    {
        // Adapter — re-uses the same underlying ILogger instance so
        // structured logging propagates upward through the base type.
        return new LoggerAdapter(logger);
    }

    /// <summary>
    /// Janus override — performs the create-session + attach-plugin
    /// HTTP exchange and returns the result envelope. On any error
    /// falls back to the parent stub envelope so the client UX
    /// degrades gracefully.
    /// </summary>
    public override async Task<SpectatorVoiceJoinResult> JoinSpectatorVoice(string tableId)
    {
        if (!_options.Enabled)
            return SpectatorVoiceJoinResult.Fail(VoiceHubResult.ReasonVoiceNotEnabled);
        if (string.IsNullOrWhiteSpace(tableId))
            return SpectatorVoiceJoinResult.Fail(VoiceHubResult.ReasonTargetNotFound);

        var httpContext = Context.GetHttpContext();
        var anonId = httpContext is not null ? _identity.ResolveFromCookie(httpContext) : null;
        if (string.IsNullOrEmpty(anonId))
            return SpectatorVoiceJoinResult.Fail(VoiceHubResult.ReasonUnauthorized);

        try
        {
            return await OpenJanusSessionAsync(tableId, anonId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Janus session open failed for tableId={TableId}; falling back to stub envelope.",
                tableId);
            return BuildStubFallback(tableId);
        }
    }

    private async Task<SpectatorVoiceJoinResult> OpenJanusSessionAsync(string tableId, string anonId)
    {
        var endpoint = (_options.JanusEndpoint ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(endpoint))
            return BuildStubFallback(tableId);

        // ── Step 1: create session.
        var sessionId = await CreateSessionAsync(endpoint);
        if (sessionId is null) return BuildStubFallback(tableId);

        // ── Step 2: attach streaming-plugin handle.
        var handleId = await AttachStreamingPluginAsync(endpoint, sessionId.Value);
        if (handleId is null) return BuildStubFallback(tableId);

        // Mountpoint id — deterministic per table so every spectator
        // on the table converges on the same Janus mountpoint
        // without an extra RPC.
        var mountpointId = ComputeMountpointId(tableId);

        var sfuEndpoint = $"janus:{endpoint}/{sessionId}/{handleId}/{mountpointId}";
        var peerId = $"janus-{sessionId}-{handleId}";

        _logger.LogDebug(
            "Janus spectator-voice session opened: tableId={TableId} sessionId={SessionId} handleId={HandleId} mp={MountpointId} anonId={AnonId}",
            tableId, sessionId, handleId, mountpointId, anonId);

        return new SpectatorVoiceJoinResult(
            Ok: true,
            Reason: null,
            SfuEndpoint: sfuEndpoint,
            PeerId: peerId);
    }

    private async Task<long?> CreateSessionAsync(string endpoint)
    {
        var req = new
        {
            janus = "create",
            transaction = NewTransactionId(),
        };
        using var resp = await _http.PostAsJsonAsync(endpoint, req);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync();
        return ExtractIdField(body);
    }

    private async Task<long?> AttachStreamingPluginAsync(string endpoint, long sessionId)
    {
        var req = new
        {
            janus = "attach",
            plugin = "janus.plugin.streaming",
            transaction = NewTransactionId(),
        };
        using var resp = await _http.PostAsJsonAsync($"{endpoint}/{sessionId}", req);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync();
        return ExtractIdField(body);
    }

    /// <summary>
    /// Parses the canonical Janus response envelope
    /// <c>{ janus: "success", data: { id: &lt;long&gt; }, ... }</c>
    /// and returns the inner id. Exposed internal so the test layer
    /// can verify the parsing without standing up a real Janus.
    /// </summary>
    internal static long? ExtractIdField(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("id", out var id))
                {
                    return id.ValueKind switch
                    {
                        JsonValueKind.Number => id.GetInt64(),
                        JsonValueKind.String when long.TryParse(id.GetString(), out var parsed) => parsed,
                        _ => null,
                    };
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deterministic 6-digit mountpoint id derived from the
    /// supplied table id. Same input → same output across processes
    /// so coordinated Janus mountpoint config stays trivial.
    /// </summary>
    internal static long ComputeMountpointId(string tableId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tableId ?? string.Empty));
        long acc = 0;
        for (var i = 0; i < 6 && i < hash.Length; i++)
        {
            acc = (acc << 8) | hash[i];
        }
        return Math.Abs(acc % 1_000_000);
    }

    private static string NewTransactionId() => Guid.NewGuid().ToString("N");

    private SpectatorVoiceJoinResult BuildStubFallback(string tableId)
    {
        var peerId = $"{StubPeerIdPrefix}{Guid.NewGuid():N}";
        return new SpectatorVoiceJoinResult(
            Ok: true,
            Reason: null,
            SfuEndpoint: $"sfu://stub/{tableId}",
            PeerId: peerId);
    }

    private sealed class LoggerAdapter : ILogger<SpectatorVoiceHub>
    {
        private readonly ILogger<JanusSpectatorVoiceHub> _inner;
        public LoggerAdapter(ILogger<JanusSpectatorVoiceHub> inner) { _inner = inner; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
