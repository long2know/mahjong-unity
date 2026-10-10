namespace Mahjong.Autotable.Api.Spectator;

/// <summary>
/// Phase K Wave 2 — Bishop (Backend). Stub-grade spectator livestream
/// surface. The full HLS pipeline lands in Phase L (tile-flip → m3u8);
/// for Wave 2 this service exists so:
/// <list type="bullet">
///   <item>The <c>/api/replay/{id}/livestream.m3u8</c> endpoint has a
///         home for its 404 envelope without polluting Program.cs.</item>
///   <item>A future runtime hook (<c>OnTileFlipped</c>) can debounce at
///         30 Hz before flushing into the HLS encoder — we ship the
///         debouncer today so the seam exists.</item>
///   <item>Operators get a deterministic JSON shape ("not yet
///         implemented") rather than a generic 404 with empty body.</item>
/// </list>
/// </summary>
public sealed class SpectatorService
{
    /// <summary>Maximum frame rate the eventual HLS encoder will accept.
    /// 30 Hz keeps the encoder's GOP boundaries aligned with one
    /// frame-per-tile-flip on the client side.</summary>
    public const int MaxTileFlipsPerSecond = 30;

    private readonly TimeSpan _debounceWindow = TimeSpan.FromMilliseconds(1000 / MaxTileFlipsPerSecond);
    private DateTime _lastEmitUtc = DateTime.MinValue;
    private readonly object _gate = new();

    /// <summary>
    /// Returns the 404 envelope payload for the
    /// <c>/api/replay/{id}/livestream.m3u8</c> stub. The endpoint sets
    /// the HTTP status code; this helper just owns the shape so the
    /// route stays terse.
    /// </summary>
    public object NotImplementedEnvelope(string replayId) => new
    {
        error = "spectator-livestream-not-implemented",
        replayId,
        message = "HLS livestream lands in Phase L; this endpoint is reserved.",
    };

    /// <summary>
    /// Phase L seam: a tile-flip event arrived; should we forward it to
    /// the encoder right now, or coalesce into the next 33-ms window?
    /// Returns true when the caller should emit, false to drop. Phase K
    /// Wave 2 has no encoder so the result is informational — wired
    /// into a future <c>EmitTileFlippedAsync</c>.
    /// </summary>
    public bool ShouldEmitTileFlip()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastEmitUtc < _debounceWindow) return false;
            _lastEmitUtc = now;
            return true;
        }
    }
}
