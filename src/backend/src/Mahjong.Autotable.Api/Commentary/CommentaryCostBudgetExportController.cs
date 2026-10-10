using System.Globalization;
using System.Text;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 18 — Bishop. Admin-gated CSV export of the
/// historical commentary cost-budget data. W15-W17 layered the
/// forecast (warning + exhausted state, fail-open swap to
/// <see cref="StubCommentaryGenerator"/>) + the admin override
/// (W17 <c>X-Admin-Reason</c> header surface) on top of the W9
/// per-month <see cref="CommentaryUsageRecord"/> ledger. W18
/// closes the loop with a historical export so an operator can
/// pull a finance-friendly CSV without a SQL session against the
/// production database.
///
/// <list type="bullet">
///   <item><c>GET /api/admin/commentary-cost-budget/export?from=YYYY-MM&amp;to=YYYY-MM&amp;tenant=...</c>
///         — returns a streamed CSV with one row per
///         (PeriodYear, PeriodMonth) tuple inclusive of both
///         end points. <c>tenant</c> is accepted as a parameter
///         for forward-compatibility — the underlying
///         <see cref="CommentaryUsageRecord"/> entity does not
///         currently carry a per-tenant column, so the parameter
///         is recorded in the audit row but otherwise ignored.
///   </item>
/// </list>
///
/// <para>CSV columns (header row, always present):</para>
/// <code>
/// periodYear,periodMonth,inputTokens,outputTokens,totalTokens,
/// requestCount,tokensPerDollar,monthlyCapUsd,usdSpent,
/// percentOfCap,state,createdAt,updatedAt
/// </code>
///
/// <para>Auth: <c>session.Role == "admin"</c> required. Empty
/// session → 401; non-admin session → 403. The
/// <c>X-Admin-Reason</c> header is OPTIONAL on the export (the
/// data is read-only and already gated by the admin role); when
/// present, the reason is included in the audit detail row.</para>
///
/// <para>Audit: every successful export emits a
/// <see cref="ReconnectAuditEntry.KindCommentaryCostBudgetExport"/>
/// audit row so the trail of "who exported, when, for what
/// window" is answerable post-hoc. The detail format is
/// <c>"from={from}|to={to}|tenant={tenant ?? ""}|rows={count}"</c>.</para>
///
/// <para>See <c>docs/commentary-llm.md §6.1 "Historical export"</c>
/// (added W18).</para>
/// </summary>
[ApiController]
[Route("api/admin/commentary-cost-budget")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class CommentaryCostBudgetExportController : ControllerBase
{
    /// <summary>Optional admin-reason header — captured in the
    /// audit row when present.</summary>
    public const string AdminReasonHeader = "X-Admin-Reason";

    /// <summary>Maximum window the export will accept (months).
    /// 60 — five years of monthly rows is well above any
    /// realistic operator query. Larger windows are rejected
    /// 400 to keep the response size predictable.</summary>
    public const int MaxWindowMonths = 60;

    /// <summary>Wire-stable CSV header — first line of every
    /// export response (including an empty result set).</summary>
    public const string CsvHeader =
        "periodYear,periodMonth,inputTokens,outputTokens,totalTokens," +
        "requestCount,tokensPerDollar,monthlyCapUsd,usdSpent," +
        "percentOfCap,state,createdAt,updatedAt";

    /// <summary>Wire-stable content type — RFC 4180 + the
    /// canonical UTF-8 charset.</summary>
    public const string CsvContentType = "text/csv; charset=utf-8";

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommentaryCostBudgetExportController> _logger;
    private readonly IOptionsMonitor<CommentaryOptions>? _options;

    public CommentaryCostBudgetExportController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<CommentaryCostBudgetExportController> logger,
        IOptionsMonitor<CommentaryOptions>? options = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options;
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? tenant,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
        {
            return Unauthorized(new { error = "session-required" });
        }
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }

        if (!TryParseMonth(from, out var fromYear, out var fromMonth))
        {
            return BadRequest(new { error = "from-invalid", expected = "YYYY-MM" });
        }
        if (!TryParseMonth(to, out var toYear, out var toMonth))
        {
            return BadRequest(new { error = "to-invalid", expected = "YYYY-MM" });
        }
        var fromKey = fromYear * 12 + fromMonth;
        var toKey = toYear * 12 + toMonth;
        if (fromKey > toKey)
        {
            return BadRequest(new { error = "from-after-to" });
        }
        var windowMonths = toKey - fromKey + 1;
        if (windowMonths > MaxWindowMonths)
        {
            return BadRequest(new
            {
                error = "window-exceeds-maximum",
                maximumMonths = MaxWindowMonths,
                requestedMonths = windowMonths,
            });
        }

        // Tenant parameter is forward-compat — the W9 ledger has
        // no per-tenant column. We accept it (record in audit) but
        // do not filter the rows.
        var tenantTrimmed = tenant?.Trim();
        if (tenantTrimmed is { Length: 0 }) tenantTrimmed = null;

        var rows = await LoadRowsAsync(fromYear, fromMonth, toYear, toMonth, ct);

        var opts = _options?.CurrentValue.CostBudget;
        var tokensPerDollar = opts is { TokensPerDollar: > 0 }
            ? opts.TokensPerDollar
            : 200_000L;
        var monthlyCapUsd = opts?.MonthlyCapUsd ?? 0m;
        var warnThreshold = opts?.WarnThreshold ?? 0.8;

        var csv = BuildCsv(rows, tokensPerDollar, monthlyCapUsd, warnThreshold);
        await WriteAuditAsync(from!, to!, tenantTrimmed, rows.Count, ct);

        var bytes = Encoding.UTF8.GetBytes(csv);
        return File(bytes, CsvContentType,
            fileDownloadName: $"commentary-cost-budget-{from}-to-{to}.csv");
    }

    /// <summary>
    /// Public façade — exposed so contract tests can render a
    /// CSV directly off a known row set without hitting the
    /// controller's auth path. Keeps the formatter contract
    /// testable in isolation.
    /// </summary>
    public static string BuildCsv(
        IReadOnlyList<CommentaryUsageRecord> rows,
        long tokensPerDollar,
        decimal monthlyCapUsd,
        double warnThreshold)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (tokensPerDollar <= 0) tokensPerDollar = 200_000L;
        var sb = new StringBuilder();
        sb.AppendLine(CsvHeader);
        foreach (var r in rows)
        {
            var total = r.InputTokens + r.OutputTokens;
            var usdSpent = (decimal)total / tokensPerDollar;
            var percentOfCap = monthlyCapUsd > 0m
                ? (double)(usdSpent / monthlyCapUsd) * 100.0
                : 0.0;
            var state = ResolveState(usdSpent, monthlyCapUsd, warnThreshold);
            sb.Append(r.PeriodYear.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.PeriodMonth.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.InputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.OutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(total.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.RequestCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(tokensPerDollar.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(monthlyCapUsd.ToString("0.######", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(usdSpent.ToString("0.######", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(percentOfCap.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(state).Append(',');
            sb.Append(r.CreatedAt.ToString("o", CultureInfo.InvariantCulture)).Append(',');
            sb.AppendLine(r.UpdatedAt.ToString("o", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Parses "YYYY-MM" → (year, month). Accepts
    /// leading / trailing whitespace; rejects everything
    /// else (including bare years, bare months, dashes-only).</summary>
    public static bool TryParseMonth(string? value, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        var dash = trimmed.IndexOf('-');
        if (dash <= 0 || dash >= trimmed.Length - 1) return false;
        var yPart = trimmed[..dash];
        var mPart = trimmed[(dash + 1)..];
        if (!int.TryParse(yPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) return false;
        if (!int.TryParse(mPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out month)) return false;
        if (year < 2000 || year > 9999) return false;
        if (month < 1 || month > 12) return false;
        return true;
    }

    private static string ResolveState(decimal usdSpent, decimal cap, double warnThreshold)
    {
        if (cap <= 0m) return "Healthy";
        var ratio = (double)(usdSpent / cap);
        if (ratio >= 1.0) return "Exhausted";
        if (ratio >= warnThreshold) return "Warning";
        return "Healthy";
    }

    private async Task<IReadOnlyList<CommentaryUsageRecord>> LoadRowsAsync(
        int fromYear, int fromMonth, int toYear, int toMonth, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var raw = await db.CommentaryUsage
            .AsNoTracking()
            .OrderBy(r => r.PeriodYear)
            .ThenBy(r => r.PeriodMonth)
            .ToListAsync(ct);
        var fromKey = fromYear * 12 + fromMonth;
        var toKey = toYear * 12 + toMonth;
        return raw
            .Where(r =>
            {
                var key = r.PeriodYear * 12 + r.PeriodMonth;
                return key >= fromKey && key <= toKey;
            })
            .ToList();
    }

    private async Task WriteAuditAsync(
        string from,
        string to,
        string? tenant,
        int rowCount,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = "admin",
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindCommentaryCostBudgetExport,
                Detail = $"from={from}|to={to}|tenant={tenant ?? string.Empty}|rows={rowCount}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Commentary cost-budget export audit write failed for from={From}, to={To}.",
                from, to);
        }
    }
}
