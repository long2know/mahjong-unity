using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Controller-level contract tests
/// for <see cref="PerTenantRotationAdminController"/>. Uses a
/// SQLite-backed AppDbContext so the audit-write side effect is
/// exercised end-to-end.
/// </summary>
[Collection("DbSerial")]
public sealed class PerTenantRotationAdminControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w16-admin-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public PerTenantRotationAdminControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w16-admin-{Guid.NewGuid():N}.sqlite");
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

    private PerTenantRotationAdminController MakeController(
        bool toggleEnabled,
        IPerTenantJwksRotationStore? store,
        HttpContext httpContext,
        AuthCookieService cookies)
    {
        var options = new PerTenantJwksRotationOptions { Enabled = toggleEnabled };
        var controller = new PerTenantRotationAdminController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PerTenantRotationAdminController>.Instance,
            options,
            store);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private async Task<(AuthCookieService cookies, HttpContext context)> MakeAdminAsync(string role = "admin")
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

    private static PerTenantRotationAdminBody MakeBody(string tenantId) => new()
    {
        TenantId = tenantId,
        ActiveKid = $"{tenantId}-kid-A",
        PreviousKid = $"{tenantId}-kid-B",
        RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-1),
        RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(7),
        OverlapWindowDays = 7,
    };

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task NoSession_Returns401()
    {
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var ctx = new DefaultHttpContext();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task NonAdminSession_Returns403()
    {
        var (cookies, ctx) = await MakeAdminAsync(role: "player");
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ToggleOff_Returns503()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(false, store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ToggleOn_NoStore_Returns503()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var c = MakeController(true, null, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_List_EmptyStore_Returns200()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Get_NotFound_Returns404()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Get("missing", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Get_EmptyTenant_Returns400()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Get("", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Create(null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_MissingTenantId_Returns400()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var body = MakeBody("");
        var result = await c.Create(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_MissingActiveKid_Returns400()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var body = MakeBody("acme"); body.ActiveKid = "";
        var result = await c.Create(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_CompleteBeforeStart_Returns400()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var body = MakeBody("acme");
        body.RotationCompleteUtc = body.RotationStartUtc.AddSeconds(-1);
        var result = await c.Create(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_NegativeOverlap_Returns400()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var body = MakeBody("acme"); body.OverlapWindowDays = -3;
        var result = await c.Create(body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_NewTenant_Returns201()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Create(MakeBody("acme"), CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_ExistingTenant_Returns200()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "acme",
            ActiveKid = "kid-A",
            PreviousKid = "kid-B",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-1),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(7),
        });
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Create(MakeBody("acme"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Update_RouteAndBodyMismatch_Returns400()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var body = MakeBody("acme"); body.TenantId = "other";
        var result = await c.Update("acme", body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Update_RouteOnly_Returns200()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var body = MakeBody("acme"); body.TenantId = null;
        var result = await c.Update("acme", body, CancellationToken.None);
        // First write is a create → 201; if the policy preexisted
        // it's a 200. The Update endpoint normalises the tenant id
        // from the route before upserting either way.
        Assert.True(result is CreatedAtActionResult or OkObjectResult);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Delete_NotFound_Returns404()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Delete("missing", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Delete_Existing_Returns204()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "acme",
            ActiveKid = "kid-A",
            PreviousKid = "kid-B",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-30),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(-1),
            OverlapWindowDays = 7,
        });
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Delete("acme", CancellationToken.None);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Create_EmitsAuditRow()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        await c.Create(MakeBody("acme"), CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries
            .FirstOrDefaultAsync(r => r.Kind == PerTenantRotationAdminController.KindCreated && r.Detail == "acme");
        Assert.NotNull(row);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Delete_EmitsAuditRow()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "acme",
            ActiveKid = "kid-A",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-30),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });
        var c = MakeController(true, store, ctx, cookies);
        await c.Delete("acme", CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries
            .FirstOrDefaultAsync(r => r.Kind == PerTenantRotationAdminController.KindDeleted && r.Detail == "acme");
        Assert.NotNull(row);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void AuditKindConstants_AreWireStable()
    {
        Assert.Equal("auth.jwks.per-tenant.created", PerTenantRotationAdminController.KindCreated);
        Assert.Equal("auth.jwks.per-tenant.updated", PerTenantRotationAdminController.KindUpdated);
        Assert.Equal("auth.jwks.per-tenant.deleted", PerTenantRotationAdminController.KindDeleted);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_Get_PresentRow_Returns200()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "acme",
            ActiveKid = "kid-A",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-1),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(7),
        });
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.Get("acme", CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task Admin_List_WithRow_ReturnsItems()
    {
        var (cookies, ctx) = await MakeAdminAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "alpha",
            ActiveKid = "kid",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-1),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(7),
        });
        var c = MakeController(true, store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }
}
