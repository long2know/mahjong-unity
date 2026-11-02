using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Bishop;

/// <summary>
/// Phase K Wave 22 — Bishop. Tests for the W22 JWT emergency
/// revoke admin endpoint: auth gate, X-Admin-Reason validation,
/// tenant/kid parameter validation, idempotency, audit row
/// writes, metric increment, JWKS cache invalidation.
/// </summary>
[Collection("DbSerial")]
public sealed class JwtEmergencyRevokeControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w22-revoke-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public JwtEmergencyRevokeControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w22-revoke-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<(AuthCookieService, HttpContext)> MakeSessionAsync(string role = "admin")
    {
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var issue = new DefaultHttpContext();
        var s = await cookies.IssueAsync(issue, $"p-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={s.Token}";
        return (cookies, ctx);
    }

    private JwtEmergencyRevokeController MakeController(
        HttpContext ctx, AuthCookieService cookies,
        string? reason = "incident-2026-04-01",
        JwksCacheService? cache = null,
        JwtEmergencyRevokeMetrics? metrics = null)
    {
        var c = new JwtEmergencyRevokeController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            cache,
            metrics);
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        if (reason is not null) ctx.Request.Headers[JwtEmergencyRevokeController.AdminReasonHeader] = reason;
        return c;
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.Revoke("t1", "k1", CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.Revoke("t1", "k1", CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: null);
        var r = await c.Revoke("t1", "k1", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_BlankReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: "   ");
        var r = await c.Revoke("t1", "k1", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: new string('x', JwtEmergencyRevokeController.MaxAdminReasonLength + 1));
        var r = await c.Revoke("t1", "k1", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_MissingTenant_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Revoke(null, "k1", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_BlankTenant_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Revoke("  ", "k1", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_TenantTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Revoke(new string('t', JwtEmergencyRevokeController.MaxTenantLength + 1), "k1", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_MissingKid_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Revoke("t1", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_BlankKid_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Revoke("t1", " ", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_KidTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Revoke("t1", new string('k', JwtEmergencyRevokeController.MaxKidLength + 1), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_HappyPath_Returns200()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).Revoke("tenant-a", "kid-7", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_PersistsRow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Revoke("tenant-a", "kid-7", CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await db.JwtEmergencyRevokedKids.FirstOrDefaultAsync(r => r.TenantId == "tenant-a" && r.Kid == "kid-7"));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_IsIdempotent_ReturnsIdempotentTrue()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Revoke("tenant-a", "kid-7", CancellationToken.None);
        var (cookies2, ctx2) = await MakeSessionAsync();
        var r = await MakeController(ctx2, cookies2).Revoke("tenant-a", "kid-7", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.JwtEmergencyRevokedKids.CountAsync(r => r.TenantId == "tenant-a" && r.Kid == "kid-7"));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_EmitsAuditRow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies, reason: "key-leak-2026-04-01")
            .Revoke("tenant-a", "kid-7", CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries.AsNoTracking()
            .FirstAsync(e => e.Kind == ReconnectAuditEntry.KindJwtEmergencyRevoke);
        Assert.Contains("tenant-a", row.Detail ?? string.Empty);
        Assert.Contains("kid-7", row.Detail ?? string.Empty);
        Assert.Contains("key-leak-2026-04-01", row.Detail ?? string.Empty);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_IncrementsMetric()
    {
        var metrics = new JwtEmergencyRevokeMetrics();
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies, metrics: metrics).Revoke("tenant-a", "kid-7", CancellationToken.None);
        Assert.Equal(1, metrics.Get("tenant-a"));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_IncrementsMetricOnIdempotentReplay()
    {
        var metrics = new JwtEmergencyRevokeMetrics();
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies, metrics: metrics).Revoke("tenant-a", "kid-7", CancellationToken.None);
        var (cookies2, ctx2) = await MakeSessionAsync();
        await MakeController(ctx2, cookies2, metrics: metrics).Revoke("tenant-a", "kid-7", CancellationToken.None);
        Assert.Equal(2, metrics.Get("tenant-a"));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_WithJwksCache_DoesNotThrow()
    {
        var cache = new JwksCacheService(new MemoryCache(new MemoryCacheOptions()));
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies, cache: cache).Revoke("tenant-a", "kid-7", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        // Invalidate is a no-op on an empty cache; the test
        // verifies the integration wiring compiles + executes
        // without exception against a real JwksCacheService.
        cache.Invalidate();
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Revoke_DifferentTenantSameKid_TreatedSeparately()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        await MakeController(ctx, cookies).Revoke("tenant-a", "kid-7", CancellationToken.None);
        var (cookies2, ctx2) = await MakeSessionAsync();
        await MakeController(ctx2, cookies2).Revoke("tenant-b", "kid-7", CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.JwtEmergencyRevokedKids.CountAsync(r => r.Kid == "kid-7"));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Metrics_PrometheusEmitContainsCounter()
    {
        var metrics = new JwtEmergencyRevokeMetrics();
        metrics.Increment("tenant-x");
        metrics.Increment("tenant-x");
        var sb = new System.Text.StringBuilder();
        metrics.AppendPrometheus(sb);
        Assert.Contains(JwtEmergencyRevokeMetrics.MetricName, sb.ToString());
        Assert.Contains("tenant-x", sb.ToString());
        Assert.Contains("} 2", sb.ToString());
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Metrics_UnknownTenantBucketsToSentinel()
    {
        var metrics = new JwtEmergencyRevokeMetrics();
        metrics.Increment(null);
        Assert.Equal(1, metrics.Get(JwtEmergencyRevokeMetrics.UnknownTenantBucket));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Metrics_SnapshotIndependentOfMutations()
    {
        var metrics = new JwtEmergencyRevokeMetrics();
        metrics.Increment("t1");
        var snap = metrics.Snapshot();
        metrics.Increment("t1");
        Assert.Equal(1, snap["t1"]);
    }
}
