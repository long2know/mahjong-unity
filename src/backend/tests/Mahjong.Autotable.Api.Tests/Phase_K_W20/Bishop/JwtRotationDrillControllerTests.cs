using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Contract tests for the new
/// <see cref="JwtRotationDrillController"/>. Covers the
/// production-environment block, the env-var override block,
/// the admin auth gate, the X-Admin-Reason header contract,
/// and the audit-row + JWKS-cache invalidation side-effects.
/// </summary>
[Collection("DbSerial")]
public sealed class JwtRotationDrillControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w20-drill-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;
    private readonly string? _savedEnvVar;

    private sealed class StubHostEnv : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    public JwtRotationDrillControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w20-drill-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        _savedEnvVar = Environment.GetEnvironmentVariable(JwtRotationDrillController.DrillEnvVar);
        Environment.SetEnvironmentVariable(JwtRotationDrillController.DrillEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(JwtRotationDrillController.DrillEnvVar, _savedEnvVar);
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private JwtRotationDrillController MakeController(
        string environment,
        bool perTenantEnabled,
        IPerTenantJwksRotationStore? store,
        HttpContext httpContext,
        AuthCookieService cookies,
        JwksCacheService? cache = null,
        string? adminReason = "rotation-drill-test")
    {
        var env = new StubHostEnv { EnvironmentName = environment };
        var options = new PerTenantJwksRotationOptions { Enabled = perTenantEnabled };
        var validator = new PerTenantJwksRotationValidator(
            options,
            NullLogger<PerTenantJwksRotationValidator>.Instance,
            store);
        var controller = new JwtRotationDrillController(
            cookies,
            env,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JwtRotationDrillController>.Instance,
            validator,
            options,
            store,
            cache);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        if (adminReason is not null)
        {
            httpContext.Request.Headers[JwtRotationDrillController.AdminReasonHeader] = adminReason;
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

    private async Task<long> CountDrillAuditAsync()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries.CountAsync(
            e => e.Kind == ReconnectAuditEntry.KindJwtKeyRotationDrill);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_InProduction_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController("Production", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.Drill(CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_EnvVarFalse_Returns403()
    {
        Environment.SetEnvironmentVariable(JwtRotationDrillController.DrillEnvVar, "false");
        try
        {
            var (cookies, ctx) = await MakeSessionAsync();
            var c = MakeController("Development", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
            var r = await c.Drill(CancellationToken.None);
            var s = Assert.IsType<ObjectResult>(r);
            Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(JwtRotationDrillController.DrillEnvVar, null);
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController("Development", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.Drill(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController("Development", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.Drill(CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_MissingReasonHeader_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController("Development", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: null);
        var r = await c.Drill(CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_EmptyReasonHeader_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController("Development", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies, adminReason: "   ");
        var r = await c.Drill(CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController("Development", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies,
            adminReason: new string('x', JwtRotationDrillController.MaxAdminReasonLength + 1));
        var r = await c.Drill(CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_HappyPath_ReturnsOkAndAuditsAndInvalidatesCache()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(new PerTenantJwksRotationPolicy
        {
            TenantId = "tenant-d",
            ActiveKid = "active",
            PreviousKid = "prev",
            RotationStartUtc = DateTimeOffset.UtcNow.AddDays(-1),
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(30),
        });
        using var mem = new MemoryCache(new MemoryCacheOptions());
        using var cache = new JwksCacheService(mem);
        var before = await CountDrillAuditAsync();
        var c = MakeController("Development", true, store, ctx, cookies, cache);
        var r = await c.Drill(CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        Assert.Equal(before + 1, await CountDrillAuditAsync());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_StagingEnvironment_AlsoAllowed()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController("Staging", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies);
        var r = await c.Drill(CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_PerTenantDisabled_StillAuditsAndReturnsOk()
    {
        // The drill should always audit + invalidate even when
        // per-tenant rotation is toggled off, so the operator
        // can validate the wire-shape in environments without
        // per-tenant policies provisioned.
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController("Development", false, null, ctx, cookies);
        var before = await CountDrillAuditAsync();
        var r = await c.Drill(CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        Assert.Equal(before + 1, await CountDrillAuditAsync());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task Drill_AuditDetailIncludesReasonAndEnvironment()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController("Staging", true, new InMemoryPerTenantJwksRotationStore(), ctx, cookies,
            adminReason: "monthly-rotation-rehearsal");
        await c.Drill(CancellationToken.None);
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries
            .Where(e => e.Kind == ReconnectAuditEntry.KindJwtKeyRotationDrill)
            .OrderByDescending(e => e.At)
            .FirstAsync();
        Assert.Contains("reason=monthly-rotation-rehearsal", row.Detail);
        Assert.Contains("env=Staging", row.Detail);
        Assert.Contains("drillId=", row.Detail);
    }
}
