using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Autotable.Api.Audit;

/// <summary>
/// Phase K Wave 8 — Bishop. ASP.NET middleware that enforces RFC-7231
/// idempotency for unsafe HTTP methods (POST + PUT + PATCH). A
/// client supplies an <c>Idempotency-Key</c> header on the original
/// request; if the same key + payload hash arrives again inside the
/// 5-minute replay window the second request is rejected with
/// <c>409 Conflict</c> + a structured envelope identifying the
/// cached response.
///
/// <list type="bullet">
///   <item>Requests without an <c>Idempotency-Key</c> header skip
///         the middleware entirely (the surface is opt-in).</item>
///   <item>Keys MUST be 8..128 characters, alphanumeric +
///         <c>-</c> / <c>_</c>. Invalid keys produce
///         <c>400 Bad Request</c>.</item>
///   <item>The payload hash is SHA-256 over the request body bytes,
///         lower-case hex. Same key + same hash inside the replay
///         window → 409. Same key + different hash → 409 with
///         <c>reason: "payload-mismatch"</c>.</item>
///   <item>Cache entries are pruned on every read so the dictionary
///         stays bounded under steady-state traffic.</item>
/// </list>
///
/// <para>The cache is in-process — distributed replay protection is
/// out of scope for Phase K Wave 8 (sticky load-balancing covers the
/// single-replica deployment). Phase L will swap the
/// <see cref="IIdempotencyStore"/> seam for a Redis-backed
/// implementation.</para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    /// <summary>HTTP header carrying the client-supplied key.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>HttpContext.Items key under which the resolved
    /// idempotency-key is surfaced for downstream consumers (audit
    /// writers).</summary>
    public const string ContextKey = "Mahjong.Autotable.Api.IdempotencyKey";

    /// <summary>Minimum allowed key length. Below this the
    /// middleware rejects with 400.</summary>
    public const int MinKeyLength = 8;

    /// <summary>Maximum allowed key length (also matches the audit
    /// column max length).</summary>
    public const int MaxKeyLength = 128;

    /// <summary>Default replay window — 5 minutes per spec.</summary>
    public static readonly TimeSpan DefaultReplayWindow = TimeSpan.FromMinutes(5);

    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly TimeSpan _replayWindow;

    public IdempotencyMiddleware(RequestDelegate next, IIdempotencyStore store, TimeSpan? replayWindow = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _replayWindow = replayWindow ?? DefaultReplayWindow;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if (!IsUnsafeMethod(method))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var raw) || raw.Count == 0)
        {
            await _next(context);
            return;
        }

        var key = (raw[0] ?? string.Empty).Trim();
        if (!IsValidKey(key))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid-idempotency-key",
                detail = $"Idempotency-Key header must be {MinKeyLength}..{MaxKeyLength} characters " +
                         "matching [A-Za-z0-9_-].",
            });
            return;
        }

        var payloadHash = await ComputePayloadHashAsync(context);
        context.Items[ContextKey] = key;

        var existing = _store.TryGet(key);
        if (existing is not null)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - existing.RecordedAt <= _replayWindow)
            {
                if (string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    // Stripe-style replay: return the cached
                    // response status + body so the second request
                    // is observationally identical to the first.
                    context.Response.StatusCode = existing.StatusCode > 0 ? existing.StatusCode : StatusCodes.Status200OK;
                    if (!string.IsNullOrEmpty(existing.ContentType))
                        context.Response.ContentType = existing.ContentType;
                    if (!string.IsNullOrEmpty(existing.ResponseBody))
                        await context.Response.WriteAsync(existing.ResponseBody);
                    return;
                }
                // Same key, different payload → reject with 409.
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "idempotency-replay-rejected",
                    reason = "payload-mismatch",
                    idempotencyKey = key,
                    firstSeenAt = existing.RecordedAt,
                    correlationId = CorrelationIdMiddleware.Resolve(context),
                });
                return;
            }
            // Stale entry — fall through and let _store.Record() overwrite.
        }

        // Capture the downstream response so we can replay it on
        // subsequent retries with the same key + payload.
        var originalBody = context.Response.Body;
        using var captureStream = new MemoryStream();
        context.Response.Body = captureStream;
        try
        {
            await _next(context);
            captureStream.Position = 0;
            var capturedBody = await new StreamReader(captureStream, Encoding.UTF8).ReadToEndAsync();
            captureStream.Position = 0;
            await captureStream.CopyToAsync(originalBody);

            if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300)
            {
                _store.Record(new IdempotencyRecord(
                    Key: key,
                    PayloadHash: payloadHash,
                    RecordedAt: DateTimeOffset.UtcNow,
                    StatusCode: context.Response.StatusCode,
                    ContentType: context.Response.ContentType ?? string.Empty,
                    ResponseBody: capturedBody));
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool IsUnsafeMethod(string method) =>
        string.Equals(method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, HttpMethods.Put, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, HttpMethods.Patch, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, HttpMethods.Delete, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (key.Length < MinKeyLength || key.Length > MaxKeyLength) return false;
        foreach (var c in key)
        {
            var ok = (c >= 'A' && c <= 'Z')
                  || (c >= 'a' && c <= 'z')
                  || (c >= '0' && c <= '9')
                  || c is '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    private static async Task<string> ComputePayloadHashAsync(HttpContext context)
    {
        // Enable buffering so downstream readers (MVC binder) can
        // re-read the body after we've consumed it for hashing.
        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        context.Request.Body.Position = 0;

        var bytes = ms.ToArray();
        // Treat empty body deterministically — common for /forfeit + similar endpoints.
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Resolve the idempotency-key the middleware stamped
    /// onto the context, or null when the request didn't carry one.
    /// Used by audit writers to populate
    /// <see cref="Mahjong.Autotable.Api.Data.Entities.ReconnectAuditEntry.IdempotencyKey"/>.</summary>
    public static string? Resolve(HttpContext? context)
    {
        if (context is null) return null;
        if (context.Items.TryGetValue(ContextKey, out var raw) && raw is string s && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }
}

/// <summary>
/// Phase K Wave 8 — Bishop. Storage seam for the
/// <see cref="IdempotencyMiddleware"/>. The default in-process
/// implementation is registered as a singleton; Phase L can swap to
/// Redis without touching the middleware.
/// </summary>
public interface IIdempotencyStore
{
    IdempotencyRecord? TryGet(string key);
    void Record(IdempotencyRecord record);
    void Remove(string key);
}

public sealed record IdempotencyRecord(
    string Key,
    string PayloadHash,
    DateTimeOffset RecordedAt,
    int StatusCode = 200,
    string ContentType = "",
    string ResponseBody = "");

/// <summary>
/// Phase K Wave 8 — Bishop. In-process LRU-bounded idempotency
/// cache. Bounded at 4096 entries to cap memory at ~600 KB worst
/// case. Eviction policy: when capacity is hit, the oldest entries
/// (by RecordedAt) are pruned in bulk down to 75%.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    public const int DefaultCapacity = 4096;

    private readonly ConcurrentDictionary<string, IdempotencyRecord> _map = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly object _evictGate = new();

    public InMemoryIdempotencyStore(int? capacity = null)
    {
        _capacity = capacity ?? DefaultCapacity;
        if (_capacity < 32) _capacity = 32;
    }

    public IdempotencyRecord? TryGet(string key) =>
        _map.TryGetValue(key, out var rec) ? rec : null;

    public void Record(IdempotencyRecord record)
    {
        _map[record.Key] = record;
        MaybeEvict();
    }

    public void Remove(string key) => _map.TryRemove(key, out _);

    private void MaybeEvict()
    {
        if (_map.Count < _capacity) return;
        lock (_evictGate)
        {
            if (_map.Count < _capacity) return;
            var keep = (int)(_capacity * 0.75);
            var toDrop = _map.Count - keep;
            if (toDrop <= 0) return;
            foreach (var entry in _map.Values.OrderBy(v => v.RecordedAt).Take(toDrop))
                _map.TryRemove(entry.Key, out _);
        }
    }
}
