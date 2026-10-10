using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Replays;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Contract tests for the new
/// <see cref="ReplayStoreIntegrityAuditController"/>. Covers:
/// admin auth gate (401/403), window validation (missing /
/// reversed / oversize), per-tenant grouping, checksum
/// determinism, tenant filtering, and audit-row side-effects.
/// </summary>
[Collection("DbSerial")]
public sealed class ReplayStoreIntegrityAuditControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w19-replay-audit-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public ReplayStoreIntegrityAuditControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w19-replay-{Guid.NewGuid():N}.sqlite");
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

    private ReplayStoreIntegrityAuditController MakeController(
        HttpContext httpContext, AuthCookieService cookies)
    {
        var c = new ReplayStoreIntegrityAuditController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReplayStoreIntegrityAuditController>.Instance);
        c.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return c;
    }

    private async Task<(AuthCookieService cookies, HttpContext context)> MakeSessionAsync(string role = "admin")
    {
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var issueContext = new DefaultHttpContext();
        var session = await cookies.IssueAsync(issueContext, $"player-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var resolveContext = new DefaultHttpContext();
        resolveContext.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={session.Token}";
        return (cookies, resolveContext);
    }

    private async Task SeedAsync(params (string tenant, DateTime ingested)[] rows)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (tenant, ingested) in rows)
        {
            db.Replays.Add(new ReplayRecord
            {
                ReplayId = Guid.NewGuid().ToString("N"),
                GameId = Guid.NewGuid(),
                CompletedAt = ingested.AddMinutes(-5),
                TurnCount = 42,
                IngestedAt = ingested,
                TenantId = tenant,
                CompressedPayload = new byte[] { 1, 2, 3 },
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_MissingFrom_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(null, DateTime.UtcNow, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_MissingTo_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(DateTime.UtcNow.AddDays(-1), null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_ReversedWindow_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(DateTime.UtcNow, DateTime.UtcNow.AddDays(-1), null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_OversizeWindow_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(
            DateTime.UtcNow.AddDays(-200),
            DateTime.UtcNow,
            null,
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_EmptyStore_ReturnsOkWithZeroRows()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow,
            null,
            CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"totalRowCount\":0", json);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_GroupsByTenant()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            ("tenant-a", now.AddMinutes(-30)),
            ("tenant-a", now.AddMinutes(-20)),
            ("tenant-b", now.AddMinutes(-10)));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(now.AddHours(-1), now.AddHours(1), null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"tenantId\":\"tenant-a\"", json);
        Assert.Contains("\"tenantId\":\"tenant-b\"", json);
        Assert.Contains("\"totalRowCount\":3", json);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_TenantFilter_RestrictsResultSet()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            ("tenant-a", now.AddMinutes(-30)),
            ("tenant-b", now.AddMinutes(-10)));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(now.AddHours(-1), now.AddHours(1), "tenant-a", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"totalRowCount\":1", json);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_ChecksumIsDeterministicAcrossCalls()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            ("tenant-a", now.AddMinutes(-30)),
            ("tenant-a", now.AddMinutes(-20)));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r1 = await c.Audit(now.AddHours(-1), now.AddHours(1), null, CancellationToken.None);
        var (cookies2, ctx2) = await MakeSessionAsync();
        var c2 = MakeController(ctx2, cookies2);
        var r2 = await c2.Audit(now.AddHours(-1), now.AddHours(1), null, CancellationToken.None);
        var json1 = JsonSerializer.Serialize(((OkObjectResult)r1).Value);
        var json2 = JsonSerializer.Serialize(((OkObjectResult)r2).Value);
        // Strip the "from"/"to" timestamps (they may differ
        // by sub-second). Compare the globalChecksum.
        var cs1 = ExtractGlobalChecksum(json1);
        var cs2 = ExtractGlobalChecksum(json2);
        Assert.False(string.IsNullOrEmpty(cs1));
        Assert.Equal(cs1, cs2);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_WritesAuditRow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Audit(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, CancellationToken.None);
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var n = await db.ReconnectAuditEntries
            .CountAsync(e => e.Kind == ReconnectAuditEntry.KindReplayIntegrityAudit);
        Assert.Equal(1, n);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_RowsOutsideWindow_NotCounted()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(("tenant-a", now.AddDays(-30)));
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit(now.AddHours(-1), now, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"totalRowCount\":0", json);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task Audit_MaxWindowConstantIsNinetyDays()
    {
        Assert.Equal(90, ReplayStoreIntegrityAuditController.MaxWindowDays);
        await Task.CompletedTask;
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void ProjectionFor_IsDeterministicForSameInputs()
    {
        var id = "abc123";
        var g = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1 = ReplayStoreIntegrityAuditController.ProjectionFor(id, g, t, 5, t);
        var p2 = ReplayStoreIntegrityAuditController.ProjectionFor(id, g, t, 5, t);
        Assert.Equal(p1, p2);
    }

    private static string ExtractGlobalChecksum(string json)
    {
        const string key = "\"globalChecksum\":\"";
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var start = i + key.Length;
        var end = json.IndexOf('"', start);
        return end < 0 ? string.Empty : json.Substring(start, end - start);
    }
}
