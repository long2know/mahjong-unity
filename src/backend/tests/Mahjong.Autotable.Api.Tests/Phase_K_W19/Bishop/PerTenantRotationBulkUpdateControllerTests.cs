using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Contract tests for the new
/// <see cref="PerTenantRotationBulkUpdateController"/>. Covers:
/// admin auth gate (401/403), disabled-toggle and null-store
/// 503 postures, body / items validation (400), batch-size cap
/// (413), reason header (mandatory + length cap), audit-row
/// write side-effects, and the all-or-nothing transactional
/// guarantee.
/// </summary>
[Collection("DbSerial")]
public sealed class PerTenantRotationBulkUpdateControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w19-bulk-update-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public PerTenantRotationBulkUpdateControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w19-bulk-{Guid.NewGuid():N}.sqlite");
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

    private PerTenantRotationBulkUpdateController MakeController(
        bool toggleEnabled,
        IPerTenantJwksRotationStore? store,
        HttpContext httpContext,
        AuthCookieService cookies,
        string? adminReason = "fleet-rotation-test")
    {
        var options = new PerTenantJwksRotationOptions { Enabled = toggleEnabled };
        var controller = new PerTenantRotationBulkUpdateController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PerTenantRotationBulkUpdateController>.Instance,
            options,
            store);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        if (adminReason is not null)
        {
            httpContext.Request.Headers[PerTenantRotationBulkUpdateController.AdminReasonHeader] = adminReason;
        }
        return controller;
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

    private static PerTenantRotationAdminBody MakeBody(string tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        return new PerTenantRotationAdminBody
        {
            TenantId = tenantId,
            ActiveKid = $"{tenantId}-kid-active",
            PreviousKid = $"{tenantId}-kid-prev",
            RotationStartUtc = now.AddHours(-1),
            RotationCompleteUtc = now.AddHours(1),
            OverlapWindowDays = 1,
        };
    }

    private static PerTenantRotationBulkUpdateBody MakeBatch(params string[] tenantIds) =>
        new() { Items = tenantIds.Select(MakeBody).ToList() };

    private async Task<long> CountAuditRowsAsync(string kind)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries.CountAsync(e => e.Kind == kind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkUpdate(MakeBatch("a"), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkUpdate(MakeBatch("a"), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_DisabledToggle_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(false, null, ctx, cookies);
        var r = await c.BulkUpdate(MakeBatch("a"), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_NullStore_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, null, ctx, cookies);
        var r = await c.BulkUpdate(MakeBatch("a"), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkUpdate(null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_EmptyItems_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkUpdate(new PerTenantRotationBulkUpdateBody { Items = new() }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_BatchAboveMaximum_Returns413()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var oversize = MakeBatch(Enumerable.Range(0, PerTenantRotationBulkUpdateController.MaxBatchSize + 1)
            .Select(i => $"tenant-{i}").ToArray());
        var r = await c.BulkUpdate(oversize, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_BatchAtMaximum_AppliesSuccessfully()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var atMax = MakeBatch(Enumerable.Range(0, PerTenantRotationBulkUpdateController.MaxBatchSize)
            .Select(i => $"tenant-{i}").ToArray());
        var r = await c.BulkUpdate(atMax, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_MissingAdminReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: null);
        var r = await c.BulkUpdate(MakeBatch("a"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_EmptyAdminReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: "   ");
        var r = await c.BulkUpdate(MakeBatch("a"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_AdminReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies,
            adminReason: new string('x', PerTenantRotationBulkUpdateController.MaxAdminReasonLength + 1));
        var r = await c.BulkUpdate(MakeBatch("a"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_InvalidItem_Returns400_AndAppliesNothing()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var body = MakeBatch("good-a", "good-b");
        // Spoil the second item.
        body.Items![1].TenantId = "";
        var r = await c.BulkUpdate(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
        // Nothing applied.
        Assert.Null(await store.GetAsync("good-a"));
        Assert.Null(await store.GetAsync("good-b"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_NullItem_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var body = new PerTenantRotationBulkUpdateBody { Items = new() { MakeBody("a"), null! } };
        var r = await c.BulkUpdate(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_AllValidRows_AppliesAllAndReturnsOk()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.BulkUpdate(MakeBatch("a", "b", "c"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        Assert.NotNull(await store.GetAsync("a"));
        Assert.NotNull(await store.GetAsync("b"));
        Assert.NotNull(await store.GetAsync("c"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_WritesOneAuditRowPerAppliedPolicy()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        await c.BulkUpdate(MakeBatch("a", "b", "c"), CancellationToken.None);
        var auditCount = await CountAuditRowsAsync(ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied);
        Assert.Equal(3, auditCount);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_AuditDetail_CarriesReasonAndBatchId()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: "fleet-rotation-x");
        var r = await c.BulkUpdate(MakeBatch("tenant-z"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.ReconnectAuditEntries
            .Where(e => e.Kind == ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied)
            .OrderByDescending(e => e.At)
            .FirstAsync();
        Assert.Contains("tenant=tenant-z", entry.Detail);
        Assert.Contains("reason=fleet-rotation-x", entry.Detail);
        Assert.Contains("batchId=", entry.Detail);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_OkResponseShape_IncludesBatchIdAndTenants()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkUpdate(MakeBatch("a", "b"), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        Assert.NotNull(ok.Value);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"appliedCount\":2", json);
        Assert.Contains("\"batchId\"", json);
        Assert.Contains("\"tenants\"", json);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_RotationCompleteBeforeStart_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var body = MakeBatch("a");
        body.Items![0].RotationStartUtc = DateTimeOffset.UtcNow.AddDays(1);
        body.Items[0].RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(-1);
        var r = await c.BulkUpdate(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_NegativeOverlap_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var body = MakeBatch("a");
        body.Items![0].OverlapWindowDays = -1;
        var r = await c.BulkUpdate(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task BulkUpdate_MaxBatchConstantIsHundred()
    {
        Assert.Equal(100, PerTenantRotationBulkUpdateController.MaxBatchSize);
        await Task.CompletedTask;
    }
}
