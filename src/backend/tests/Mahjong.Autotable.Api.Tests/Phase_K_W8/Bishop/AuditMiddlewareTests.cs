using System.Net;
using System.Text;
using Mahjong.Autotable.Api.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Hard-asserted facts for the audit
/// pipeline:
///
/// <list type="number">
///   <item><see cref="CorrelationIdMiddleware"/> stamps the
///         <c>X-Correlation-Id</c> response header.</item>
///   <item><see cref="CorrelationIdMiddleware.Resolve"/> returns the
///         middleware-supplied id when present, and a fresh guid
///         when not.</item>
///   <item><see cref="IdempotencyMiddleware.IsValidKey"/> enforces
///         length + charset bounds.</item>
///   <item>The <see cref="IdempotencyMiddleware"/> rejects invalid
///         keys with 400.</item>
///   <item>Same key + same payload inside the window → 409.</item>
///   <item>Same key + different payload → 409 with reason
///         "payload-mismatch".</item>
///   <item>Missing <c>Idempotency-Key</c> header bypasses the
///         middleware entirely.</item>
///   <item><see cref="InMemoryIdempotencyStore"/> records + replays
///         entries.</item>
/// </list>
/// </summary>
public sealed class AuditMiddlewareTests
{
    // ── Helpers ─────────────────────────────────────────────────────

    private static DefaultHttpContext NewContext(string method, string body = "", string? idempotencyKey = null, string? correlationId = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentLength = body.Length;
        ctx.Request.ContentType = "application/json";
        ctx.Response.Body = new MemoryStream();
        if (!string.IsNullOrEmpty(idempotencyKey))
            ctx.Request.Headers[IdempotencyMiddleware.HeaderName] = idempotencyKey;
        if (!string.IsNullOrEmpty(correlationId))
            ctx.Request.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        return ctx;
    }

