using System.Text;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Bishop;

/// <summary>
/// Phase K Wave 21 — Bishop. Tests for the W21 SignalR
/// retention manual-purge surface: counter shape +
/// <see cref="SignalRRetentionManualPurgeController"/> admin
/// gate / cutoff-validation / per-tenant scoping.
/// </summary>
public sealed class SignalRManualPurgeMetricsTests
{
    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MetricName_IsStable()
    {
        Assert.Equal("signalr_manual_purge_total", SignalRManualPurgeMetrics.MetricName);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Add_Accumulates()
    {
        var m = new SignalRManualPurgeMetrics();
        m.Add("tenant-a", 3);
        m.Add("tenant-a", 7);
        Assert.Equal(10, m.Get("tenant-a"));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Add_ZeroDelta_Ignored()
    {
        var m = new SignalRManualPurgeMetrics();
        m.Add("tenant-a", 0);
        Assert.Equal(0, m.Get("tenant-a"));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Add_EmptyTenant_FoldsIntoUnknownBucket()
    {
        var m = new SignalRManualPurgeMetrics();
        m.Add("", 5);
        Assert.Equal(5, m.Get(SignalRManualPurgeMetrics.UnknownTenantBucket));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndType()
    {
        var m = new SignalRManualPurgeMetrics();
        m.Add("tenant-a", 4);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var s = sb.ToString();
        Assert.Contains("# HELP signalr_manual_purge_total", s);
        Assert.Contains("# TYPE signalr_manual_purge_total counter", s);
        Assert.Contains("tenant=\"tenant-a\"", s);
    }
}

[Collection("DbSerial")]
public sealed class SignalRRetentionManualPurgeControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w21-signalr-purge-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SignalRRetentionManualPurgeControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w21-signalr-purge-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<SignalRManualPurgeMetrics>();
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
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var issueContext = new DefaultHttpContext();
        var session = await cookies.IssueAsync(issueContext, $"player-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var resolveContext = new DefaultHttpContext();
        resolveContext.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={session.Token}";
        return (cookies, resolveContext);
    }

    private SignalRRetentionManualPurgeController MakeController(HttpContext ctx, AuthCookieService cookies, string? reason = "purge-test")
    {
        var c = new SignalRRetentionManualPurgeController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            _sp.GetService<SignalRManualPurgeMetrics>());
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        if (reason is not null) ctx.Request.Headers[SignalRRetentionManualPurgeController.AdminReasonHeader] = reason;
        return c;
    }

    private async Task SeedSeqAsync(string tenant, DateTime createdAt, int count = 1)
    {
        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        for (int i = 0; i < count; i++)
        {
            db.SignalRSequenceEntries.Add(new SignalRSequenceEntry
            {
                Id = Guid.NewGuid(),
                HubName = "TestHub",
                ConnectionId = $"conn-{Guid.NewGuid():N}",
                GroupName = "g",
                Method = "Push",
                Sequence = i + 1,
                CreatedAt = createdAt,
                ExpiresAt = createdAt.AddHours(1),
                PayloadJson = "{}",
                TenantId = tenant,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.Purge(tenant: "t", before: DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.Purge("t", DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: null);
        var r = await c.Purge("t", DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies,
            reason: new string('x', SignalRRetentionManualPurgeController.MaxAdminReasonLength + 1));
        var r = await c.Purge("t", DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_MissingBefore_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Purge("t", before: "", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_MalformedBefore_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Purge("t", before: "not-a-date", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_BeforeInFuture_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Purge("t", before: DateTime.UtcNow.AddDays(1).ToString("O"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_HappyPath_DeletesOldRows()
    {
        await SeedSeqAsync("tenant-old", DateTime.UtcNow.AddHours(-3), count: 5);
        await SeedSeqAsync("tenant-old", DateTime.UtcNow.AddMinutes(-5), count: 2);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Purge("tenant-old",
            DateTime.UtcNow.AddHours(-1).ToString("O"),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.SignalRSequenceEntries.Where(e => e.TenantId == "tenant-old").CountAsync();
        Assert.Equal(2, remaining);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_TenantScoped_LeavesOtherTenantsAlone()
    {
        await SeedSeqAsync("tenant-x", DateTime.UtcNow.AddHours(-3), count: 3);
        await SeedSeqAsync("tenant-y", DateTime.UtcNow.AddHours(-3), count: 4);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Purge("tenant-x", DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var x = await db.SignalRSequenceEntries.CountAsync(e => e.TenantId == "tenant-x");
        var y = await db.SignalRSequenceEntries.CountAsync(e => e.TenantId == "tenant-y");
        Assert.Equal(0, x);
        Assert.Equal(4, y);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_NoTenantFilter_PurgesAcrossAllTenants()
    {
        await SeedSeqAsync("tenant-1", DateTime.UtcNow.AddHours(-3), count: 2);
        await SeedSeqAsync("tenant-2", DateTime.UtcNow.AddHours(-3), count: 3);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Purge(tenant: null, before: DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.SignalRSequenceEntries.CountAsync();
        Assert.Equal(0, remaining);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_StampsAuditRow()
    {
        await SeedSeqAsync("tenant-audit", DateTime.UtcNow.AddHours(-3), count: 1);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Purge("tenant-audit", DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.ReconnectAuditEntries
            .Where(a => a.Kind == ReconnectAuditEntry.KindSignalRManualPurge)
            .ToListAsync();
        Assert.NotEmpty(audits);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_IncrementsCounter()
    {
        await SeedSeqAsync("tenant-counter", DateTime.UtcNow.AddHours(-3), count: 4);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Purge("tenant-counter", DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);

        var metrics = _sp.GetRequiredService<SignalRManualPurgeMetrics>();
        Assert.Equal(4, metrics.Get("tenant-counter"));
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Purge_NoMatchingRows_ReturnsZero()
    {
        await SeedSeqAsync("tenant-fresh", DateTime.UtcNow.AddMinutes(-5), count: 3);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Purge("tenant-fresh", DateTime.UtcNow.AddHours(-1).ToString("O"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.SignalRSequenceEntries.CountAsync(e => e.TenantId == "tenant-fresh");
        Assert.Equal(3, remaining);
    }
}
