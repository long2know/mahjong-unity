using System.Text.Json;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase J Wave 9 — Content-Security-Policy report sink (Apone, DevOps).
///
/// <para>Maps <c>POST /api/csp-report</c>. Accepts the two common CSP report
/// envelopes:</para>
///
/// <list type="bullet">
///   <item><b>Legacy (CSP 2):</b> <c>application/csp-report</c> body
///         <c>{"csp-report": { document-uri, violated-directive, ... }}</c>.
///         Hyphen-cased keys, single-report-per-POST.</item>
///   <item><b>Modern (Reporting API):</b> <c>application/reports+json</c>
///         body <c>[{ "type": "csp-violation", "body": { documentURL,
///         effectiveDirective, ... } }]</c>. camelCased keys, array of
///         reports.</item>
/// </list>
///
/// <para><b>Why this endpoint is deliberately permissive.</b> Browsers fire
/// CSP reports asynchronously from the violating page; the report POST is
/// authenticated with whatever cookies happen to be on the page (cross-site
/// SameSite=Lax means we usually get nothing). The endpoint therefore:
/// <list type="bullet">
///   <item>does NOT require authentication;</item>
///   <item>does NOT participate in the API token-bucket rate limit (the
///         Cloudflare / browser firing rate is bursty and dropping a report
///         is worse than logging a few extras);</item>
///   <item>caps each accepted payload at 32 KiB so a malicious caller can't
///         flood the DB;</item>
///   <item>returns <c>204 No Content</c> on every accepted shape so the
///         browser doesn't retry with backoff.</item>
/// </list></para>
///
/// <para><b>Schema source-of-truth:</b> <see cref="CspViolation"/>. The DB
/// row keeps both the parsed canonical fields (for aggregation queries)
/// and the raw JSON envelope (for forensics).</para>
/// </summary>
public static class CspReportEndpoint
{
    /// <summary>Configured route path for the report sink.</summary>
    public const string Path = "/api/csp-report";

    /// <summary>Maximum payload size accepted by the endpoint (32 KiB). Anything
    /// larger is rejected with 413; in practice Chromium's report bodies are
    /// under 4 KiB so the cap is purely defensive.</summary>
    public const int MaxPayloadBytes = 32 * 1024;

