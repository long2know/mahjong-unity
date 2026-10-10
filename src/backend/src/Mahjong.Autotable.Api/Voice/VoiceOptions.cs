namespace Mahjong.Autotable.Api.Voice;

// Phase K Wave 2 — Bishop (backend). Configuration knobs for the WebRTC
// voice signalling layer. Default `Enabled = false` keeps the feature
// opt-in per-deployment; table creators can flip individual rooms via
// the per-table toggle (Wave 3, ChangshaGame.VoiceEnabled).
// `MaxPeersPerTable = 4` enforces the mesh-only ceiling Vasquez's
// contract asserts. `TurnServers` lets operators pre-populate a
// STUN/TURN list returned by `/api/turn`.
//
// Phase K Wave 3 — Bishop. `TurnSharedSecret` enables the
// `/api/turn/credentials` HMAC-SHA1 minting endpoint (RFC 7635-style
// short-term TURN credentials). When unset, the credential endpoint
// returns 503 and the older anon `/api/turn` STUN-only path remains
// the only fallback. `TurnCredentialTtlSeconds` controls how long a
// minted credential is valid (default 1h).
public sealed class VoiceOptions
{
    public bool Enabled { get; set; } = false;
    public int MaxPeersPerTable { get; set; } = 4;
    public int RateLimitPerSecond { get; set; } = 30;
    public List<TurnServerOption> TurnServers { get; set; } = new();

    /// <summary>
    /// Phase K Wave 3 — shared secret used by the TURN server (e.g.
    /// coturn's <c>--static-auth-secret</c>) to validate
    /// <c>username = unix_ttl:playerId</c> + <c>credential =
    /// base64(HMAC-SHA1(secret, username))</c> short-term credentials.
    /// Leave null/empty to disable the mint endpoint.
    /// </summary>
    public string? TurnSharedSecret { get; set; }

    /// <summary>
    /// Phase K Wave 3 — TTL (seconds) applied to short-term TURN
    /// credentials minted by <c>POST /api/turn/credentials</c>. Default
    /// 3600 (1h) matches the coturn convention.
    /// </summary>
    public int TurnCredentialTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// Phase K Wave 7 — Bishop. Selects the
    /// <see cref="ILivestreamRecorder"/> implementation bound at
    /// startup. Two values supported:
    /// <list type="bullet">
    ///   <item><c>"InMemoryStub"</c> (default) — in-process
    ///         <see cref="InMemoryLivestreamRecorder"/>; matches the
    ///         Wave-6 default and keeps test harnesses /
    ///         dev hosts resolvable without ffmpeg installed.</item>
    ///   <item><c>"FfmpegHls"</c> — production
    ///         <see cref="FfmpegHlsRecorder"/> that spawns one
    ///         ffmpeg subprocess per livestream, writing HLS
    ///         segments + playlist to a per-game directory. The
    ///         host process fails fast at startup if the
    ///         <c>ffmpeg</c> binary is missing from PATH (see
    ///         <see cref="IFfmpegHealthProbe"/>).</item>
    /// </list>
    /// Value comparison is case-insensitive. Unknown values fall
    /// back to <c>InMemoryStub</c> with a startup warning.
    /// </summary>
    public string LivestreamRecorderImpl { get; set; } = "InMemoryStub";

    /// <summary>
    /// Phase K Wave 7 — Bishop. Per-segment duration (seconds)
    /// emitted by the ffmpeg HLS pipeline. Default <c>6</c> matches
    /// the HLS-spec recommended sweet-spot — long enough to absorb
    /// muxer overhead, short enough that a client waits at most
    /// ~6s for the first playable segment. Range: 2..30s; values
    /// outside the range are clamped at boot with a warning.
    /// </summary>
    public int LivestreamSegmentSeconds { get; set; } = 6;

    /// <summary>
    /// Phase K Wave 7 — Bishop. HLS playlist window (sliding
    /// segment count). Default <c>5</c> segments * 6s/segment =
    /// 30s playlist window, matching the Wave-7 brief.
    /// </summary>
    public int LivestreamPlaylistSegmentCount { get; set; } = 5;

    /// <summary>
    /// Phase K Wave 7 — Bishop. Filesystem directory where the
    /// ffmpeg HLS pipeline writes per-game playlists + segments.
    /// Each game id gets its own sub-directory so the streams
    /// stay process-isolated. Default
    /// <c>"./voice-livestream"</c> resolves under the host's
    /// content root; in k8s this is bound to an emptyDir volume.
    /// </summary>
    public string LivestreamWorkingDirectory { get; set; } = "voice-livestream";

    /// <summary>
    /// Phase K Wave 8 — Bishop. Selects the
    /// spectator-voice SFU backend bound at startup. Two values
    /// supported:
    /// <list type="bullet">
    ///   <item><c>"InMemoryStub"</c> (default) — deterministic
    ///         in-process stub matching the W6/W7 surface; the hub
    ///         returns a synthetic <c>sfu://stub/&lt;tableId&gt;</c>
    ///         endpoint so the test harness + dev hosts resolve
    ///         without a Janus install.</item>
    ///   <item><c>"Janus"</c> — production. The hub opens a Janus
    ///         session per spectator via the HTTP REST API at
    ///         <see cref="JanusEndpoint"/>, returns the mountpoint
    ///         id + Janus session creds. Health is probed via
    ///         <c>IJanusHealthProbe</c> at startup; an unreachable
    ///         Janus surface fails the host fast.</item>
    /// </list>
    /// Value comparison is case-insensitive. Unknown values fall
    /// back to <c>InMemoryStub</c> with a startup warning.
    /// </summary>
    public string SpectatorSfuImpl { get; set; } = "InMemoryStub";

    /// <summary>
    /// Phase K Wave 8 — Bishop. Janus Gateway HTTP API base URL
    /// (e.g. <c>"http://janus:8088/janus"</c>). Required when
    /// <see cref="SpectatorSfuImpl"/> is <c>"Janus"</c>; ignored on
    /// the stub.
    /// </summary>
    public string JanusEndpoint { get; set; } = string.Empty;
}

public sealed class TurnServerOption
{
    public string Url { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Credential { get; set; }
}
