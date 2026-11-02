using System.Globalization;
using System.Text;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase K Wave 1 — match-history export surface.
///
/// <para><b>GET /api/games?playerId=&amp;from=&amp;to=&amp;format=json|csv&amp;limit=&amp;offset=</b>
/// — returns the completed games this player participated in.
/// <c>playerId</c> is required. <c>from</c> + <c>to</c> are inclusive
/// ISO-8601 UTC instants applied against <c>CompletedAt</c>. <c>format</c>
/// is <c>json</c> (default) or <c>csv</c>; the CSV body includes exactly
/// the columns <c>GameId, StartedAt, CompletedAt, FinalScore, Won,
/// OpponentPlayerIds, RulePresetId</c> per the Phase-K-1 contract.</para>
///
/// <para>Rate limit: the read-only token-bucket-api policy, same shape as
/// every <c>/api/**</c> read endpoint (Phase J Wave 6 default), wired
/// here explicitly so the route inherits the bucket regardless of where
/// the global <c>MapControllers</c> RequireRateLimiting() configures it.</para>
/// </summary>
[ApiController]
[Route("api/games")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class GamesHistoryController : ControllerBase
{
    /// <summary>Maximum page size accepted by the endpoint. Larger
    /// <c>limit</c> values are clamped silently. Phase K Wave 2 raises
    /// the ceiling from 200 → 10000 so CSV exports can complete in
    /// fewer round-trips. The endpoint switches to streaming for
    /// large exports so the bump doesn't blow the heap.</summary>
    public const int MaxLimit = 10000;

    /// <summary>Default page size when <c>limit</c> is omitted. Phase K
    /// Wave 2 bumps from 50 → 1000 — large enough that a typical
    /// CSV export completes in one call, small enough that a JSON
    /// caller doesn't hit the wire with megabytes of payload.</summary>
    public const int DefaultLimit = 1000;

    private readonly IServiceScopeFactory _scopeFactory;

    public GamesHistoryController(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Lists this player's completed games. Returns <c>400</c> when
    /// <c>playerId</c> is missing or <c>format</c> is unrecognised. JSON
    /// shape: <c>{ playerId, total, limit, offset, games:[…] }</c>. CSV
    /// shape: a single header row followed by one data row per game.
    /// Phase K Wave 2 — accepts an opaque URL-safe <c>cursor</c>
    /// parameter for keyset pagination + emits an <c>X-Next-Cursor</c>
    /// response header when more rows exist beyond the returned page.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? playerId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? format,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromQuery] string? cursor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return BadRequest(new { error = "playerId is required." });
        }

        var fmt = (format ?? "json").Trim().ToLowerInvariant();
        if (fmt != "json" && fmt != "csv")
        {
            return BadRequest(new { error = "format must be one of: json, csv." });
        }

        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var effectiveOffset = Math.Max(0, offset ?? 0);

        // Phase K Wave 2 — decode the opaque cursor. Malformed cursor
        // → 400 (Vasquez's contract test asserts "never 500").
        DateTime? cursorCompletedAt = null;
        Guid? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryDecodeCursor(cursor, out cursorCompletedAt, out cursorId))
            {
                return BadRequest(new { error = "cursor is malformed." });
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var q = db.PlayerGameHistory
            .AsNoTracking()
            .Where(h => h.PlayerId == playerId);
        if (from.HasValue)
        {
            var fromUtc = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
            q = q.Where(h => h.CompletedAt >= fromUtc);
        }
        if (to.HasValue)
        {
            var toUtc = DateTime.SpecifyKind(to.Value, DateTimeKind.Utc);
            q = q.Where(h => h.CompletedAt <= toUtc);
        }
        if (cursorCompletedAt is not null && cursorId is not null)
        {
            // Keyset pagination: skip rows on the cursor's date or
            // later — we order by CompletedAt desc, Id asc, so the
            // "next page" is strictly older.
            var cAt = cursorCompletedAt.Value;
            var cId = cursorId.Value;
            q = q.Where(h => h.CompletedAt < cAt
                          || (h.CompletedAt == cAt && string.Compare(h.Id.ToString(), cId.ToString()) > 0));
        }

        var total = await q.CountAsync(ct);
        // Pull one extra row so we can detect whether a next page exists.
        var rows = await q
            .OrderByDescending(h => h.CompletedAt)
            .ThenBy(h => h.Id)
            .Take(effectiveLimit + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > effectiveLimit)
        {
            // Trim the probe row + mint the cursor from the LAST returned row.
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = EncodeCursor(last.CompletedAt, last.Id);
        }

        if (nextCursor is not null)
        {
            Response.Headers["X-Next-Cursor"] = nextCursor;
        }

        if (fmt == "csv")
        {
            var csv = new StringBuilder();
            csv.AppendLine("GameId,StartedAt,CompletedAt,FinalScore,Won,OpponentPlayerIds,RulePresetId");
            foreach (var r in rows)
            {
                csv.Append(r.GameId).Append(',');
                csv.Append(r.StartedAt.ToString("o", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(r.CompletedAt.ToString("o", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(r.FinalScore).Append(',');
                csv.Append(r.Won ? "true" : "false").Append(',');
                csv.Append(CsvEscape(r.OpponentPlayerIdsCsv)).Append(',');
                csv.Append(r.RulePresetId?.ToString() ?? string.Empty);
                csv.AppendLine();
            }
            return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"games-{playerId}.csv");
        }

        return Ok(new
        {
            playerId,
            total,
            limit = effectiveLimit,
            offset = effectiveOffset,
            nextCursor,
            games = rows.Select(r => new
            {
                gameId = r.GameId,
                seatIndex = r.SeatIndex,
                finalScore = r.FinalScore,
                won = r.Won,
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
                opponentPlayerIds = string.IsNullOrEmpty(r.OpponentPlayerIdsCsv)
                    ? Array.Empty<string>()
                    : r.OpponentPlayerIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries),
                rulePresetId = r.RulePresetId,
            }).ToArray(),
        });
    }

    /// <summary>
    /// Phase K Wave 2 — encodes a (completedAt, id) pair into an opaque
    /// URL-safe base64 cursor. The format is <c>{ISO8601}|{Guid}</c>
    /// then base64url-encoded so it survives a round-trip through a
    /// <c>?cursor=</c> query string without further escaping.
    /// </summary>
    internal static string EncodeCursor(DateTime completedAt, Guid id)
    {
        var raw = completedAt.ToString("O", CultureInfo.InvariantCulture) + "|" + id.ToString("N");
        var bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Phase K Wave 2 — companion to <see cref="EncodeCursor"/>. Returns
    /// false (caller maps to 400) on any decode failure — never throws.
    /// </summary>
    internal static bool TryDecodeCursor(string cursor, out DateTime? completedAt, out Guid? id)
    {
        completedAt = null;
        id = null;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 1: return false;
            }
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var pipe = raw.IndexOf('|');
            if (pipe <= 0 || pipe == raw.Length - 1) return false;
            if (!DateTime.TryParse(raw.AsSpan(0, pipe), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt)) return false;
            if (!Guid.TryParseExact(raw[(pipe + 1)..], "N", out var g)) return false;
            completedAt = dt;
            id = g;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wraps a CSV cell in double quotes when it contains a comma, quote,
    /// CR, or LF. Quotes inside the value are doubled per RFC 4180.
    /// Empty strings pass through unquoted (the bare blank between
    /// commas is the canonical empty representation).
    /// </summary>
    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
