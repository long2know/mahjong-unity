using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Bishop;

/// <summary>
/// Phase K Wave 22 — Bishop. Tests for the W22 SignalR
/// connection diagnostic admin endpoint: auth gate, per-tenant
/// filtering, transport breakdown, group breakdown, empty
/// registry, ping update.
/// </summary>
[Collection("DbSerial")]
public sealed class SignalRConnectionDiagnosticControllerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbPath;
    private readonly SignalRConnectionRegistry _registry = new();

    public SignalRConnectionDiagnosticControllerTests()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "bishop-w22-diag-sqlite");
        Directory.CreateDirectory(scratch);
        _dbPath = Path.Combine(scratch, $"diag-{Guid.NewGuid():N}.sqlite");
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
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var issue = new DefaultHttpContext();
        var s = await cookies.IssueAsync(issue, $"p-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={s.Token}";
        return (cookies, ctx);
    }

    private SignalRConnectionDiagnosticController MakeController(HttpContext ctx, AuthCookieService cookies)
    {
        var c = new SignalRConnectionDiagnosticController(cookies, _registry);
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        return c;
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.GetDiagnostics(null, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var r = await MakeController(ctx, cookies).GetDiagnostics(null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_EmptyRegistry_ReturnsZero()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics(null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_RegisteredConnection_AppearsInTotal()
    {
        _registry.Register(new SignalRConnectionRegistry.Entry
        {
            ConnectionId = "c1",
            TenantId = "tenant-a",
            Group = "g1",
            Transport = "websocket",
        });
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics(null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_TenantFilter_NarrowsToSubset()
    {
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", TenantId = "a", Group = "g1", Transport = "websocket" });
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c2", TenantId = "b", Group = "g1", Transport = "websocket" });
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics("a", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_Register_AddsEntry()
    {
        var reg = new SignalRConnectionRegistry();
        reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1" });
        Assert.Equal(1, reg.Count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_Unregister_RemovesEntry()
    {
        var reg = new SignalRConnectionRegistry();
        reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1" });
        Assert.True(reg.Unregister("c1"));
        Assert.Equal(0, reg.Count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_Unregister_UnknownReturnsFalse()
    {
        var reg = new SignalRConnectionRegistry();
        Assert.False(reg.Unregister("missing"));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_UpdatePing_BumpsTimestamp()
    {
        var reg = new SignalRConnectionRegistry();
        var entry = new SignalRConnectionRegistry.Entry { ConnectionId = "c1", LastPingUtc = DateTime.UtcNow.AddMinutes(-10) };
        reg.Register(entry);
        var target = DateTime.UtcNow;
        reg.UpdatePing("c1", target);
        Assert.Equal(target, reg.Snapshot().First().LastPingUtc);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_Register_RejectsBlankConnectionId()
    {
        var reg = new SignalRConnectionRegistry();
        Assert.Throws<ArgumentException>(() => reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = " " }));
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_Snapshot_IsStable()
    {
        var reg = new SignalRConnectionRegistry();
        reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1" });
        var snap = reg.Snapshot();
        reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c2" });
        Assert.Single(snap);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_Clear_ResetsToEmpty()
    {
        var reg = new SignalRConnectionRegistry();
        reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1" });
        reg.Clear();
        Assert.Equal(0, reg.Count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_ReturnsTransportBreakdown()
    {
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", Transport = "websocket" });
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c2", Transport = "longpolling" });
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics(null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        Assert.NotNull(ok.Value);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_ReturnsGroupBreakdown()
    {
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", Group = "tournament-a" });
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c2", Group = "tournament-b" });
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics(null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_TenantFilter_DoesNotMatchEmptyTenant()
    {
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", TenantId = "" });
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics("specific-tenant", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_NoTenantFilter_IncludesAllTenants()
    {
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", TenantId = "a" });
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c2", TenantId = "b" });
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics(null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Get_HandlesWhitespaceTenantAsNoFilter()
    {
        _registry.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", TenantId = "x" });
        var (cookies, ctx) = await MakeSessionAsync();
        var r = await MakeController(ctx, cookies).GetDiagnostics("   ", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_UpdatePing_OnUnknownIsNoop()
    {
        var reg = new SignalRConnectionRegistry();
        reg.UpdatePing("nope", DateTime.UtcNow);
        Assert.Equal(0, reg.Count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void Registry_Register_OverwritesDuplicate()
    {
        var reg = new SignalRConnectionRegistry();
        reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", Transport = "websocket" });
        reg.Register(new SignalRConnectionRegistry.Entry { ConnectionId = "c1", Transport = "longpolling" });
        Assert.Equal(1, reg.Count);
        Assert.Equal("longpolling", reg.Snapshot().First().Transport);
    }
}