    /// <summary>
    /// Wires <c>POST /api/csp-report</c> into the pipeline. Idempotent;
    /// downstream callers register from Program.cs after middleware setup.
    /// </summary>
    public static IEndpointConventionBuilder MapCspReport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Path, async (HttpContext context) =>
        {
            // Cap the read so a malicious caller can't pin a worker thread
            // on an arbitrarily large body. ReadAtLeastAsync would block;
            // we read incrementally instead.
            var ms = new MemoryStream();
            var buffer = new byte[8 * 1024];
            int read;
            int total = 0;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                total += read;
                if (total > MaxPayloadBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                ms.Write(buffer, 0, read);
            }
            ms.Position = 0;

            if (ms.Length == 0)
            {
                return Results.NoContent();
            }

            var raw = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            var parsed = ParseReports(raw);

            // Persist every parsed report. Use a fresh scope so we don't tie
            // the request lifetime to whatever scope the endpoint inherits.
            var services = context.RequestServices;
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Mahjong.Autotable.Api.Observability.CspReport");

            // Verified identity only — the raw cookie is a bearer credential and must never be
            // persisted into a violation row.
            var playerId = context.GetPlayerIdOrNull();
            var ua = context.Request.Headers.UserAgent.ToString();
            if (ua.Length > 512) ua = ua[..512];

            foreach (var report in parsed)
            {
                var row = new CspViolation
                {
                    PlayerId = playerId,
                    DocumentUri = report.DocumentUri,
                    Referrer = report.Referrer,
                    ViolatedDirective = report.ViolatedDirective,
                    EffectiveDirective = report.EffectiveDirective,
                    OriginalPolicy = report.OriginalPolicy,
                    Disposition = report.Disposition,
                    BlockedUri = report.BlockedUri,
                    SourceFile = report.SourceFile,
                    LineNumber = report.LineNumber,
                    ColumnNumber = report.ColumnNumber,
                    ScriptSample = Truncate(report.ScriptSample, 256),
                    StatusCode = report.StatusCode,
                    UserAgent = ua,
                    RawJson = raw.Length > 8192 ? raw[..8192] : raw,
                    ReceivedAt = DateTime.UtcNow,
                };
                db.CspViolations.Add(row);

                // Structured warn — visible in Loki / Sentry via the
                // existing JsonConsole + Sentry breadcrumb integration.
                logger.LogWarning(
                    "CSP violation: directive={Directive} blocked={Blocked} document={Document} disposition={Disposition}",
                    report.EffectiveDirective ?? report.ViolatedDirective ?? "?",
                    report.BlockedUri ?? "?",
                    report.DocumentUri ?? "?",
                    report.Disposition ?? "enforce");
            }

            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (Exception ex)
            {
                // Never propagate DB errors back to the browser — it'd retry
                // with backoff and pollute the log. Log + swallow.
                logger.LogWarning(ex, "Failed to persist {Count} CSP violation row(s)", parsed.Count);
            }

            return Results.NoContent();
        });

    /// <summary>
    /// Parses a CSP report payload into the canonical
    /// <see cref="ParsedReport"/> shape. Accepts both the legacy
    /// <c>application/csp-report</c> envelope and the modern
    /// <c>application/reports+json</c> array. Internal so tests can
    /// exercise the parser independently of HTTP.
    /// </summary>
    internal static IReadOnlyList<ParsedReport> ParseReports(string raw)
    {
        var results = new List<ParsedReport>();
        if (string.IsNullOrWhiteSpace(raw)) return results;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Legacy: { "csp-report": { ... } }
                if (root.TryGetProperty("csp-report", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                {
                    results.Add(ParseLegacy(legacy));
                    return results;
                }
                // Single Reporting-API report submitted as an object instead of array.
                if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
                {
                    results.Add(ParseModern(bodyEl));
                    return results;
                }
                // Bare modern body (no envelope) — rare; treat as modern shape.
                results.Add(ParseModern(root));
                return results;
            }
            if (root.ValueKind == JsonValueKind.Array)
            {
                // Modern: [ { "type": "csp-violation", "body": { ... } }, ... ]
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
                    {
                        results.Add(ParseModern(body));
                    }
                    else
                    {
                        results.Add(ParseModern(item));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed envelope. Don't fail the request — the canonical
            // forensic payload (RawJson) is still captured by the caller.
            results.Add(new ParsedReport { OriginalPolicy = null });
        }
        return results;
    }

    private static ParsedReport ParseLegacy(JsonElement el) => new()
    {
        DocumentUri = GetString(el, "document-uri"),
        Referrer = GetString(el, "referrer"),
        ViolatedDirective = GetString(el, "violated-directive"),
        EffectiveDirective = GetString(el, "effective-directive"),
        OriginalPolicy = GetString(el, "original-policy"),
        Disposition = GetString(el, "disposition"),
        BlockedUri = GetString(el, "blocked-uri"),
        SourceFile = GetString(el, "source-file"),
        LineNumber = GetInt(el, "line-number"),
        ColumnNumber = GetInt(el, "column-number"),
        ScriptSample = GetString(el, "script-sample"),
        StatusCode = GetInt(el, "status-code"),
    };

    private static ParsedReport ParseModern(JsonElement el) => new()
    {
        DocumentUri = GetString(el, "documentURL") ?? GetString(el, "document-uri"),
        Referrer = GetString(el, "referrer"),
        ViolatedDirective = GetString(el, "effectiveDirective") ?? GetString(el, "violated-directive"),
        EffectiveDirective = GetString(el, "effectiveDirective") ?? GetString(el, "effective-directive"),
        OriginalPolicy = GetString(el, "originalPolicy") ?? GetString(el, "original-policy"),
        Disposition = GetString(el, "disposition"),
        BlockedUri = GetString(el, "blockedURL") ?? GetString(el, "blocked-uri"),
        SourceFile = GetString(el, "sourceFile") ?? GetString(el, "source-file"),
        LineNumber = GetInt(el, "lineNumber") ?? GetInt(el, "line-number"),
        ColumnNumber = GetInt(el, "columnNumber") ?? GetInt(el, "column-number"),
        ScriptSample = GetString(el, "sample") ?? GetString(el, "script-sample"),
        StatusCode = GetInt(el, "statusCode") ?? GetInt(el, "status-code"),
    };

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length > max ? value[..max] : value;
    }

    /// <summary>Parsed canonical CSP report (subset of the union of CSP 2 +
    /// Reporting API field names). Internal — only the endpoint + tests
    /// consume it.</summary>
    internal sealed class ParsedReport
    {
        public string? DocumentUri { get; set; }
        public string? Referrer { get; set; }
        public string? ViolatedDirective { get; set; }
        public string? EffectiveDirective { get; set; }
        public string? OriginalPolicy { get; set; }
        public string? Disposition { get; set; }
        public string? BlockedUri { get; set; }
        public string? SourceFile { get; set; }
        public int? LineNumber { get; set; }
        public int? ColumnNumber { get; set; }
        public string? ScriptSample { get; set; }
        public int? StatusCode { get; set; }
    }
}
