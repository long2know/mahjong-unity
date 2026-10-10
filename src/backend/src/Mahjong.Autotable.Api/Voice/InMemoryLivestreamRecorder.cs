using System.Collections.Concurrent;
using System.Text;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 6 — Bishop. In-memory <see cref="ILivestreamRecorder"/>
/// implementation that holds the current playlist + segment payloads
/// in process memory. Wave 6 ships the surface contract; the real
/// ffmpeg/libwebrtc encoder lands in Phase L and binds the same
/// interface.
///
/// <para>The stub mints a canonical <c>m3u8</c> body containing a
/// single placeholder segment (<c>stub-000.ts</c>) carrying the
/// literal bytes <c>"HLS-LIVESTREAM-STUB"</c>. That keeps the wire
/// shape testable end-to-end without standing up an encoder — the
/// controller resolves the placeholder, returns 200 with the
/// correct media type, and the contract tests can verify the body
/// surface.</para>
///
/// <para>The stub is intentionally non-blocking: <c>StartAsync</c>
/// returns synchronously after registering the handle, and segment
/// reads are pure dictionary lookups. Production encoders will
/// override this surface with a real frame pipeline.</para>
/// </summary>
public sealed class InMemoryLivestreamRecorder : ILivestreamRecorder
{
    private const string StubSegmentName = "stub-000.ts";
    private const string StubSegmentPayload = "HLS-LIVESTREAM-STUB";

    private readonly ConcurrentDictionary<Guid, LivestreamHandle> _live = new();

    public Task<LivestreamHandle> StartAsync(Guid gameId, string requestedByPlayerId, CancellationToken ct = default)
    {
        var handle = _live.AddOrUpdate(
            gameId,
            _ => new LivestreamHandle(
                GameId: gameId,
                Status: "live",
                StartedAtUtc: DateTime.UtcNow,
                StoppedAtUtc: null,
                StartedByPlayerId: requestedByPlayerId ?? string.Empty,
                PlaylistUrl: $"/api/voice/livestream/{gameId:N}/playlist.m3u8"),
            (_, existing) => existing.Status == "live"
                ? existing
                : existing with { Status = "live", StartedAtUtc = DateTime.UtcNow, StoppedAtUtc = null });
        return Task.FromResult(handle);
    }

    public Task<LivestreamHandle?> StopAsync(Guid gameId, string requestedByPlayerId, CancellationToken ct = default)
    {
        if (!_live.TryGetValue(gameId, out var current) || current.Status != "live")
        {
            return Task.FromResult<LivestreamHandle?>(null);
        }
        var stopped = current with { Status = "stopped", StoppedAtUtc = DateTime.UtcNow };
        _live[gameId] = stopped;
        return Task.FromResult<LivestreamHandle?>(stopped);
    }

    public string? GetPlaylist(Guid gameId)
    {
        if (!_live.TryGetValue(gameId, out var current) || current.Status != "live")
            return null;
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine("#EXT-X-TARGETDURATION:1");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        sb.AppendLine("#EXTINF:1.0,");
        sb.AppendLine(StubSegmentName);
        // No #EXT-X-ENDLIST — the stub stream is logically live until stopped.
        return sb.ToString();
    }

    public byte[]? GetSegment(Guid gameId, string segmentName)
    {
        if (!_live.TryGetValue(gameId, out var current) || current.Status != "live")
            return null;
        if (!string.Equals(segmentName, StubSegmentName, StringComparison.Ordinal))
            return null;
        return Encoding.UTF8.GetBytes(StubSegmentPayload);
    }

    public bool IsLive(Guid gameId)
        => _live.TryGetValue(gameId, out var current) && current.Status == "live";
}