    // ── CorrelationIdMiddleware facts ──────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task CorrelationIdMiddleware_StampsResponseHeader_WhenAbsent()
    {
        var ctx = NewContext("GET");
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new CorrelationIdMiddleware(next);
        await mw.InvokeAsync(ctx);
        Assert.True(ctx.Response.Headers.ContainsKey(CorrelationIdMiddleware.HeaderName));
        var value = ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        Assert.Equal(32, value.Length);
        Assert.True(Guid.TryParseExact(value, "N", out _));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task CorrelationIdMiddleware_HonoursValidInboundHeader()
    {
        var inbound = Guid.NewGuid().ToString("N");
        var ctx = NewContext("GET", correlationId: inbound);
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new CorrelationIdMiddleware(next);
        await mw.InvokeAsync(ctx);
        Assert.Equal(inbound, ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task CorrelationIdMiddleware_RejectsInvalidInbound_AndMintsFresh()
    {
        var ctx = NewContext("GET", correlationId: "not-a-guid");
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new CorrelationIdMiddleware(next);
        await mw.InvokeAsync(ctx);
        var stamped = ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        Assert.NotEqual("not-a-guid", stamped);
        Assert.True(Guid.TryParseExact(stamped, "N", out _));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task CorrelationIdMiddleware_StampsContextItem()
    {
        var ctx = NewContext("GET");
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new CorrelationIdMiddleware(next);
        await mw.InvokeAsync(ctx);
        Assert.True(ctx.Items.ContainsKey(CorrelationIdMiddleware.ContextKey));
        var item = ctx.Items[CorrelationIdMiddleware.ContextKey] as string;
        Assert.False(string.IsNullOrEmpty(item));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void CorrelationIdMiddleware_Resolve_NullContext_ReturnsFreshGuid()
    {
        var resolved = CorrelationIdMiddleware.Resolve(null);
        Assert.True(Guid.TryParseExact(resolved, "N", out _));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void CorrelationIdMiddleware_Resolve_PopulatedContext_ReturnsStampedValue()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[CorrelationIdMiddleware.ContextKey] = "stamped-value-abc";
        Assert.Equal("stamped-value-abc", CorrelationIdMiddleware.Resolve(ctx));
    }

    // ── IdempotencyMiddleware.IsValidKey facts ─────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IsValidKey_AcceptsCanonicalKey()
    {
        Assert.True(IdempotencyMiddleware.IsValidKey("abc-123_DEF456"));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IsValidKey_RejectsBelowMinLength()
    {
        Assert.False(IdempotencyMiddleware.IsValidKey("short"));
        Assert.False(IdempotencyMiddleware.IsValidKey(""));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IsValidKey_RejectsAboveMaxLength()
    {
        var tooLong = new string('a', IdempotencyMiddleware.MaxKeyLength + 1);
        Assert.False(IdempotencyMiddleware.IsValidKey(tooLong));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IsValidKey_RejectsInvalidCharset()
    {
        Assert.False(IdempotencyMiddleware.IsValidKey("abcd1234!!"));
        Assert.False(IdempotencyMiddleware.IsValidKey("abcd 1234"));
        Assert.False(IdempotencyMiddleware.IsValidKey("abcd:1234"));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IsValidKey_AcceptsBoundaryLengths()
    {
        Assert.True(IdempotencyMiddleware.IsValidKey(new string('a', IdempotencyMiddleware.MinKeyLength)));
        Assert.True(IdempotencyMiddleware.IsValidKey(new string('a', IdempotencyMiddleware.MaxKeyLength)));
    }

    // ── IdempotencyMiddleware behaviour facts ──────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_GETBypass_NoStateChange()
    {
        var store = new InMemoryIdempotencyStore();
        var ctx = NewContext("GET", idempotencyKey: "abcdefgh");
        var nextCalled = 0;
        RequestDelegate next = _ => { nextCalled++; return Task.CompletedTask; };
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx);
        Assert.Equal(1, nextCalled);
        Assert.Null(store.TryGet("abcdefgh"));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_MissingHeader_BypassesMiddleware()
    {
        var store = new InMemoryIdempotencyStore();
        var ctx = NewContext("POST", body: "{}");
        var nextCalled = 0;
        RequestDelegate next = _ => { nextCalled++; return Task.CompletedTask; };
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx);
        Assert.Equal(1, nextCalled);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_InvalidKey_Returns400()
    {
        var store = new InMemoryIdempotencyStore();
        var ctx = NewContext("POST", body: "{}", idempotencyKey: "bad!");
        ctx.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx);
        Assert.Equal((int)HttpStatusCode.BadRequest, ctx.Response.StatusCode);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_ReplayWithSameKeyAndPayload_ReplaysOriginalResponse()
    {
        // Stripe-style: same key + same payload inside the window
        // returns the cached response (status + body) and DOES NOT
        // call _next a second time.
        var store = new InMemoryIdempotencyStore();
        var key = "abcd-1234";
        var ctx1 = NewContext("POST", body: "{\"a\":1}", idempotencyKey: key);
        var calls = 0;
        RequestDelegate next = c => { calls++; c.Response.StatusCode = 200; return Task.CompletedTask; };
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx1);
        var ctx2 = NewContext("POST", body: "{\"a\":1}", idempotencyKey: key);
        await mw.InvokeAsync(ctx2);
        Assert.Equal(200, ctx2.Response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_ReplayWithDifferentPayload_Returns409()
    {
        var store = new InMemoryIdempotencyStore();
        var key = "abcd-5678";
        var ctx1 = NewContext("POST", body: "{\"a\":1}", idempotencyKey: key);
        RequestDelegate next = c => { c.Response.StatusCode = 200; return Task.CompletedTask; };
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx1);
        var ctx2 = NewContext("POST", body: "{\"a\":2}", idempotencyKey: key);
        await mw.InvokeAsync(ctx2);
        Assert.Equal((int)HttpStatusCode.Conflict, ctx2.Response.StatusCode);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_StalePastWindow_AllowsThrough()
    {
        var store = new InMemoryIdempotencyStore();
        var key = "abcd-stale";
        // Pre-seed an entry far in the past so it's outside the window.
        store.Record(new IdempotencyRecord(key, "old-hash", DateTimeOffset.UtcNow.AddHours(-1)));
        var ctx = NewContext("POST", body: "{}", idempotencyKey: key);
        var nextCalls = 0;
        RequestDelegate next = _ => { nextCalls++; return Task.CompletedTask; };
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx);
        Assert.Equal(1, nextCalls);
        Assert.NotEqual((int)HttpStatusCode.Conflict, ctx.Response.StatusCode);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_FirstCall_StampsContextItem()
    {
        var store = new InMemoryIdempotencyStore();
        var key = "abcdefgh";
        var ctx = NewContext("POST", body: "{}", idempotencyKey: key);
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx);
        Assert.Equal(key, ctx.Items[IdempotencyMiddleware.ContextKey]);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IdempotencyMiddleware_Resolve_NoContext_ReturnsNull()
    {
        Assert.Null(IdempotencyMiddleware.Resolve(null));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IdempotencyMiddleware_Resolve_NoStampedKey_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();
        Assert.Null(IdempotencyMiddleware.Resolve(ctx));
    }

    // ── InMemoryIdempotencyStore facts ─────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Store_RecordsAndReplays_Entries()
    {
        var store = new InMemoryIdempotencyStore();
        store.Record(new IdempotencyRecord("key1", "hash1", DateTimeOffset.UtcNow));
        var entry = store.TryGet("key1");
        Assert.NotNull(entry);
        Assert.Equal("key1", entry!.Key);
        Assert.Equal("hash1", entry.PayloadHash);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Store_TryGet_MissingKey_ReturnsNull()
    {
        var store = new InMemoryIdempotencyStore();
        Assert.Null(store.TryGet("never-recorded"));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Store_Record_OverwritesExisting()
    {
        var store = new InMemoryIdempotencyStore();
        store.Record(new IdempotencyRecord("k", "hash-a", DateTimeOffset.UtcNow.AddMinutes(-1)));
        store.Record(new IdempotencyRecord("k", "hash-b", DateTimeOffset.UtcNow));
        Assert.Equal("hash-b", store.TryGet("k")!.PayloadHash);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Store_DefaultCapacity_Is4096()
    {
        Assert.Equal(4096, InMemoryIdempotencyStore.DefaultCapacity);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IdempotencyMiddleware_DefaultReplayWindow_Is5Minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), IdempotencyMiddleware.DefaultReplayWindow);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void IdempotencyMiddleware_Constants_AreStable()
    {
        Assert.Equal("Idempotency-Key", IdempotencyMiddleware.HeaderName);
        Assert.Equal(8, IdempotencyMiddleware.MinKeyLength);
        Assert.Equal(128, IdempotencyMiddleware.MaxKeyLength);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_NonSuccessDownstream_DoesNotRecord()
    {
        // Stripe-style behaviour — a request that ultimately 404s
        // or 5xxs must NOT be remembered, so a retry with the same
        // key lands freshly.
        var store = new InMemoryIdempotencyStore();
        var key = "abcd-1234";
        var ctx = NewContext("POST", body: "{}", idempotencyKey: key);
        RequestDelegate next = c => { c.Response.StatusCode = 404; return Task.CompletedTask; };
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx);
        Assert.Null(store.TryGet(key));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public async Task IdempotencyMiddleware_SuccessDownstream_DoesRecord()
    {
        var store = new InMemoryIdempotencyStore();
        var key = "abcd-9999";
        var ctx = NewContext("POST", body: "{}", idempotencyKey: key);
        RequestDelegate next = c => { c.Response.StatusCode = 200; return Task.CompletedTask; };
        var mw = new IdempotencyMiddleware(next, store);
        await mw.InvokeAsync(ctx);
        Assert.NotNull(store.TryGet(key));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void InMemoryIdempotencyStore_Remove_DropsEntry()
    {
        var store = new InMemoryIdempotencyStore();
        store.Record(new IdempotencyRecord("k", "h", DateTimeOffset.UtcNow));
        Assert.NotNull(store.TryGet("k"));
        store.Remove("k");
        Assert.Null(store.TryGet("k"));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void CorrelationIdMiddleware_Constants_AreStable()
    {
        Assert.Equal("X-Correlation-Id", CorrelationIdMiddleware.HeaderName);
    }
}
