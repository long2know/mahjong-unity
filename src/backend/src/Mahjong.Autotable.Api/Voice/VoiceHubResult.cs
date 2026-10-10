namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 4 — Bishop. Typed return shape for the
/// <see cref="VoiceHub"/> RPC methods. Replaces the Wave-3
/// throw-<c>HubException</c> contract so SignalR clients see a
/// structured failure object instead of a string-coded exception
/// surfaced through the protocol's <c>invocationerror</c> envelope.
///
/// <para><b>Backwards compatibility.</b> The previous Wave-3 calls
/// threw <c>HubException</c> with a string code such as
/// <c>"voice-join-unauthorized"</c>. The new <see cref="Reason"/>
/// constants reuse those exact strings so client-side switch tables
/// continue to match. The <see cref="Ok"/> flag is the canonical
/// success signal — when true, <see cref="Reason"/> is null.</para>
/// </summary>
public readonly record struct VoiceHubResult(bool Ok, string? Reason)
{
    public static VoiceHubResult Success { get; } = new(true, null);

    public static VoiceHubResult Fail(string reason) => new(false, reason);

    /// <summary>Caller is not authenticated (no <c>mahjong_pid</c>
    /// cookie on the underlying HttpContext). Wave-3 wire-name
    /// preserved.</summary>
    public const string ReasonUnauthorized = "unauthorized";

    /// <summary>Per-table voice toggle is off
    /// (<c>ChangshaGame.VoiceEnabled == false</c>). Wave-3 wire-name
    /// preserved.</summary>
    public const string ReasonVoiceNotEnabled = "voice-not-enabled";

    /// <summary>Caller is not seated at the target table and is not
    /// the table creator. Wave-3 wire-name preserved.</summary>
    public const string ReasonNotSeated = "not-seated";

    /// <summary>Caller is a spectator (in-group but no seat). Reserved
    /// for the upcoming spectator-voice surface; today, spectators
    /// take the not-seated path.</summary>
    public const string ReasonSpectator = "spectator";

    /// <summary>Per-connection rate limiter rejected the relay.</summary>
    public const string ReasonRateLimited = "rate-limited";

    /// <summary>RelayOffer / RelayAnswer / RelayIceCandidate target
    /// connection id is empty or unknown.</summary>
    public const string ReasonTargetNotFound = "target-not-found";
}
