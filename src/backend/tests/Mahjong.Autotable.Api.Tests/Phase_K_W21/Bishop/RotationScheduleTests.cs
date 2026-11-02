using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Bishop;

/// <summary>
/// Phase K Wave 21 — Bishop. Tests for the W21 scheduled
/// rotation surface: <see cref="RotationScheduleEntity"/>
/// persistence, the <see cref="RotationScheduleAdminController"/>
/// admin gate / cron-validation flow, the
/// <see cref="SimpleCronMatcher"/> parser, and the
/// <see cref="RotationScheduledExecutorService"/> RunOnce
/// evaluator with a mocked clock + metrics collector.
/// </summary>
public sealed class SimpleCronMatcherTests
{
    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_EmptyString_ReturnsFalse()
    {
        Assert.False(SimpleCronMatcher.MatchesNow("", new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_WildcardEvery_MatchesAny()
    {
        Assert.True(SimpleCronMatcher.MatchesNow("* * * * *", new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_ExactMinute_MatchesExact()
    {
        Assert.True(SimpleCronMatcher.MatchesNow("30 12 * * *", new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc)));
        Assert.False(SimpleCronMatcher.MatchesNow("31 12 * * *", new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_StepFiveMinutes_MatchesOnIntervals()
    {
        Assert.True(SimpleCronMatcher.MatchesNow("*/5 * * * *", new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc)));
        Assert.True(SimpleCronMatcher.MatchesNow("*/5 * * * *", new DateTime(2026, 5, 24, 12, 5, 0, DateTimeKind.Utc)));
        Assert.False(SimpleCronMatcher.MatchesNow("*/5 * * * *", new DateTime(2026, 5, 24, 12, 7, 0, DateTimeKind.Utc)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_Range_MatchesInsideRange()
    {
        Assert.True(SimpleCronMatcher.MatchesNow("0 10-14 * * *", new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(SimpleCronMatcher.MatchesNow("0 10-14 * * *", new DateTime(2026, 5, 24, 15, 0, 0, DateTimeKind.Utc)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_CommaList_MatchesAnyInList()
    {
        Assert.True(SimpleCronMatcher.MatchesNow("0,15,30,45 * * * *", new DateTime(2026, 5, 24, 12, 15, 0, DateTimeKind.Utc)));
        Assert.False(SimpleCronMatcher.MatchesNow("0,15,30,45 * * * *", new DateTime(2026, 5, 24, 12, 20, 0, DateTimeKind.Utc)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_SixFieldForm_AcceptsLeadingSeconds()
    {
        Assert.True(SimpleCronMatcher.MatchesNow("* 30 12 * * *", new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MatchesNow_MalformedExpression_ReturnsFalse()
    {
        Assert.False(SimpleCronMatcher.MatchesNow("not a cron", new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc)));
        Assert.False(SimpleCronMatcher.MatchesNow("a b c d e", new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc)));
    }
}

[Collection("DbSerial")]
public sealed class RotationScheduleAdminControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w21-rotsched-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public RotationScheduleAdminControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w21-rotsched-{Guid.NewGuid():N}.sqlite");
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
        var cookies = new AuthCookieService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new AuthOptions());
        var issueContext = new DefaultHttpContext();
        var session = await cookies.IssueAsync(issueContext, $"player-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var resolveContext = new DefaultHttpContext();
        resolveContext.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={session.Token}";
        return (cookies, resolveContext);
    }

    private RotationScheduleAdminController MakeController(HttpContext ctx, AuthCookieService cookies, string? reason = "rotation-test")
    {
        var controller = new RotationScheduleAdminController(cookies, _sp.GetRequiredService<IServiceScopeFactory>());
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        if (reason is not null) ctx.Request.Headers[RotationScheduleAdminController.AdminReasonHeader] = reason;
        return controller;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.Schedule("tenant-1", new RotationScheduleAdminController.ScheduleRequest { CronExpression = "0 0 * * *" }, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.Schedule("tenant-1", new RotationScheduleAdminController.ScheduleRequest { CronExpression = "0 0 * * *" }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_MissingReason_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: null);
        var r = await c.Schedule("tenant-1", new RotationScheduleAdminController.ScheduleRequest { CronExpression = "0 0 * * *" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_ReasonTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, reason: new string('x', RotationScheduleAdminController.MaxAdminReasonLength + 1));
        var r = await c.Schedule("tenant-1", new RotationScheduleAdminController.ScheduleRequest { CronExpression = "0 0 * * *" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_NullBody_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Schedule("tenant-1", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_EmptyCron_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Schedule("tenant-1", new RotationScheduleAdminController.ScheduleRequest { CronExpression = "" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_MalformedCron_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Schedule("tenant-1", new RotationScheduleAdminController.ScheduleRequest { CronExpression = "not-cron" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_HappyPath_Creates()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Schedule("tenant-create", new RotationScheduleAdminController.ScheduleRequest
        {
            CronExpression = "0 0 * * *", Enabled = true, Notes = "nightly",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.RotationSchedules.FirstOrDefaultAsync(s => s.TenantId == "tenant-create");
        Assert.NotNull(row);
        Assert.Equal("0 0 * * *", row!.CronExpression);
        Assert.True(row.Enabled);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_SecondCall_UpdatesInPlace()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c1 = MakeController(ctx, cookies);
        await c1.Schedule("tenant-update", new RotationScheduleAdminController.ScheduleRequest
        {
            CronExpression = "0 0 * * *", Enabled = true,
        }, CancellationToken.None);

        var (cookies2, ctx2) = await MakeSessionAsync();
        var c2 = MakeController(ctx2, cookies2);
        await c2.Schedule("tenant-update", new RotationScheduleAdminController.ScheduleRequest
        {
            CronExpression = "*/5 * * * *", Enabled = false,
        }, CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.RotationSchedules.Where(s => s.TenantId == "tenant-update").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("*/5 * * * *", rows[0].CronExpression);
        Assert.False(rows[0].Enabled);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_StampsAuditRow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Schedule("tenant-audit", new RotationScheduleAdminController.ScheduleRequest
        {
            CronExpression = "0 0 * * *",
        }, CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.ReconnectAuditEntries
            .Where(a => a.Kind == ReconnectAuditEntry.KindAuthJwksRotationScheduled)
            .ToListAsync();
        Assert.NotEmpty(audits);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Schedule_NotesTooLong_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Schedule("tenant-1", new RotationScheduleAdminController.ScheduleRequest
        {
            CronExpression = "0 0 * * *",
            Notes = new string('n', RotationScheduleEntity.MaxNotesLength + 1),
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }
}

[Collection("DbSerial")]
public sealed class RotationScheduledExecutorServiceTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w21-rotexec-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public RotationScheduledExecutorServiceTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w21-rotexec-{Guid.NewGuid():N}.sqlite");
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

    private async Task<DateTime> SeedScheduleAndPolicyAsync(string tenant, string cron, bool enabled = true)
    {
        var now = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        db.RotationSchedules.Add(new RotationScheduleEntity
        {
            TenantId = tenant, CronExpression = cron, Enabled = enabled,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        });
        db.PerTenantJwksRotationPolicies.Add(new PerTenantJwksRotationPolicy
        {
            TenantId = tenant,
            RotationStartUtc = new DateTimeOffset(now.AddDays(-1), TimeSpan.Zero),
            RotationCompleteUtc = new DateTimeOffset(now.AddDays(29), TimeSpan.Zero),
            UpdatedAt = now.AddDays(-1),
        });
        await db.SaveChangesAsync();
        return now;
    }

    private RotationScheduledExecutorService MakeExecutor(DateTime fixedNow, JwtScheduledRotationMetrics? metrics = null)
        => new RotationScheduledExecutorService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RotationScheduledExecutorService>.Instance,
            metrics,
            () => fixedNow);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task RunOnce_NoSchedules_ReturnsZero()
    {
        var ex = MakeExecutor(DateTime.UtcNow);
        var executed = await ex.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, executed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task RunOnce_CronDoesNotMatch_RecordsSkipped()
    {
        var now = await SeedScheduleAndPolicyAsync("tenant-skip", "30 12 * * *");
        var metrics = new JwtScheduledRotationMetrics();
        var ex = MakeExecutor(now, metrics);
        var executed = await ex.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, executed);
        Assert.Equal(1, metrics.Get("tenant-skip", JwtScheduledRotationMetrics.StatusSkipped));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task RunOnce_CronMatches_AdvancesPolicy_RecordsSuccess()
    {
        var now = await SeedScheduleAndPolicyAsync("tenant-success", "0 12 * * *");
        var metrics = new JwtScheduledRotationMetrics();
        var ex = MakeExecutor(now, metrics);
        var executed = await ex.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, executed);
        Assert.Equal(1, metrics.Get("tenant-success", JwtScheduledRotationMetrics.StatusSuccess));

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var policy = await db.PerTenantJwksRotationPolicies.FirstAsync(p => p.TenantId == "tenant-success");
        Assert.True(policy.RotationStartUtc >= new DateTimeOffset(now.AddSeconds(-1), TimeSpan.Zero));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task RunOnce_DisabledSchedule_NotEvaluated()
    {
        var now = await SeedScheduleAndPolicyAsync("tenant-disabled", "0 12 * * *", enabled: false);
        var metrics = new JwtScheduledRotationMetrics();
        var ex = MakeExecutor(now, metrics);
        var executed = await ex.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, executed);
        Assert.Equal(0, metrics.Get("tenant-disabled", JwtScheduledRotationMetrics.StatusSuccess));
        Assert.Equal(0, metrics.Get("tenant-disabled", JwtScheduledRotationMetrics.StatusSkipped));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task RunOnce_NoPolicy_RecordsError()
    {
        var now = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>())
        {
            db.RotationSchedules.Add(new RotationScheduleEntity
            {
                TenantId = "tenant-error", CronExpression = "0 12 * * *", Enabled = true,
                CreatedAtUtc = now, UpdatedAtUtc = now,
            });
            await db.SaveChangesAsync();
        }
        var metrics = new JwtScheduledRotationMetrics();
        var ex = MakeExecutor(now, metrics);
        await ex.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, metrics.Get("tenant-error", JwtScheduledRotationMetrics.StatusError));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task RunOnce_Idempotent_WithinSameMinute()
    {
        var now = await SeedScheduleAndPolicyAsync("tenant-idemp", "0 12 * * *");
        var metrics = new JwtScheduledRotationMetrics();
        var ex = MakeExecutor(now, metrics);
        var first = await ex.RunOnceAsync(CancellationToken.None);
        var second = await ex.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, metrics.Get("tenant-idemp", JwtScheduledRotationMetrics.StatusSuccess));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task RunOnce_StampsAuditRow()
    {
        var now = await SeedScheduleAndPolicyAsync("tenant-audit-exec", "0 12 * * *");
        var ex = MakeExecutor(now);
        await ex.RunOnceAsync(CancellationToken.None);
        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.ReconnectAuditEntries
            .Where(a => a.Kind == ReconnectAuditEntry.KindAuthJwksRotationScheduledExecuted)
            .ToListAsync();
        Assert.NotEmpty(audits);
    }
}

public sealed class JwtScheduledRotationMetricsTests
{
    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MetricName_IsStable()
    {
        Assert.Equal("jwt_scheduled_rotation_total", JwtScheduledRotationMetrics.MetricName);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void StatusConstants_AreStable()
    {
        Assert.Equal("success", JwtScheduledRotationMetrics.StatusSuccess);
        Assert.Equal("error", JwtScheduledRotationMetrics.StatusError);
        Assert.Equal("skipped", JwtScheduledRotationMetrics.StatusSkipped);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Record_Accumulates()
    {
        var m = new JwtScheduledRotationMetrics();
        m.Record("tenant-a", JwtScheduledRotationMetrics.StatusSuccess);
        m.Record("tenant-a", JwtScheduledRotationMetrics.StatusSuccess);
        Assert.Equal(2, m.Get("tenant-a", JwtScheduledRotationMetrics.StatusSuccess));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Snapshot_ReflectsRecords()
    {
        var m = new JwtScheduledRotationMetrics();
        m.Record("tenant-a", JwtScheduledRotationMetrics.StatusSuccess);
        m.Record("tenant-b", JwtScheduledRotationMetrics.StatusError);
        var snap = m.Snapshot();
        Assert.Contains(("tenant-a", JwtScheduledRotationMetrics.StatusSuccess), snap.Keys);
        Assert.Contains(("tenant-b", JwtScheduledRotationMetrics.StatusError), snap.Keys);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndType()
    {
        var m = new JwtScheduledRotationMetrics();
        m.Record("tenant-a", JwtScheduledRotationMetrics.StatusSuccess);
        var sb = new System.Text.StringBuilder();
        m.AppendPrometheus(sb);
        var s = sb.ToString();
        Assert.Contains("# HELP jwt_scheduled_rotation_total", s);
        Assert.Contains("# TYPE jwt_scheduled_rotation_total counter", s);
        Assert.Contains("tenant=\"tenant-a\"", s);
        Assert.Contains("status=\"success\"", s);
    }
}
