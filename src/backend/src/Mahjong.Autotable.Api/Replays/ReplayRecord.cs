using System.IO.Compression;
using System.Text;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 12 — Bishop. Canonical replay record persisted to the
/// <c>Replays</c> table. Hicks's W12 <c>?action=replay</c> client URL
/// fetches a replay by its synthetic <see cref="ReplayId"/> (a short
/// URL-safe string assigned at ingest time) which the
/// <see cref="ReplayController"/> resolves to a single row.
///
/// <para>The <see cref="CompressedPayload"/> column holds the
/// gzip-compressed JSON play-by-play. End-of-game replays for a
/// 16-hand championship game can run into hundreds of KB
/// uncompressed; the gzip envelope keeps storage flat in the
/// dominant case (long sparse JSON arrays) without forcing the
/// API surface to deal with binary on the wire.</para>
/// </summary>
public sealed class ReplayRecord
{
    /// <summary>Synthetic id, opaque to clients. Format:
    /// <c>r-{8 url-safe base64 chars}</c>. Stable across the row's
    /// lifetime — the public <c>/api/replays/{replayId}</c> URL
    /// pins this value.</summary>
    public string ReplayId { get; set; } = string.Empty;

    /// <summary>Originating game id. Indexed for the
    /// <c>GET /api/games/{gameId}/replays</c> lookup
    /// (W13 forward).</summary>
    public Guid GameId { get; set; }

    /// <summary>UTC timestamp when the game completed (the
    /// emission edge that ingests the replay). Used by retention
    /// sweep + listing endpoints.</summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>Rule variant — typically <c>"changsha-v1"</c>.
    /// Stored verbatim so future variants don't pollute the
    /// surface.</summary>
    public string Variant { get; set; } = "changsha-v1";

    /// <summary>Number of turns recorded in the payload. Used by
    /// lightweight metadata-only callers (replay browser, audit
    /// dashboard) to avoid pulling the full payload.</summary>
    public int TurnCount { get; set; }

    /// <summary>gzip-compressed JSON play-by-play. Decompressed
    /// on read by <see cref="DecompressPayload"/>.</summary>
    public byte[] CompressedPayload { get; set; } = Array.Empty<byte>();

    /// <summary>UTC timestamp when the row was first inserted —
    /// distinct from <see cref="CompletedAt"/> so the retention
    /// sweeper has a stable insertion anchor (a backfill job
    /// could re-stamp the same <c>CompletedAt</c>).</summary>
    public DateTime IngestedAt { get; set; }

    /// <summary>UTC retention expiry — the sweeper deletes rows
    /// older than this. Derived as
    /// <c>CompletedAt + RetentionDays</c> at insert time.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Phase K Wave 16 — Bishop. Optional tenant association
    /// consumed by the per-tenant retention sweep. Null = no
    /// tenant scope (the global default retention applies).
    /// Non-null = the sweep consults
    /// <see cref="IReplayRetentionPolicyStore"/> for the
    /// matching row first and falls back to the global default
    /// when no row is found. Stable across the row's lifetime —
    /// the ingest endpoint stamps it from the
    /// <c>X-Tenant-Id</c> header (or future tenant claim) when
    /// present.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>Compresses a JSON play-by-play payload for
    /// storage. The gzip wrapper uses default compression so
    /// CPU cost stays moderate at ingest time.</summary>
    public static byte[] CompressPayload(string json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<byte>();
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>Decompresses the stored payload back to the
    /// canonical JSON string. Returns an empty string when the
    /// payload is null/empty.</summary>
    public static string DecompressPayload(byte[]? compressed)
    {
        if (compressed is null || compressed.Length == 0) return string.Empty;
        using var ms = new MemoryStream(compressed);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Returns the decompressed payload of this
    /// record.</summary>
    public string DecompressPayload() => DecompressPayload(CompressedPayload);
}
