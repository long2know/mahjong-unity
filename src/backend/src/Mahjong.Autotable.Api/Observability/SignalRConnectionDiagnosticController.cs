using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 22 — Bishop. Per-tenant SignalR connection
/// snapshot maintained in-memory by the hub lifecycle hooks.
/// One <see cref="Entry"/> per active connection; the diagnostic
/// admin endpoint exposes the aggregate (transport breakdown,
/// per-group fan-out, last-ping spread).
///
/// <para>The registry is a process-local convenience — multi-
/// replica deployments emit per-replica metrics + the admin
/// dashboard aggregates client-side. For Wave 22 the surface
/// returns the single-replica snapshot which matches the
/// dominant local-dev / single-pod prod posture.</para>
/// </summary>
public sealed class SignalRConnectionRegistry
{
    public sealed class Entry
    {
        public string ConnectionId { get; init; } = string.Empty;
        public string TenantId { get; init; } = string.Empty;
        public string Group { get; init; } = string.Empty;
        public string Transport { get; init; } = string.Empty;
        public DateTime LastPingUtc { get; set; } = DateTime.UtcNow;
        public DateTime ConnectedAtUtc { get; init; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    public void Register(Entry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (string.IsNullOrWhiteSpace(e.ConnectionId))
        {
            throw new ArgumentException("ConnectionId required.", nameof(e));
        }
        _entries[e.ConnectionId] = e;
    }

    public bool Unregister(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) return false;
        return _entries.TryRemove(connectionId, out _);
    }

    public void UpdatePing(string connectionId, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(connectionId)) return;
        if (_entries.TryGetValue(connectionId, out var e))
        {
            e.LastPingUtc = utcNow;
        }
    }

    public IReadOnlyCollection<Entry> Snapshot() =>
        _entries.Values.ToArray();

    public int Count => _entries.Count;

    public void Clear() => _entries.Clear();
}

/// <summary>
/// Phase K Wave 22 — Bishop. Admin-gated SignalR connection
/// diagnostic endpoint. Surface:
/// <c>GET /api/admin/signalr/diagnostics?tenant=...</c>.
///
/// <para>Returns the per-tenant active-connection breakdown:
/// connections grouped by hub-group, per-group last-ping spread,
/// transport mix (websocket / longpolling / sse), and oldest
/// /newest connection timestamps. Operators use the trail to
/// distinguish "transport regression" from "hub-side stall" at a
/// glance.</para>
///
/// <para>Auth: 401 / 403 / 200. The endpoint is read-only and
/// does not mutate any state; the <c>X-Admin-Reason</c> header
/// is NOT required (matches the W21 audit-trail read pattern
/// where read-only diagnostic surfaces are gated by admin role
/// alone).</para>
/// </summary>
[ApiController]
[Route("api/admin/signalr/diagnostics")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class SignalRConnectionDiagnosticController : ControllerBase
{
    private readonly AuthCookieService _cookies;
    private readonly SignalRConnectionRegistry _registry;

    public SignalRConnectionDiagnosticController(
        AuthCookieService cookies,
        SignalRConnectionRegistry registry)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    [HttpGet]
    public async Task<IActionResult> GetDiagnostics(
        [FromQuery] string? tenant,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }

        var snapshot = _registry.Snapshot();
        IEnumerable<SignalRConnectionRegistry.Entry> filtered = snapshot;
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            var trimmed = tenant.Trim();
            filtered = snapshot.Where(e => string.Equals(e.TenantId, trimmed, StringComparison.Ordinal));
        }
        var rows = filtered.ToList();

        var byGroup = rows
            .GroupBy(e => string.IsNullOrEmpty(e.Group) ? "_default" : e.Group, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new
            {
                group = g.Key,
                count = g.Count(),
                oldestLastPing = g.Min(e => e.LastPingUtc),
                newestLastPing = g.Max(e => e.LastPingUtc),
            }).ToArray();

        var byTransport = rows
            .GroupBy(e => string.IsNullOrEmpty(e.Transport) ? "_unknown" : e.Transport, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return Ok(new
        {
            tenant = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim(),
            totalConnections = rows.Count,
            byGroup,
            byTransport,
            oldestConnectedAt = rows.Count == 0 ? (DateTime?)null : rows.Min(e => e.ConnectedAtUtc),
            newestConnectedAt = rows.Count == 0 ? (DateTime?)null : rows.Max(e => e.ConnectedAtUtc),
            oldestLastPing = rows.Count == 0 ? (DateTime?)null : rows.Min(e => e.LastPingUtc),
        });
    }
}
