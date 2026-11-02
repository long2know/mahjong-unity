using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Tests for the W20
/// <see cref="PerTenantRotationBulkDeleteController"/>. Mirrors
/// the W19 bulk-update test posture: auth gates (401/403),
/// disabled-toggle 503, body validation (400), batch-size cap
/// (413), reason header (mandatory + length cap), audit-row
/// write side-effects.
/// </summary>
[Collection("DbSerial")]
public sealed class PerTenantRotationBulkDeleteControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w20-bulk-delete-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public PerTenantRotationBulkDeleteControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w20-bulk-del-{Guid.NewGuid():N}.sqlite");
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

    private PerTenantRotationBulkDeleteController MakeController(
        bool toggleEnabled,
        IPerTenantJwksRotationStore? store,
        HttpContext httpContext,
        AuthCookieService cookies,
        string? adminReason = "fleet-rotation-cleanup")
    {
        var options = new PerTenantJwksRotationOptions { Enabled = toggleEnabled };
        var controller = new PerTenantRotationBulkDeleteController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PerTenantRotationBulkDeleteController>.Instance,
            options,
            store);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        if (adminReason is not null)
        {
            httpContext.Request.Headers[PerTenantRotationBulkDeleteController.AdminReasonHeader] = adminReason;
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

    private async Task<long> CountDeleteAuditAsync()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries.CountAsync(
            e => e.Kind == ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted);
    }

    private static PerTenantRotationBulkDeleteBody Body(params string[] ids) =>
        new() { TenantIds = ids.ToList() };

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkDelete(Body("a"), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkDelete(Body("a"), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_DisabledToggle_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(false, null, ctx, cookies);
        var r = await c.BulkDelete(Body("a"), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_NullStore_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, null, ctx, cookies);
        var r = await c.BulkDelete(Body("a"), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkDelete(null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_EmptyTenantIds_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkDelete(new PerTenantRotationBulkDeleteBody { TenantIds = new() }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_BatchAboveMaximum_Returns413()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var oversize = Body(Enumerable.Range(0, PerTenantRotationBulkDeleteController.MaxBatchSize + 1)
            .Select(i => $"tenant-{i}").ToArray());
        var r = await c.BulkDelete(oversize, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_MissingReasonHeader_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: null);
        var r = await c.BulkDelete(Body("a"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_EmptyReasonHeader_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: "   ");
        var r = await c.BulkDelete(Body("a"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_ReasonHeaderTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies,
            adminReason: new string('x', PerTenantRotationBulkDeleteController.MaxAdminReasonLength + 1));
        var r = await c.BulkDelete(Body("a"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_EmptyTenantId_RejectsEntireBatch()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkDelete(Body("a", "", "c"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_TenantIdTooLong_RejectsEntireBatch()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkDelete(
            Body("a", new string('y', PerTenantRotationBulkDeleteController.MaxTenantIdLength + 1)),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_MissingRowsTreatedAsSoftMiss()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.BulkDelete(Body("not-there"), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        Assert.NotNull(ok.Value);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_RealDeletion_WritesAuditRow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "tenant-a",
            ActiveKid = "active",
            PreviousKid = "prev",
            RotationStartUtc = DateTimeOffset.UtcNow,
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(30),
        });
        var before = await CountDeleteAuditAsync();
        var c = MakeController(true, store, ctx, cookies);
        var r = await c.BulkDelete(Body("tenant-a"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        Assert.Equal(before + 1, await CountDeleteAuditAsync());
        Assert.Equal(0, await store.CountAsync());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkDelete_AuditDetailIncludesReasonAndTenant()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "tenant-z",
            ActiveKid = "active",
            PreviousKid = "prev",
            RotationStartUtc = DateTimeOffset.UtcNow,
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(30),
        });
        var c = MakeController(true, store, ctx, cookies, adminReason: "fleet-z-decommission");
        await c.BulkDelete(Body("tenant-z"), CancellationToken.None);
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries
            .FirstAsync(e => e.Kind == ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted);
        Assert.Contains("tenant=tenant-z", row.Detail);
        Assert.Contains("reason=fleet-z-decommission", row.Detail);
        Assert.Contains("batchId=", row.Detail);
    }
}
