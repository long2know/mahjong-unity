namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 4 — Bishop. Stable metric-name constants for the
/// VoiceHub signalling surface. Vasquez's Wave-4 contract tests
/// assert these exact strings (the Prometheus exposition format is
/// allergic to renames — every dashboard, alert, and recording rule
/// pins the metric name). Keep this class as the single source of
/// truth; any future relay-class metric should land here too.
/// </summary>
public static class VoiceHubMetrics
{
    /// <summary>Total successful signalling relays
    /// (RelayOffer / RelayAnswer / RelayIceCandidate).</summary>
    public const string MetricRelayCount = "voice_relay_count_total";

    /// <summary>Total signalling relays rejected by the per-connection
    /// token-bucket rate limiter.</summary>
    public const string MetricRateLimitRejection = "voice_rate_limit_rejection_total";

    /// <summary>Total JoinVoice attempts rejected by the per-table auth
    /// gate (missing cookie, unseated player, voice disabled).</summary>
    public const string MetricJoinUnauthorized = "voice_join_unauthorized_total";

    // ────────────────────────────────────────────────────────────────────
    //  Phase K Wave 5 — Bishop. Stable `reason="…"` label values for
    //  the labeled Prometheus surfaces. New reasons MUST land here so
    //  the cardinality stays bounded and grep'able; arbitrary
    //  ad-hoc strings would break the contract Vasquez's W4/W5 tests
    //  pin against.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Fallback reason label when the rejection path did
    /// not pass a structured reason. Production code paths SHOULD
    /// always pass a specific reason; this constant exists so the
    /// label collapse never produces an empty string.</summary>
    public const string ReasonUnknown = "unknown";

    /// <summary>Reason label for the rate-limit-rejection surface —
    /// matches the SignalR <c>VoiceHubResult.ReasonRateLimited</c>
    /// wire name so a single dashboard query covers both.</summary>
    public const string ReasonRateLimited = "rate-limited";
}
