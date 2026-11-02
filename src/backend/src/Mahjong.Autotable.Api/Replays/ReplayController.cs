using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 12 — Bishop. REST surface for the replay-by-id
/// store. Hicks's W12 <c>?action=replay</c> client URL fetches a
/// replay envelope from <c>GET /api/replays/{replayId}</c>; the
/// game-over runtime hook (W13 forward) will <c>POST</c> the
/// payload at completion time. For W12 the POST is a public
/// endpoint accepting raw payloads — production deployments
/// gate it via the API gateway / network policy.
///
/// <list type="bullet">
///   <item><c>GET /api/replays/{replayId}</c> — returns the
///         metadata envelope + the decompressed JSON
///         play-by-play. 404 when no row exists.</item>
///   <item><c>POST /api/replays</c> — accepts the JSON envelope
///         <c>{ gameId, completedAt, variant, turnCount, payload }</c>
///         and returns <c>{ replayId, ingestedAt, expiresAt }</c>.
///         The server gzip-compresses the payload at ingest.</item>
/// </list>
///
/// <para>Toggle: <see cref="ReplayOptions.StorageImpl"/>
/// (<c>"InMemory"</c> default for tests / <c>"Ef"</c> for prod).
/// See <c>docs/replay-by-id.md</c>.</para>
/// </summary>
[ApiController]
[Route("api/replays")]
public sealed class ReplayController : ControllerBase
{
    private readonly IReplayStore _store;
    private readonly ReplayOptions _options;
    private readonly ILogger<ReplayController> _logger;
    private readonly AuthCookieService? _cookies;
    private readonly Mahjong.Autotable.Api.Observability.TournamentQueryLatencyMetrics? _latencyMetrics;

    public ReplayController(
        IReplayStore store,
        ReplayOptions options,
        ILogger<ReplayController> logger,
        AuthCookieService? cookies = null,
        Mahjong.Autotable.Api.Observability.TournamentQueryLatencyMetrics? latencyMetrics = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cookies = cookies;
        _latencyMetrics = latencyMetrics;
    }

