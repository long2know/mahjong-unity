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
/// <see cref="PerTenantRotationBulkEnableController"/>. Mirrors
/// the W19 bulk-update + W20 bulk-delete test posture: auth
/// gates, disabled-toggle, body validation, batch-size cap,
/// reason header, renewal-window validation, audit write,
/// rotation window renewal side-effect.
/// </summary>
[Collection("DbSerial")]
public sealed class PerTenantRotationBulkEnableControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w20-bulk-enable-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public PerTenantRotationBulkEnableControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w20-bulk-en-{Guid.NewGuid():N}.sqlite");
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

    private PerTenantRotationBulkEnableController MakeController(
        bool toggleEnabled,
        IPerTenantJwksRotationStore? store,
        HttpContext httpContext,
        AuthCookieService cookies,
        string? adminReason = "fleet-rotation-enable")
    {
        var options = new PerTenantJwksRotationOptions { Enabled = toggleEnabled };
        var controller = new PerTenantRotationBulkEnableController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PerTenantRotationBulkEnableController>.Instance,
            options,
            store);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        if (adminReason is not null)
        {
            httpContext.Request.Headers[PerTenantRotationBulkEnableController.AdminReasonHeader] = adminReason;
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

    private async Task<long> CountEnableAuditAsync()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries.CountAsync(
            e => e.Kind == ReconnectAuditEntry.KindAuthJwksPerTenantBulkEnabled);
    }

    private static PerTenantRotationBulkEnableBody Body(params (string id, int? days)[] items) =>
        new()
        {
            Items = items.Select(p => new PerTenantRotationBulkEnableItem
            {
                TenantId = p.id,
                RenewalWindowDays = p.days,
            }).ToList(),
        };

    private static async Task SeedAsync(IPerTenantJwksRotationStore store, string id)
    {
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = id,
            ActiveKid = $"{id}-kid",
            PreviousKid = $"{id}-prev",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-100),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(-90),
        });
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(Body(("a", null)), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(Body(("a", null)), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_DisabledToggle_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(false, null, ctx, cookies);
        var r = await c.BulkEnable(Body(("a", null)), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_NullStore_Returns503()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, null, ctx, cookies);
        var r = await c.BulkEnable(Body(("a", null)), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_EmptyItems_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(new PerTenantRotationBulkEnableBody { Items = new() }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_BatchAboveMaximum_Returns413()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var oversize = Body(Enumerable.Range(0, PerTenantRotationBulkEnableController.MaxBatchSize + 1)
            .Select(i => ($"tenant-{i}", (int?)null)).ToArray());
        var r = await c.BulkEnable(oversize, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: null);
        var r = await c.BulkEnable(Body(("a", null)), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies,
            adminReason: new string('x', PerTenantRotationBulkEnableController.MaxAdminReasonLength + 1));
        var r = await c.BulkEnable(Body(("a", null)), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_EmptyTenantId_RejectsBatch()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(Body(("a", null), ("", null)), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_NegativeRenewalWindow_RejectsBatch()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(Body(("a", -7)), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_ExcessiveRenewalWindow_RejectsBatch()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(Body(("a", PerTenantRotationBulkEnableController.MaxRenewalWindowDays + 1)), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_MissingRows_ReportedInNotFoundList()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.BulkEnable(Body(("absent", null)), CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        // No audit row written for the missing tenant.
        Assert.Equal(0, await CountEnableAuditAsync());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_PresentRow_RenewsRotationWindow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await SeedAsync(store, "tenant-a");
        var c = MakeController(true, store, ctx, cookies);
        var t0 = DateTimeOffset.UtcNow;
        var r = await c.BulkEnable(Body(("tenant-a", 30)), CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        var policy = await store.GetAsync("tenant-a");
        Assert.NotNull(policy);
        // The renewed window must start at-or-after t0 and finish ~30d later.
        Assert.True(policy!.RotationStartUtc >= t0.AddMinutes(-1));
        Assert.True((policy.RotationCompleteUtc - policy.RotationStartUtc).TotalDays > 29);
        Assert.True((policy.RotationCompleteUtc - policy.RotationStartUtc).TotalDays < 31);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_DefaultsTo30DayWindow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await SeedAsync(store, "tenant-a");
        var c = MakeController(true, store, ctx, cookies);
        await c.BulkEnable(Body(("tenant-a", null)), CancellationToken.None);
        var policy = await store.GetAsync("tenant-a");
        var span = (policy!.RotationCompleteUtc - policy.RotationStartUtc).TotalDays;
        Assert.InRange(span, 29.9, 30.1);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task BulkEnable_WritesAuditRowPerEnabledTenant()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await SeedAsync(store, "tenant-a");
        await SeedAsync(store, "tenant-b");
        var c = MakeController(true, store, ctx, cookies);
        await c.BulkEnable(Body(("tenant-a", null), ("tenant-b", null)), CancellationToken.None);
        Assert.Equal(2, await CountEnableAuditAsync());
    }
}
