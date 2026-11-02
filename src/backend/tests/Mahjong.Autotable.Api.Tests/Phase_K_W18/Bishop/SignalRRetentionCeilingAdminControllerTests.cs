using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Bishop;

/// <summary>
/// Phase K Wave 18 — Bishop. Controller-level contract tests for
/// <see cref="SignalRRetentionCeilingAdminController"/>.
/// SQLite-backed AppDbContext exercises the audit-write side
/// effect end-to-end.
/// </summary>
[Collection("DbSerial")]
public sealed class SignalRRetentionCeilingAdminControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w18-ceiling-admin-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SignalRRetentionCeilingAdminControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w18-ceiling-admin-{Guid.NewGuid():N}.sqlite");
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

    private SignalRRetentionCeilingAdminController MakeController(
        SignalRRetentionCeilingOptions options,
        HttpContext httpContext,
        AuthCookieService cookies)
    {
        var evaluator = new SignalRRetentionPolicyEvaluator(
            options, new SignalRRetentionPolicyCappedMetrics());
        var controller = new SignalRRetentionCeilingAdminController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SignalRRetentionCeilingAdminController>.Instance,
            options,
            evaluator);
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

    private static void StampReason(HttpContext ctx, string reason)
    {
        ctx.Request.Headers[SignalRRetentionCeilingAdminController.AdminReasonHeader] = reason;
    }

    private async Task<long> CountAuditRowsAsync(string kind)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries.CountAsync(e => e.Kind == kind);
    }

    // ─── Get ───────────────────────────────────────────────────

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Get_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Get(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Get_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Get(CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Get_Admin_ReturnsCeilingAndOverrides()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var opts = new SignalRRetentionCeilingOptions
        {
            GlobalCeilingMinutes = 30 * 24 * 60,
            AllowAboveCeilingTenants = new List<string> { "tenant-x" },
        };
        var c = MakeController(opts, ctx, cookies);
        var r = await c.Get(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        Assert.NotNull(ok.Value);
    }

    // ─── Grant ─────────────────────────────────────────────────

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Grant_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Grant(new SignalRRetentionCeilingOverrideBody { TenantId = "t" }, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Grant_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Grant(new SignalRRetentionCeilingOverrideBody { TenantId = "t" }, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Grant_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampReason(ctx, "test");
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Grant(null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Grant_MissingReasonHeader_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Grant(new SignalRRetentionCeilingOverrideBody { TenantId = "t" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Grant_EmptyTenantId_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampReason(ctx, "test");
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Grant(new SignalRRetentionCeilingOverrideBody { TenantId = "  " }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Grant_FirstGrant_Returns201_AndAddsTenant()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampReason(ctx, "enterprise-tier-upgrade");
        var opts = new SignalRRetentionCeilingOptions();
        var c = MakeController(opts, ctx, cookies);
        var r = await c.Grant(
            new SignalRRetentionCeilingOverrideBody { TenantId = "tenant-z" },
            CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(r);
        Assert.Contains("tenant-z", opts.AllowAboveCeilingTenants);
        Assert.Equal(1, await CountAuditRowsAsync(ReconnectAuditEntry.KindSignalRRetentionCeilingOverride));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Grant_RepeatGrant_Returns200_NoDupe()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampReason(ctx, "test");
        var opts = new SignalRRetentionCeilingOptions
        {
            AllowAboveCeilingTenants = new List<string> { "tenant-z" },
        };
        var c = MakeController(opts, ctx, cookies);
        var r = await c.Grant(
            new SignalRRetentionCeilingOverrideBody { TenantId = "tenant-z" },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
        // No duplicate added.
        Assert.Single(opts.AllowAboveCeilingTenants);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Revoke_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Revoke("t", CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Revoke_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "spectator");
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Revoke("t", CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Revoke_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var opts = new SignalRRetentionCeilingOptions
        {
            AllowAboveCeilingTenants = new List<string> { "t" },
        };
        var c = MakeController(opts, ctx, cookies);
        var r = await c.Revoke("t", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Revoke_UnknownTenant_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampReason(ctx, "cleanup");
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Revoke("tenant-not-here", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Revoke_Existing_Returns204_AndRemovesAndAudits()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampReason(ctx, "expired-contract");
        var opts = new SignalRRetentionCeilingOptions
        {
            AllowAboveCeilingTenants = new List<string> { "tenant-z" },
        };
        var c = MakeController(opts, ctx, cookies);
        var r = await c.Revoke("tenant-z", CancellationToken.None);
        Assert.IsType<NoContentResult>(r);
        Assert.Empty(opts.AllowAboveCeilingTenants);
        Assert.Equal(1, await CountAuditRowsAsync(ReconnectAuditEntry.KindSignalRRetentionCeilingOverride));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Revoke_EmptyTenantRoute_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        StampReason(ctx, "test");
        var c = MakeController(new SignalRRetentionCeilingOptions(), ctx, cookies);
        var r = await c.Revoke("   ", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }
}