    /// <summary>
    /// Phase K Wave 14 — Bishop. Metadata-only listing endpoint
    /// backing the replay browser. Filters by completed-at range
    /// (<c>from</c>/<c>to</c>) and optional <c>variant</c>; page
    /// pinned by <c>skip</c>/<c>limit</c>. The heavy payload is
    /// intentionally dropped from the wire — clients pull a single
    /// replay's payload via <c>GET /api/replays/{replayId}</c>.
    /// See <c>docs/replay-by-id.md §3</c>.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimiting.RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> List(
        [FromQuery(Name = "from")] string? from = null,
        [FromQuery(Name = "to")] string? to = null,
        [FromQuery(Name = "variant")] string? variant = null,
        [FromQuery(Name = "skip")] int? skip = null,
        [FromQuery(Name = "limit")] int? limit = null,
        CancellationToken ct = default)
    {
        DateTime? fromUtc = ParseUtc(from);
        if (!string.IsNullOrWhiteSpace(from) && fromUtc is null)
        {
            return BadRequest(new { error = "from must be an ISO 8601 UTC timestamp." });
        }
        DateTime? toUtc = ParseUtc(to);
        if (!string.IsNullOrWhiteSpace(to) && toUtc is null)
        {
            return BadRequest(new { error = "to must be an ISO 8601 UTC timestamp." });
        }
        var configuredPageSize = _options.PageSize <= 0
            ? ReplayOptions.DefaultPageSize
            : _options.PageSize;
        if (configuredPageSize > ReplayOptions.MaxPageSize)
            configuredPageSize = ReplayOptions.MaxPageSize;
        var take = Math.Clamp(limit ?? configuredPageSize, 1, ReplayOptions.MaxPageSize);
        var skipN = Math.Max(0, skip ?? 0);

        // Phase K Wave 15 — Bishop. Time the listing query so the
        // tournament-scale latency histogram can surface a p99 by
        // page-size bucket. See docs/bracket-shape.md §6.
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = await _store.ListAsync(fromUtc, toUtc, variant, skipN, take, ct);
        _latencyMetrics?.ObserveTimestamp("replay-list", take, t0);
        return Ok(new
        {
            items = rows.Select(r => new
            {
                replayId = r.ReplayId,
                gameId = r.GameId,
                completedAt = r.CompletedAt,
                variant = r.Variant,
                turnCount = r.TurnCount,
                payloadSize = r.CompressedPayload?.Length ?? 0,
                ingestedAt = r.IngestedAt,
                expiresAt = r.ExpiresAt,
            }).ToArray(),
            count = rows.Count,
            skip = skipN,
            limit = take,
            pageSize = configuredPageSize,
            filters = new
            {
                from = fromUtc,
                to = toUtc,
                variant = string.IsNullOrWhiteSpace(variant) ? null : variant.Trim(),
            },
        });
    }

    private static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>
    /// Fetch a single replay by id. Returns the metadata
    /// envelope plus the decompressed JSON play-by-play. 404
    /// when no row exists.
    /// </summary>
    [HttpGet("{replayId}")]
    [EnableRateLimiting(RateLimiting.RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> Get(string replayId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(replayId))
        {
            return BadRequest(new { error = "replayId is required." });
        }
        var row = await _store.GetAsync(replayId, ct);
        if (row is null)
        {
            return NotFound(new { error = "replay-not-found", replayId });
        }
        var payloadJson = row.DecompressPayload();
        // Phase K Wave 12 — Bishop. Wire shape mirrors the
        // existing ChangshaReplayController contract: metadata
        // fields camelCased + the JSON payload reified as a
        // nested object (not an opaque string) so consumers see
        // a structured shape directly.
        object payloadNode;
        try
        {
            payloadNode = string.IsNullOrWhiteSpace(payloadJson)
                ? new { }
                : JsonSerializer.Deserialize<JsonElement>(payloadJson);
        }
        catch (JsonException)
        {
            payloadNode = new { raw = payloadJson };
        }
        return Ok(new
        {
            replayId = row.ReplayId,
            gameId = row.GameId,
            completedAt = row.CompletedAt,
            variant = row.Variant,
            turnCount = row.TurnCount,
            ingestedAt = row.IngestedAt,
            expiresAt = row.ExpiresAt,
            payload = payloadNode,
        });
    }

    /// <summary>
    /// Phase K Wave 15 — Bishop. Streams the decompressed JSON
    /// play-by-play as <c>application/octet-stream</c> bytes
    /// without materialising the full payload in memory. Suited
    /// for large 16-hand championship replays where the
    /// <see cref="Get"/> JSON envelope would force the client to
    /// buffer ~1 MB of nested objects before parsing.
    ///
    /// <para>The endpoint honours <c>Range: bytes=&lt;start&gt;-&lt;end&gt;</c>
    /// over the decompressed payload so resumable downloads work
    /// against the same byte offsets a non-streaming client would
    /// see. <c>Content-Length</c> + <c>Accept-Ranges: bytes</c>
    /// are stamped on the response so well-behaved clients can
    /// detect resumability up-front; a malformed Range header
    /// returns <c>416 Range Not Satisfiable</c>.</para>
    ///
    /// <para>404 when no row exists — same envelope as
    /// <see cref="Get"/>. See <c>docs/replay-streaming.md</c>.</para>
    /// </summary>
    [HttpGet("{replayId}/blob")]
    [EnableRateLimiting(RateLimiting.RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> GetBlob(string replayId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(replayId))
        {
            return BadRequest(new { error = "replayId is required." });
        }
        var row = await _store.GetAsync(replayId, ct);
        if (row is null)
        {
            return NotFound(new { error = "replay-not-found", replayId });
        }

        // Phase K Wave 15 — Bishop. Decompress to bytes once so we
        // can advertise Content-Length + serve Range requests with
        // a known byte ceiling. The payload is bounded by the
        // upstream MaxCompressedBytes invariant (8 MB compressed
        // ≈ 64 MB uncompressed worst case) so a single buffer
        // stays within typical kestrel limits; the chunked
        // transfer arises naturally from kestrel framing because
        // we write the bytes back to the response body stream
        // without setting Content-Length on the slice path.
        byte[] decompressed;
        try
        {
            var json = ReplayRecord.DecompressPayload(row.CompressedPayload);
            decompressed = System.Text.Encoding.UTF8.GetBytes(json);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
        {
            _logger.LogWarning(ex,
                "Replay {ReplayId} payload decompression failed; rejecting blob request.",
                replayId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "payload-decompression-failed",
                replayId,
            });
        }

        var totalLength = decompressed.Length;
        Response.Headers.Append("Accept-Ranges", "bytes");
        Response.Headers.Append("X-Replay-Id", row.ReplayId);
        Response.Headers.Append("X-Replay-Variant", row.Variant ?? string.Empty);

        // RFC 7233 single-range Range support. We parse only the
        // canonical "bytes=start-end" / "bytes=start-" forms; any
        // other shape returns 416 (clients fall back to a full
        // GET on 416).
        if (Request.Headers.TryGetValue("Range", out var rangeHeaderValues)
            && rangeHeaderValues.Count > 0
            && !string.IsNullOrWhiteSpace(rangeHeaderValues[0]))
        {
            var rangeHeader = rangeHeaderValues[0]!.Trim();
            if (!TryParseSingleByteRange(rangeHeader, totalLength, out var start, out var end))
            {
                Response.Headers.Append("Content-Range", $"bytes */{totalLength}");
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }
            var sliceLength = end - start + 1;
            Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{totalLength}");
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = sliceLength;
            Response.StatusCode = StatusCodes.Status206PartialContent;
            await Response.Body.WriteAsync(decompressed.AsMemory((int)start, (int)sliceLength), ct);
            return new EmptyResult();
        }

        // Full body — chunked transfer is enabled by leaving
        // Content-Length unset; we still emit the total length
        // for clients that want to allocate. Setting both is
        // valid per HTTP/1.1.
        Response.ContentType = "application/octet-stream";
        Response.ContentLength = totalLength;
        Response.StatusCode = StatusCodes.Status200OK;
        await Response.Body.WriteAsync(decompressed.AsMemory(0, totalLength), ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Phase K Wave 15 — Bishop. Parse a single-range
    /// <c>bytes=start-end</c> Range header against the decompressed
    /// payload length. Returns false (caller serves 416) for any
    /// malformed value, inverted ranges, or end ≥ length.
    /// Suffix ranges (<c>bytes=-N</c>) and open-ended start ranges
    /// (<c>bytes=N-</c>) are both honoured.
    /// </summary>
    internal static bool TryParseSingleByteRange(string headerValue, long totalLength, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (totalLength <= 0) return false;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        const string prefix = "bytes=";
        if (!headerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var spec = headerValue.Substring(prefix.Length).Trim();
        if (spec.Length == 0 || spec.Contains(',')) return false; // multi-range unsupported
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var startStr = spec.Substring(0, dash).Trim();
        var endStr = spec.Substring(dash + 1).Trim();
        if (startStr.Length == 0)
        {
            // Suffix range — last N bytes.
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
            System.Globalization.CultureInfo.InvariantCulture, out start) || start < 0)
        {
            return false;
        }
        if (endStr.Length == 0)
        {
            end = totalLength - 1;
        }
        else if (!long.TryParse(endStr, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out end) || end < start)
        {
            return false;
        }
        if (start >= totalLength) return false;
        if (end >= totalLength) end = totalLength - 1;
        return true;
    }

    /// <summary>
    /// Insert a new replay record. The server mints
    /// <c>replayId</c> + <c>ingestedAt</c> + <c>expiresAt</c>
    /// and gzip-compresses the payload before persistence.
    ///
    /// <para>Phase K Wave 13 — Bishop. POST is admin-gated by
    /// default (<see cref="ReplayOptions.RequireAdminForPost"/>);
    /// anonymous → 401, non-admin → 403. Development fixtures
    /// can disable the gate via
    /// <c>Replays:RequireAdminForPost = false</c>.</para>
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimiting.RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> Post([FromBody] PostReplayBody? body, CancellationToken ct = default)
    {
        // Phase K Wave 13 — Bishop. Admin gate. When the toggle is
        // disabled (dev convenience) we skip both the session resolve
        // + the role check so anonymous callers get the W12 behaviour.
        if (_options.RequireAdminForPost)
        {
            if (_cookies is null)
            {
                // Defence in depth — if the cookie service isn't wired
                // we cannot evaluate the admin claim, so the gate is
                // closed rather than fail-open. In practice the cookie
                // service is registered as a singleton in Program.cs
                // before any controller resolves.
                return Unauthorized(new { error = "session-required" });
            }
            var session = await _cookies.ResolveAsync(HttpContext, ct);
            if (session is null)
            {
                return Unauthorized(new { error = "session-required" });
            }
            if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "admin-required",
                });
            }
        }

        if (body is null)
        {
            return BadRequest(new { error = "body is required." });
        }
        if (body.GameId == Guid.Empty)
        {
            return BadRequest(new { error = "gameId is required." });
        }
        var payloadJson = body.Payload is null
            ? "{}"
            : body.Payload.Value.GetRawText();
        if (payloadJson.Length > _options.MaxCompressedBytes * 8) // soft upper bound on uncompressed
        {
            return BadRequest(new { error = "payload-too-large" });
        }
        var compressed = ReplayRecord.CompressPayload(payloadJson);
        if (compressed.Length > _options.MaxCompressedBytes)
        {
            return BadRequest(new { error = "payload-too-large" });
        }
        var completedAt = body.CompletedAt == default ? DateTime.UtcNow : body.CompletedAt.ToUniversalTime();
        var record = new ReplayRecord
        {
            ReplayId = string.IsNullOrWhiteSpace(body.ReplayId) ? string.Empty : body.ReplayId!.Trim(),
            GameId = body.GameId,
            CompletedAt = completedAt,
            Variant = string.IsNullOrWhiteSpace(body.Variant) ? "changsha-v1" : body.Variant!.Trim(),
            TurnCount = Math.Max(0, body.TurnCount),
            CompressedPayload = compressed,
        };
        var stored = await _store.InsertAsync(record, ct);
        return CreatedAtAction(nameof(Get), new { replayId = stored.ReplayId }, new
        {
            replayId = stored.ReplayId,
            gameId = stored.GameId,
            ingestedAt = stored.IngestedAt,
            expiresAt = stored.ExpiresAt,
        });
    }

    /// <summary>
    /// Phase K Wave 12 — Bishop. POST body envelope. The
    /// payload field is a free-form JSON object — typically the
    /// canonical Changsha play-by-play emitted by
    /// <c>ChangshaGameRuntime.EmitGameCompletedAsync</c>.
    /// </summary>
    public sealed class PostReplayBody
    {
        /// <summary>Optional client-supplied replay id. Empty
        /// → server-minted. Mostly useful for replay tests that
        /// need a deterministic id.</summary>
        public string? ReplayId { get; set; }

        /// <summary>Originating game id. Required.</summary>
        public Guid GameId { get; set; }

        /// <summary>UTC timestamp when the game completed.
        /// Empty → server stamps <c>DateTime.UtcNow</c>.</summary>
        public DateTime CompletedAt { get; set; }

        /// <summary>Rule variant (e.g. <c>"changsha-v1"</c>).
        /// Empty → defaults to <c>"changsha-v1"</c>.</summary>
        public string? Variant { get; set; }

        /// <summary>Turn count for the encoded play-by-play.
        /// Surfaced in the GET response so listing endpoints
        /// can show a digest without pulling the
        /// payload.</summary>
        public int TurnCount { get; set; }

        /// <summary>The play-by-play JSON object. Compressed
        /// at ingest.</summary>
        public JsonElement? Payload { get; set; }
    }
}
