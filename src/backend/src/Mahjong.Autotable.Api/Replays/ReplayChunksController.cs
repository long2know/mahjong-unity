using System.Security.Cryptography;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 22 — Bishop. Admin-gated chunked download
/// surface for replay payloads. Surface:
/// <c>GET /api/admin/replays/{replayId}/chunks/{n}</c>.
///
/// <para>The endpoint serves a fixed-size slice (default
/// <see cref="DefaultChunkSize"/> bytes; clients may override
/// via the <c>?chunkSize=</c> query parameter up to
/// <see cref="MaxChunkSize"/>) of the decompressed payload.
/// Chunks are 1-indexed; offset = <c>(n - 1) * chunkSize</c>.
/// Out-of-range n returns 404. The final chunk is truncated to
/// the remaining bytes.</para>
///
/// <para>The response stamps an <c>ETag</c> derived from the
/// payload SHA-256 + chunk-size + chunk-index so a CDN /
/// resumable-download client can revalidate without
/// re-fetching the body. RFC 7233 <c>Range</c> headers are
/// honoured INSIDE the chosen chunk so a partial chunk fetch
/// (e.g. resume after a flaky disconnect) works without
/// re-downloading the whole slice.</para>
///
/// <para>Auth: 401 / 403 / 400 / 404 / 304 / 200 / 206.</para>
/// </summary>
[ApiController]
[Route("api/admin/replays/{replayId}/chunks")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class ReplayChunksController : ControllerBase
{
    public const int DefaultChunkSize = 64 * 1024;     // 64 KB
    public const int MinChunkSize = 1024;              // 1 KB
    public const int MaxChunkSize = 4 * 1024 * 1024;   // 4 MB

    public const string ErrorReplayNotFound = "replay-not-found";
    public const string ErrorChunkOutOfRange = "chunk-out-of-range";
    public const string ErrorInvalidChunkIndex = "invalid-chunk-index";
    public const string ErrorInvalidChunkSize = "invalid-chunk-size";

    private readonly AuthCookieService _cookies;
    private readonly IReplayStore _store;
    private readonly ILogger<ReplayChunksController> _logger;

    public ReplayChunksController(
        AuthCookieService cookies,
        IReplayStore store,
        ILogger<ReplayChunksController> logger)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("{n:int}")]
    public async Task<IActionResult> GetChunk(
        [FromRoute] string replayId,
        [FromRoute] int n,
        [FromQuery(Name = "chunkSize")] int? chunkSize,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }
        if (string.IsNullOrWhiteSpace(replayId))
        {
            return BadRequest(new { error = "replay-id-required" });
        }
        if (n < 1)
        {
            return BadRequest(new { error = ErrorInvalidChunkIndex, chunkIndex = n });
        }
        var effective = chunkSize ?? DefaultChunkSize;
        if (effective < MinChunkSize || effective > MaxChunkSize)
        {
            return BadRequest(new
            {
                error = ErrorInvalidChunkSize,
                minimum = MinChunkSize,
                maximum = MaxChunkSize,
                requested = effective,
            });
        }

        var row = await _store.GetAsync(replayId, ct);
        if (row is null)
        {
            return NotFound(new { error = ErrorReplayNotFound, replayId });
        }

        byte[] decompressed;
        try
        {
            var json = ReplayRecord.DecompressPayload(row.CompressedPayload);
            decompressed = System.Text.Encoding.UTF8.GetBytes(json);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
        {
            _logger.LogWarning(ex,
                "Replay {ReplayId} payload decompression failed; rejecting chunk request.",
                replayId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "payload-decompression-failed",
                replayId,
            });
        }

        var totalLength = decompressed.Length;
        var chunkCount = ComputeChunkCount(totalLength, effective);
        if (n > chunkCount)
        {
            return NotFound(new
            {
                error = ErrorChunkOutOfRange,
                replayId,
                chunkIndex = n,
                totalChunks = chunkCount,
            });
        }

        long offset = (long)(n - 1) * effective;
        int sliceLength = (int)Math.Min((long)effective, totalLength - offset);

        var etag = ComputeEtag(decompressed, effective, n);
        // RFC 7232 304 Not Modified — strong ETag match.
        if (Request.Headers.TryGetValue("If-None-Match", out var inm)
            && inm.Count > 0
            && string.Equals(inm.ToString().Trim(), etag, StringComparison.Ordinal))
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;
        Response.Headers.Append("Accept-Ranges", "bytes");
        Response.Headers.Append("X-Replay-Id", row.ReplayId);
        Response.Headers.Append("X-Replay-Chunk-Index", n.ToString());
        Response.Headers.Append("X-Replay-Chunk-Count", chunkCount.ToString());
        Response.Headers.Append("X-Replay-Chunk-Size", effective.ToString());
        Response.Headers.Append("X-Replay-Total-Length", totalLength.ToString());

        // Honour RFC 7233 Range header against THIS chunk's
        // byte range (so a resume re-fetch of a partial chunk
        // doesn't re-download bytes the client already wrote
        // to disk).
        if (Request.Headers.TryGetValue("Range", out var rangeHeaderValues)
            && rangeHeaderValues.Count > 0
            && !string.IsNullOrWhiteSpace(rangeHeaderValues[0]))
        {
            var rangeHeader = rangeHeaderValues[0]!.Trim();
            if (!TryParseSingleByteRange(rangeHeader, sliceLength, out var rs, out var re))
            {
                Response.Headers.Append("Content-Range", $"bytes */{sliceLength}");
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }
            var partialLen = re - rs + 1;
            Response.Headers.Append("Content-Range", $"bytes {rs}-{re}/{sliceLength}");
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = partialLen;
            Response.StatusCode = StatusCodes.Status206PartialContent;
            await Response.Body.WriteAsync(decompressed.AsMemory((int)(offset + rs), (int)partialLen), ct);
            return new EmptyResult();
        }

        Response.ContentType = "application/octet-stream";
        Response.ContentLength = sliceLength;
        Response.StatusCode = StatusCodes.Status200OK;
        await Response.Body.WriteAsync(decompressed.AsMemory((int)offset, sliceLength), ct);
        return new EmptyResult();
    }

    internal static int ComputeChunkCount(int totalLength, int chunkSize)
    {
        if (totalLength <= 0) return 0;
        if (chunkSize <= 0) return 0;
        return (totalLength + chunkSize - 1) / chunkSize;
    }

    internal static string ComputeEtag(byte[] payload, int chunkSize, int chunkIndex)
    {
        var hash = SHA256.HashData(payload);
        // Format: "<hex-payload-hash>-<chunkSize>-<chunkIndex>"
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}-{chunkSize}-{chunkIndex}\"";
    }

    internal static bool TryParseSingleByteRange(string headerValue, long totalLength, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (totalLength <= 0) return false;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        const string prefix = "bytes=";
        if (!headerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var spec = headerValue.Substring(prefix.Length).Trim();
        if (spec.Length == 0 || spec.Contains(',')) return false;
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var startStr = spec.Substring(0, dash).Trim();
        var endStr = spec.Substring(dash + 1).Trim();
        if (startStr.Length == 0)
        {
            if (!long.TryParse(endStr, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }
            suffix = Math.Min(suffix, totalLength);
            start = totalLength - suffix;
            end = totalLength - 1;
            return true;
        }
        if (!long.TryParse(startStr, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out start) || start < 0 || start >= totalLength)
        {
            return false;
        }
        if (endStr.Length == 0)
        {
            end = totalLength - 1;
        }
        else if (!long.TryParse(endStr, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out end) || end < start || end >= totalLength)
        {
            return false;
        }
        return true;
    }
}
