using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 6 — Bishop. SignalR hub that ships the SFU
/// (Selective Forwarding Unit) signalling surface for spectator
/// voice fan-out. The existing peer-mesh <see cref="VoiceHub"/>
/// scales to ~4 seated peers per table (every peer connects to
/// every other) — once spectator counts pass that boundary the
/// mesh's O(n²) connection growth crushes both client CPU and
/// table-side egress bandwidth. The SFU model fixes this by
/// terminating the dealer's outbound stream at a server endpoint
/// and re-broadcasting it to every spectator over a single
/// receive-only peer connection.
///
/// <para><b>Wave-6 scope:</b> SURFACE-ONLY. This hub stubs the
/// envelope shape — <see cref="JoinSpectatorVoice"/> returns the
/// canonical <c>{ ok, sfuEndpoint, peerId }</c> result with a
/// deterministic stub endpoint so the frontend can wire the
/// "spectator voice" path against a stable contract today. The
/// actual SFU pipeline (Janus, mediasoup, LiveKit, etc.) lands in
/// Phase L; sizing requirements are documented in
/// <c>docs/voice-sfu-design.md</c>.</para>
///
/// <para>The hub is intentionally distinct from <see cref="VoiceHub"/>
/// (peer-mesh signalling). Spectator voice is a one-way channel —
/// spectators only RECEIVE the dealer's narration; they don't
/// originate audio back. The two hubs share no transport state
/// because their topologies are incompatible (full-mesh vs.
/// star-fan-out).</para>
/// </summary>
public class SpectatorVoiceHub : Hub
{
    private readonly VoiceOptions _options;
    private readonly PlayerIdentityService _identity;
    private readonly ILogger<SpectatorVoiceHub> _logger;

    public SpectatorVoiceHub(
        IOptions<VoiceOptions> options,
        PlayerIdentityService identity,
        ILogger<SpectatorVoiceHub> logger)
    {
        _options = options.Value;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>
    /// Hub-side stub for the spectator-voice join handshake. Returns
    /// the canonical <see cref="SpectatorVoiceJoinResult"/> envelope
    /// containing the SFU endpoint URI + the per-spectator peer id
    /// the SFU will use to address fan-out frames.
    /// <para>The Wave-6 implementation returns a deterministic stub
    /// URI (<c>sfu://stub/&lt;tableId&gt;</c>) so the contract surface
    /// is testable end-to-end without the production SFU wiring.
    /// Phase L replaces the URI with a real Janus / mediasoup /
    /// LiveKit endpoint discovered from <see cref="VoiceOptions"/>.</para>
    /// </summary>
    public virtual Task<SpectatorVoiceJoinResult> JoinSpectatorVoice(string tableId)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(SpectatorVoiceJoinResult.Fail(
                VoiceHubResult.ReasonVoiceNotEnabled));
        }
        if (string.IsNullOrWhiteSpace(tableId))
        {
            return Task.FromResult(SpectatorVoiceJoinResult.Fail(
                VoiceHubResult.ReasonTargetNotFound));
        }

        var httpContext = Context.GetHttpContext();
        var anonId = httpContext is not null ? _identity.ResolveFromCookie(httpContext) : null;
        if (string.IsNullOrEmpty(anonId))
        {
            return Task.FromResult(SpectatorVoiceJoinResult.Fail(
                VoiceHubResult.ReasonUnauthorized));
        }

        // Phase K Wave 6 — deterministic stub URI. Real SFU sizing /
        // capacity discovery happens in Phase L; this surface returns
        // a per-table-stable endpoint so the frontend's connect path
        // resolves a non-empty value today.
        var peerId = Guid.NewGuid().ToString("N");
        var sfuEndpoint = $"sfu://stub/{tableId}";
        _logger.LogDebug(
            "Spectator voice join stubbed: tableId={TableId} anon={AnonId} peerId={PeerId}",
            tableId, anonId, peerId);

        return Task.FromResult(new SpectatorVoiceJoinResult(
            Ok: true,
            Reason: null,
            SfuEndpoint: sfuEndpoint,
            PeerId: peerId));
    }
}

/// <summary>
/// Phase K Wave 6 — Bishop. Result envelope for
/// <see cref="SpectatorVoiceHub.JoinSpectatorVoice"/>. Mirrors the
/// <see cref="VoiceHubResult"/> shape (Ok / Reason) with the added
/// SFU coordinates that the spectator client uses to open a
/// receive-only peer connection.
/// </summary>
public readonly record struct SpectatorVoiceJoinResult(
    bool Ok,
    string? Reason,
    string? SfuEndpoint,
    string? PeerId)
{
    public static SpectatorVoiceJoinResult Fail(string reason)
        => new(false, reason, null, null);
}
