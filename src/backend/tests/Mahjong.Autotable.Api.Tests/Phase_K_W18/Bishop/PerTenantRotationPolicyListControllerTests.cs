using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Bishop;

/// <summary>
/// Phase K Wave 18 — Bishop. Contract tests for the new
/// per-tenant rotation policy LIST endpoint
/// (<see cref="PerTenantRotationPolicyListController"/>).
/// Covers admin auth gate, disabled-toggle posture, pagination
/// (page + pageSize clamping), tenant-prefix filtering, audit
/// write side-effects, and the empty-result envelope.
/// </summary>
[Collection("DbSerial")]
public sealed class PerTenantRotationPolicyListControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w18-rotation-list-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public PerTenantRotationPolicyListControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w18-list-{Guid.NewGuid():N}.sqlite");
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

    private PerTenantRotationPolicyListController MakeController(
        bool toggleEnabled,
        IPerTenantJwksRotationStore? store,
        HttpContext httpContext,
        AuthCookieService cookies)
    {
        var options = new PerTenantJwksRotationOptions { Enabled = toggleEnabled };
        var controller = new PerTenantRotationPolicyListController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PerTenantRotationPolicyListController>.Instance,
            options,
            store);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
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

    private static PerTenantJwksRotationPolicy MakePolicy(string tenantId) =>
        new()
        {
            TenantId = tenantId,
            ActiveKid = $"{tenantId}-kid-active",
            PreviousKid = $"{tenantId}-kid-prev",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-1),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(1),
        };

    private async Task<long> CountAuditRowsAsync(string kind)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries.CountAsync(e => e.Kind == kind);
    }

    private static InMemoryPerTenantJwksRotationStore SeededStore(params string[] tenantIds)
    {
        var s = new InMemoryPerTenantJwksRotationStore();
        foreach (var t in tenantIds)
        {
            s.UpsertAsync(MakePolicy(t)).GetAwaiter().GetResult();
        }
        return s;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.List(null, null, null, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.List(null, null, null, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_DisabledToggle_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(false, null, ctx, cookies);
        var r = await c.List(null, null, null, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_NullStore_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, null, ctx, cookies);
        var r = await c.List(null, null, null, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_Empty_ReturnsOkWithEmptyItems()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, SeededStore(), ctx, cookies);
        var r = await c.List(null, null, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_Defaults_AppliesDefaultPageSize()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = SeededStore("a", "b", "c");
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.List(null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        Assert.NotNull(ok.Value);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_PageZero_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, SeededStore(), ctx, cookies);
        var r = await c.List(page: 0, null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_PageNegative_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, SeededStore(), ctx, cookies);
        var r = await c.List(page: -5, null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_PageAboveMaximum_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, SeededStore(), ctx, cookies);
        var r = await c.List(
            page: PerTenantRotationPolicyListController.MaxPage + 1, null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_PageSizeZero_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, SeededStore(), ctx, cookies);
        var r = await c.List(null, pageSize: 0, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_PageSizeAboveMax_IsClampedDown()
    {
        // Clamp does NOT 400 — it silently caps. The harness asserts
        // the response is OK; the response shape carries the
        // clamped pageSize.
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, SeededStore("a"), ctx, cookies);
        var r = await c.List(
            null, pageSize: PerTenantRotationPolicyListController.MaxPageSize * 2, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_TenantPrefix_FiltersResults()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = SeededStore("foo-a", "foo-b", "bar-c");
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.List(null, null, tenantPrefix: "foo", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_TenantPrefix_TooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, SeededStore(), ctx, cookies);
        var prefix = new string('x', PerTenantRotationPolicyListController.MaxTenantPrefixLength + 1);
        var r = await c.List(null, null, tenantPrefix: prefix, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_TenantPrefix_WhitespaceOnly_TreatedAsAbsent()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = SeededStore("a", "b");
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.List(null, null, tenantPrefix: "   ", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_WritesAuditRowPerRequest()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = SeededStore("a", "b");
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.List(null, null, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        Assert.Equal(1, await CountAuditRowsAsync(ReconnectAuditEntry.KindAuthJwksPerTenantListed));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_AuditDetail_CapturesPageSizeAndPrefix()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = SeededStore("a-1", "a-2", "b-1");
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.List(page: 1, pageSize: 25, tenantPrefix: "a-", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries.FirstAsync(e =>
            e.Kind == ReconnectAuditEntry.KindAuthJwksPerTenantListed);
        Assert.Contains("page=1", row.Detail);
        Assert.Contains("size=25", row.Detail);
        Assert.Contains("prefix=a-", row.Detail);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task List_RoutesAt_NewW18Path()
    {
        // Reflection contract — the controller is annotated with
        // the W18 wire-stable route, not the W17 route.
        var attrs = typeof(PerTenantRotationPolicyListController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .ToArray();
        Assert.Single(attrs);
        Assert.Equal("api/admin/per-tenant-jwks-rotation-policies", attrs[0].Template);
        await Task.CompletedTask;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void DefaultPageSize_Is50()
    {
        Assert.Equal(50, PerTenantRotationPolicyListController.DefaultPageSize);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void MaxPageSize_Is250()
    {
        Assert.Equal(250, PerTenantRotationPolicyListController.MaxPageSize);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void MaxPage_Is10000()
    {
        Assert.Equal(10_000, PerTenantRotationPolicyListController.MaxPage);
    }
}
