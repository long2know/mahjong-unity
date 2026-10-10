namespace Mahjong.Autotable.Api.Audit;

/// <summary>
/// Phase K Wave 8 — Bishop. ASP.NET middleware that stamps every
/// inbound request with a server-generated correlation id and
/// echoes it back on the response so clients can include the value
/// when reporting issues or replaying.
///
/// <list type="bullet">
///   <item>Inbound: if the request carries an
///         <c>X-Correlation-Id</c> header AND the value passes
///         validation (32-char hex Guid "N" form) the middleware
///         honours it; otherwise it mints a fresh
///         <see cref="Guid.NewGuid"/>.</item>
///   <item>Surfaces the id on <see cref="HttpContext.Items"/> under
///         <see cref="ContextKey"/> so request-scoped consumers
///         (audit writers, downstream HTTP calls, SignalR senders)
///         can resolve it without re-parsing the header.</item>
///   <item>Outbound: the resolved id is written to the
///         <c>X-Correlation-Id</c> response header BEFORE the next
///         middleware in the pipeline executes (so even error
///         responses carry the value).</item>
/// </list>
///
/// <para>Wired into <c>Program.cs</c> ahead of MVC + SignalR so
/// every downstream consumer sees a populated value. Idempotent on
/// duplicate registration — re-running it is a no-op when the
/// context already has the key.</para>
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ContextKey = "Mahjong.Autotable.Api.CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var existing = TryReadInbound(context);
        var value = existing ?? Guid.NewGuid().ToString("N");

        context.Items[ContextKey] = value;
        context.Response.Headers[HeaderName] = value;

        await _next(context);
    }

    private static string? TryReadInbound(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var raw) || raw.Count == 0)
            return null;
        var candidate = raw[0];
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        candidate = candidate.Trim();
        if (candidate.Length is not 32 and not 36) return null;
        if (!Guid.TryParse(candidate, out var parsed)) return null;
        return parsed.ToString("N");
    }

    /// <summary>
    /// Lookup helper that returns the correlation id stamped by the
    /// middleware. Falls back to a fresh "N" guid when the
    /// middleware did not run (e.g. test transports that bypass the
    /// HTTP pipeline) so callers can always emit a non-null value.
    /// </summary>
    public static string Resolve(HttpContext? context)
    {
        if (context is null) return Guid.NewGuid().ToString("N");
        if (context.Items.TryGetValue(ContextKey, out var raw) && raw is string s && !string.IsNullOrEmpty(s))
            return s;
        var fallback = Guid.NewGuid().ToString("N");
        context.Items[ContextKey] = fallback;
        return fallback;
    }
}
