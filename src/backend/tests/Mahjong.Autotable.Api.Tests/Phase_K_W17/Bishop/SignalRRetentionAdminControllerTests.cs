using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Controller-level contract tests for
/// <see cref="SignalRRetentionAdminController"/>. Mirrors the
/// replay-retention admin tests exactly so the two surfaces
/// stay aligned.
/// </summary>
[Collection("DbSerial")]
public sealed class SignalRRetentionAdminControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w17-signalr-admin-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SignalRRetentionAdminControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w17-signalr-admin-{Guid.NewGuid():N}.sqlite");
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

    private SignalRRetentionAdminController MakeController(
        ISignalRRetentionPolicyStore? store,
        HttpContext httpContext,
        AuthCookieService cookies)
    {
        var controller = new SignalRRetentionAdminController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SignalRRetentionAdminController>.Instance,
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

    private static void StampAdminReason(HttpContext ctx, string reason)
    {
        ctx.Request.Headers[SignalRRetentionAdminController.AdminReasonHeader] = reason;
    }

    private static SignalRRetentionAdminBody MakeBody(string tenantId, int minutes = 30) =>
        new() { TenantId = tenantId, RetentionMinutes = minutes };

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task List_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var ctx = new DefaultHttpContext();
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task List_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task List_StoreUnregistered_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(store: null, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task List_AdminEmptyStore_Returns200()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.List(CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Get_MissingTenant_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.Get("missing-tenant", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Create_NoReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.Create(MakeBody("t1"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Create_EmptyReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampAdminReason(ctx, "  ");
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.Create(MakeBody("t1"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Create_BadMinutes_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampAdminReason(ctx, "test");
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.Create(MakeBody("t1", minutes: 0), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Create_Valid_Returns201_AndAudits()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampAdminReason(ctx, "extend reconnect window");
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.Create(MakeBody("t1", minutes: 240), CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(result);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.ReconnectAuditEntries
            .Where(e => e.Kind == ReconnectAuditEntry.KindSignalRRetentionCreated)
            .ToListAsync();
        Assert.Single(audit);
        Assert.Contains("t1|extend reconnect window", audit[0].Detail);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Create_ExistingTenant_Returns200_AndUpdatesAudit()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampAdminReason(ctx, "first");
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(new SignalRRetentionPolicy { TenantId = "t1", RetentionMinutes = 60 }, default);
        var c = MakeController(store, ctx, cookies);
        var result = await c.Create(MakeBody("t1", minutes: 120), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.ReconnectAuditEntries
            .Where(e => e.Kind == ReconnectAuditEntry.KindSignalRRetentionUpdated)
            .ToListAsync();
        Assert.Single(audit);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Put_MismatchedTenant_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampAdminReason(ctx, "test");
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var body = MakeBody("body-tenant");
        var result = await c.Update("route-tenant", body, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Delete_MissingTenant_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampAdminReason(ctx, "purge");
        var store = new InMemorySignalRRetentionPolicyStore();
        var c = MakeController(store, ctx, cookies);
        var result = await c.Delete("nope", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Delete_Existing_Returns204_AndAudits()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampAdminReason(ctx, "purge");
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(new SignalRRetentionPolicy { TenantId = "t1", RetentionMinutes = 60 }, default);
        var c = MakeController(store, ctx, cookies);
        var result = await c.Delete("t1", CancellationToken.None);
        Assert.IsType<NoContentResult>(result);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.ReconnectAuditEntries
            .Where(e => e.Kind == ReconnectAuditEntry.KindSignalRRetentionDeleted)
            .ToListAsync();
        Assert.Single(audit);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task Delete_NoReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemorySignalRRetentionPolicyStore();
        await store.UpsertAsync(new SignalRRetentionPolicy { TenantId = "t1", RetentionMinutes = 60 }, default);
        var c = MakeController(store, ctx, cookies);
        var result = await c.Delete("t1", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
