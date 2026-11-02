using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Autotable.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Changsha.Runtime;

/// <summary>
/// Phase J Wave 7 — REST surface for completed-game replay snapshots.
///
/// <para><b>GET /api/games/{gameId}/replay</b> — returns the canonical
/// play-by-play for a completed Changsha game. Returns <c>404</c> when no
/// replay snapshot exists for the supplied id (either an unknown game id
/// or a game still in progress — Wave 7 persists the snapshot at
/// <c>GameCompleted</c> emission only).</para>
///
/// <para>Response shape on <c>200</c>:</para>
/// <code>
/// {
///   "gameId": "9b3a7f01-…",
///   "createdAt": "2026-05-23T04:32:11.812Z",
///   "events": [
///     {
///       "turn": 1,
///       "phase": "Setup",
///       "actor": -1,
///       "action": "game-created",
///       "tilesJson": "[]",
///       "timestampUtc": "2026-05-23T04:21:00.000Z"
///     },
///     {
///       "turn": 4,
///       "phase": "Discard",
///       "actor": 2,
///       "action": "tile-discarded",
///       "tilesJson": "[47]",
///       "timestampUtc": "2026-05-23T04:21:18.402Z"
///     }
///     // …one entry per ChangshaGameState.EventLog entry, ordered by
///     // sequence (insertion order on the runtime — chronological).
///   ]
/// }
/// </code>
///
/// <para><b>Rate limit.</b> The endpoint is large by nature (an end-game
/// replay can run into hundreds of KB) and read-only, so it explicitly
/// opts into the <c>token-bucket-api</c> policy (Phase J Wave 6) which
/// caps per-IP bursts at the same level as the rest of the
/// <c>/api/**</c> surface.</para>
/// </summary>
[ApiController]
[Route("api/games")]
public sealed class ChangshaReplayController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<ChangshaReplayController> _logger;

    public ChangshaReplayController(AppDbContext db, ILogger<ChangshaReplayController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns the persisted replay snapshot for <paramref name="gameId"/>,
    /// or <c>404</c> when no row exists. <c>gameId</c> must parse as a
    /// <see cref="Guid"/>; malformed ids return <c>400</c>.
    ///
    /// <para>The body's <c>events</c> field is materialised from the
    /// stored <c>EventsJson</c> string so the wire shape is JSON-native
    /// (consumers see a structured array, not an encoded string).
    /// <c>tilesJson</c> inside each event remains a string per the
    /// agreed wire contract.</para>
    /// </summary>
    [HttpGet("{gameId}/replay")]
    [EnableRateLimiting(RateLimiting.RateLimitingExtensions.ApiPolicy)]
    public async Task<IActionResult> Get(string gameId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(gameId, out var gameGuid))
        {
            return BadRequest(new { error = "gameId must be a GUID." });
        }

        var row = await _db.ChangshaGameReplays
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.GameId == gameGuid, ct);

        if (row is null)
        {
            return NotFound(new { error = "Replay not found.", gameId });
        }

        // Turns reset at each hand. V3 carries the real global event sequence;
        // legacy arrays preserve their recorded storage order without guessing it.
        //
        // Phase J Wave 9 — the writer now emits a v2 envelope
        // ({ schemaVersion, events: [...] }); we normalise both v1
        // (bare array) and v2 (envelope object with "events" key) into
        // the same canonical wire response and surface schemaVersion
        // so a client can branch on shape if it wants.
        //
        // Malformed JSON cannot establish a legacy schema from a stale row
        // version. Never expose an unparsed replay as public events.
        object events;
        int schemaVersion = row.SchemaVersion;
        try
        {
            using var doc = JsonDocument.Parse(row.EventsJson);
            if (row.SchemaVersion >= 3
                && (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("schemaVersion", out var declaredVersion)
                    || declaredVersion.ValueKind != JsonValueKind.Number
                    || !declaredVersion.TryGetInt32(out var declared)
                    || declared != 3))
                return StatusCode(500, new { error = "invalid-replay-schema", gameId });
            // Resolve the payload version before any legacy fallback, even if
            // its events field is malformed and the row's column has drifted.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("schemaVersion", out var sv))
            {
                if (sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out schemaVersion))
                    return StatusCode(500, new { error = "invalid-replay-schema", gameId });
            }
            JsonElement eventsArrayElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // v1 — bare events array. Legacy rows; schemaVersion
                // already defaults to 1 in the row.
                eventsArrayElement = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object
                  && doc.RootElement.TryGetProperty("events", out var maybeEvents)
                  && maybeEvents.ValueKind == JsonValueKind.Array)
            {
                eventsArrayElement = maybeEvents;
            }
            else
            {
                if (schemaVersion >= 3)
                    return StatusCode(500, new { error = "invalid-replay-schema", gameId });
                events = doc.RootElement.Clone();
                return Ok(new
                {
                    gameId = gameGuid,
                    createdAt = row.CreatedAt,
                    schemaVersion,
                    events,
                });
            }

            var elements = eventsArrayElement.EnumerateArray().Select(el => el.Clone()).ToArray();
            if (schemaVersion > 3)
                return StatusCode(500, new { error = "unsupported-replay-schema", gameId });
            if (schemaVersion >= 3)
            {
                var sequenced = new List<(long Sequence, JsonElement Element)>(elements.Length);
                foreach (var element in elements)
                {
                    if (element.ValueKind != JsonValueKind.Object
                        || !element.TryGetProperty("sequence", out var sequence)
                        || sequence.ValueKind != JsonValueKind.Number
                        || !sequence.TryGetInt64(out var value) || value <= 0)
                        return StatusCode(500, new { error = "invalid-replay-order", gameId });
                    sequenced.Add((value, element));
                }
                var ordered = sequenced.OrderBy(e => e.Sequence).ToArray();
                if (ordered.Where((entry, index) => entry.Sequence != index + 1L).Any())
                    return StatusCode(500, new { error = "invalid-replay-order", gameId });
                elements = ordered.Select(e => e.Element).ToArray();
            }
            events = elements.Select(NormaliseLegacyEvent).ToArray();
        }
        catch (JsonException)
        {
            _logger.LogWarning("Replay row {ReplayId} for game {GameId} has malformed EventsJson.", row.Id, gameId);
            return StatusCode(500, new { error = "invalid-replay-format", gameId });
        }

        return Ok(new
        {
            gameId = gameGuid,
            createdAt = row.CreatedAt,
            schemaVersion,
            events,
        });
    }

    /// <summary>
    /// Phase J Wave 10 — v1 → v2 read-path normaliser. Some replay rows were
    /// persisted before Wave 9's schema-versioning hook existed; their per-
    /// event objects lack the v2 envelope fields (<c>source</c>,
    /// <c>durationMs</c>, <c>debugScore</c>). The reader synthesises stable
    /// defaults so the wire surface is shape-invariant regardless of when
    /// the row was written:
    /// <list type="bullet">
    ///   <item><c>source</c> → <c>"unknown"</c> (cannot retroactively
    ///     classify human vs bot vs system without state).</item>
    ///   <item><c>durationMs</c> → <c>null</c> (the inter-event gap was
    ///     never recorded for legacy rows; <c>null</c> distinguishes
    ///     "unknown" from "instantaneous" in the wire shape).</item>
    ///   <item><c>debugScore</c> → <c>null</c> (no bot decision metadata
    ///     was captured pre-Wave-10).</item>
    /// </list>
    ///
    /// <para>Non-legacy events (already carrying the v2 fields) pass
    /// through unchanged — we re-emit the object so the response wire
    /// shape stays a flat object even after the per-key check.
    /// <c>JsonNode</c> is the projection target because <see cref="JsonElement"/>
    /// is immutable; the parsed clone is rebuilt on every read which is
    /// fine (a typical end-game replay has &lt;200 events).</para>
    ///
    /// <para>The same projection runs against v2 rows that lack
    /// <c>debugScore</c> (Wave 9 wrote source + durationMs but
    /// <see cref="BotDecision"/> bot reasoning didn't land until Wave 10),
    /// so the synthesised <c>debugScore: null</c> is what Wave 9 rows
    /// surface too.</para>
    /// </summary>
    private static JsonNode NormaliseLegacyEvent(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText()) as JsonObject ?? new JsonObject();
        if (!node.ContainsKey("source")) node["source"] = "unknown";
        if (!node.ContainsKey("durationMs")) node["durationMs"] = null;
        if (!node.ContainsKey("debugScore")) node["debugScore"] = null;
        return node;
    }
}
