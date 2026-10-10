namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 6 — Bishop. Backend abstraction for the eventual
/// HLS recording pipeline that fans the per-table peer-mesh
/// <see cref="VoiceHub"/> audio stream into a CDN-distributable
/// <c>m3u8</c> playlist plus <c>.ts</c> segments.
///
/// <para>Wave 6 ships the surface contract — controller, audit
/// kinds, lifecycle — without the production ffmpeg/libwebrtc
/// wiring. The default implementation
/// (<see cref="InMemoryLivestreamRecorder"/>) holds state in
/// memory so the controller resolves cleanly in tests and a
/// dev host. The real encoder lands in Phase L; it slots in by
/// re-binding this interface in the DI container.</para>
///
/// <para>Thread-safety: implementations MUST be safe for
/// concurrent <see cref="StartAsync"/> / <see cref="StopAsync"/>
/// callers (operators may stop a stream while a separate request
/// is pulling the playlist). The in-memory default uses a
/// single coarse lock; production encoders pin per-stream
/// pipelines without contention against the playlist path.</para>
/// </summary>
public interface ILivestreamRecorder
{
    /// <summary>
    /// Begins HLS recording for <paramref name="gameId"/>. Idempotent
    /// — calling against a stream that is already live returns the
    /// existing <see cref="LivestreamHandle"/> instead of throwing,
    /// so a transient retry from the admin UI doesn't trip a
    /// "stream already started" error path.
    /// </summary>
    Task<LivestreamHandle> StartAsync(Guid gameId, string requestedByPlayerId, CancellationToken ct = default);

    /// <summary>
    /// Stops the HLS recording for <paramref name="gameId"/>. Returns
    /// the final <see cref="LivestreamHandle"/> (with
    /// <see cref="LivestreamHandle.Status"/> set to <c>stopped</c>)
    /// when the stream existed, null when no live stream matches
    /// the supplied id (idempotent stop is a no-op).
    /// </summary>
    Task<LivestreamHandle?> StopAsync(Guid gameId, string requestedByPlayerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the current HLS playlist body for
    /// <paramref name="gameId"/>, or null when no stream is live.
    /// The body is the canonical m3u8 text (per RFC 8216), suitable
    /// for direct response with <c>Content-Type: application/vnd.apple.mpegurl</c>.
    /// </summary>
    string? GetPlaylist(Guid gameId);

    /// <summary>
    /// Returns the bytes of the named <c>.ts</c> segment, or null
    /// when the segment is not registered for the supplied
    /// <paramref name="gameId"/>. Segment names match the entries
    /// in the m3u8 playlist returned by <see cref="GetPlaylist"/>.
    /// </summary>
    byte[]? GetSegment(Guid gameId, string segmentName);

    /// <summary>
    /// True when a livestream is currently active for
    /// <paramref name="gameId"/>. Used by the controller to short-
    /// circuit before reading the playlist body.
    /// </summary>
    bool IsLive(Guid gameId);
}

/// <summary>
/// Phase K Wave 6 — Bishop. Result envelope returned from the
/// <c>start</c> / <c>stop</c> endpoints. The shape stays small so the
/// admin UI can render the live state from a single fetch.
/// </summary>
public sealed record LivestreamHandle(
    Guid GameId,
    string Status,
    DateTime StartedAtUtc,
    DateTime? StoppedAtUtc,
    string StartedByPlayerId,
    string PlaylistUrl);
