using Mahjong.Autotable.Api.Audit;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Bishop;

/// <summary>
/// Phase K Wave 22 — Bishop. Tests for the W22 audit-log
/// query admin endpoint: auth gate, filter combinations,
/// page/pageSize behaviour (default, cap, validation), and
/// the meta-audit row that the endpoint stamps on every read.
/// </summary>
[Collection("DbSerial")]
public sealed class AuditLogQueryControllerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbPath;
    private readonly AuthCookieService _cookies;

    public AuditLogQueryControllerTests()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "bishop-w22-audit-sqlite");
        Directory.CreateDirectory(scratch);
        _dbPath = Path.Combine(scratch, $"audit-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        _cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<HttpContext> MakeAdminContextAsync(string role = "admin")
    {
        var issue = new DefaultHttpContext();
        var s = await _cookies.IssueAsync(issue, $"p-{Guid.NewGuid():N}", Guid.NewGuid(), role);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{AuthCookieService.CookieName}={s.Token}";
        return ctx;
    }

    private AuditLogQueryController MakeController(HttpContext ctx)
    {
        return new AuditLogQueryController(_cookies, _sp.GetRequiredService<IServiceScopeFactory>())
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }

    private async Task SeedEntryAsync(string playerId, string kind, DateTime atUtc, string? detail = null)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Kind = kind,
            At = atUtc,
            Detail = detail,
        });
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_NoSession_Returns401()
    {
        var r = await MakeController(new DefaultHttpContext())
            .Query(null, null, null, null, null, null, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_NonAdmin_Returns403()
    {
        var ctx = await MakeAdminContextAsync(role: "player");
        var r = await MakeController(ctx).Query(null, null, null, null, null, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_EmptyDb_ReturnsEmpty()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, null, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_DefaultsPageAndSize()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var payload = ok.Value!.GetType().GetProperty("pageSize")!.GetValue(ok.Value);
        Assert.Equal(AuditLogQueryController.DefaultPageSize, payload);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_KindFilter_NarrowsResults()
    {
        await SeedEntryAsync("p1", "audit.k1", DateTime.UtcNow);
        await SeedEntryAsync("p2", "audit.k2", DateTime.UtcNow);
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("audit.k1", null, null, null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalCount = (int)ok.Value!.GetType().GetProperty("totalCount")!.GetValue(ok.Value)!;
        Assert.Equal(1, totalCount);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_ActorFilter_NarrowsResults()
    {
        await SeedEntryAsync("p1", "audit.x", DateTime.UtcNow);
        await SeedEntryAsync("p2", "audit.x", DateTime.UtcNow);
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, "p1", null, null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalCount = (int)ok.Value!.GetType().GetProperty("totalCount")!.GetValue(ok.Value)!;
        Assert.Equal(1, totalCount);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_FromFilter_ExcludesEarlier()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedEntryAsync("p1", "audit.x", t);
        await SeedEntryAsync("p1", "audit.x", t.AddDays(1));
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, t.AddHours(1).ToString("o"), null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalCount = (int)ok.Value!.GetType().GetProperty("totalCount")!.GetValue(ok.Value)!;
        // Only entries strictly later than t+1h count (1d > 1h)
        Assert.Equal(1, totalCount);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_ToFilter_ExcludesLater()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedEntryAsync("p1", "audit.x", t);
        await SeedEntryAsync("p1", "audit.x", t.AddDays(2));
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, t.AddDays(1).ToString("o"), null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalCount = (int)ok.Value!.GetType().GetProperty("totalCount")!.GetValue(ok.Value)!;
        Assert.Equal(1, totalCount);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_FromAfterTo_Returns400()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, "2027-01-01T00:00:00Z", "2026-01-01T00:00:00Z", null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_InvalidFrom_Returns400()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, "not-a-date", null, null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_InvalidTo_Returns400()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, "not-a-date", null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_PageZero_Returns400()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, 0, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_NegativePage_Returns400()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, -1, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_PageSizeZero_Returns400()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, null, 0, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_PageSizeAboveMax_CapsToMax()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, null, 5000, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var pageSize = (int)ok.Value!.GetType().GetProperty("pageSize")!.GetValue(ok.Value)!;
        Assert.Equal(AuditLogQueryController.MaxPageSize, pageSize);
        var capped = (bool)ok.Value!.GetType().GetProperty("pageSizeCapped")!.GetValue(ok.Value)!;
        Assert.True(capped);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_OrderingDescByAt()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedEntryAsync("p1", "audit.x", t);
        await SeedEntryAsync("p1", "audit.x", t.AddDays(2));
        await SeedEntryAsync("p1", "audit.x", t.AddDays(1));
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, "p1", null, null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var events = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("events")!.GetValue(ok.Value)!;
        var ats = events.Cast<object>().Select(e => (DateTime)e.GetType().GetProperty("at")!.GetValue(e)!).ToArray();
        Assert.True(ats[0] >= ats[1]);
        Assert.True(ats[1] >= ats[2]);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_EmitsMetaAuditRow()
    {
        await SeedEntryAsync("p1", "audit.x", DateTime.UtcNow);
        var ctx = await MakeAdminContextAsync();
        await MakeController(ctx).Query(null, null, null, null, null, null, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meta = await db.ReconnectAuditEntries.AsNoTracking()
            .Where(e => e.Kind == ReconnectAuditEntry.KindAuditLogQueried).ToListAsync();
        Assert.Single(meta);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_MetaAuditCarriesFilters()
    {
        var ctx = await MakeAdminContextAsync();
        await MakeController(ctx).Query("audit.x", "p1", null, null, null, null, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries.AsNoTracking()
            .FirstAsync(e => e.Kind == ReconnectAuditEntry.KindAuditLogQueried);
        Assert.NotNull(row.Detail);
        Assert.Contains("kind=audit.x", row.Detail!);
        Assert.Contains("actor=p1", row.Detail);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_TotalCountIndependentOfPaging()
    {
        var t = DateTime.UtcNow;
        for (int i = 0; i < 7; i++)
        {
            await SeedEntryAsync("pX", "audit.bulk", t.AddSeconds(i));
        }
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("audit.bulk", null, null, null, 1, 3, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalCount = (int)ok.Value!.GetType().GetProperty("totalCount")!.GetValue(ok.Value)!;
        var count = (int)ok.Value!.GetType().GetProperty("count")!.GetValue(ok.Value)!;
        Assert.Equal(7, totalCount);
        Assert.Equal(3, count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_TotalPagesRoundedUp()
    {
        var t = DateTime.UtcNow;
        for (int i = 0; i < 7; i++)
        {
            await SeedEntryAsync("pY", "audit.tp", t.AddSeconds(i));
        }
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("audit.tp", null, null, null, null, 3, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalPages = (int)ok.Value!.GetType().GetProperty("totalPages")!.GetValue(ok.Value)!;
        Assert.Equal(3, totalPages);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_Page2_OffsetsResults()
    {
        var t = DateTime.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            await SeedEntryAsync("pZ", "audit.pg", t.AddSeconds(i));
        }
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("audit.pg", null, null, null, 2, 2, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var count = (int)ok.Value!.GetType().GetProperty("count")!.GetValue(ok.Value)!;
        Assert.Equal(2, count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_PageBeyondEnd_ReturnsEmpty()
    {
        await SeedEntryAsync("pQ", "audit.beyond", DateTime.UtcNow);
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("audit.beyond", null, null, null, 99, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var count = (int)ok.Value!.GetType().GetProperty("count")!.GetValue(ok.Value)!;
        Assert.Equal(0, count);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_BlankFromAndTo_TreatedAsUnset()
    {
        await SeedEntryAsync("pp", "audit.blank", DateTime.UtcNow);
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, "   ", "   ", null, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_BlankKindFilter_DoesNotNarrow()
    {
        await SeedEntryAsync("pp", "audit.a", DateTime.UtcNow);
        await SeedEntryAsync("pp", "audit.b", DateTime.UtcNow);
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("   ", null, null, null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalCount = (int)ok.Value!.GetType().GetProperty("totalCount")!.GetValue(ok.Value)!;
        Assert.True(totalCount >= 2);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_ZeroResultMatchingFilter_StillEmitsMetaRow()
    {
        var ctx = await MakeAdminContextAsync();
        await MakeController(ctx).Query("audit.nonexistent", null, null, null, null, null, CancellationToken.None);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meta = await db.ReconnectAuditEntries.AsNoTracking()
            .CountAsync(e => e.Kind == ReconnectAuditEntry.KindAuditLogQueried);
        Assert.Equal(1, meta);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_FromEqualsTo_AllowsRow()
    {
        var t = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        await SeedEntryAsync("pq", "audit.eq", t);
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("audit.eq", null, t.ToString("o"), t.ToString("o"), null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var totalCount = (int)ok.Value!.GetType().GetProperty("totalCount")!.GetValue(ok.Value)!;
        Assert.Equal(1, totalCount);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_ReportsRequestedPageSize()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, null, 17, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var req = (int)ok.Value!.GetType().GetProperty("requestedPageSize")!.GetValue(ok.Value)!;
        Assert.Equal(17, req);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_BelowCap_NotMarkedCapped()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query(null, null, null, null, null, 10, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var capped = (bool)ok.Value!.GetType().GetProperty("pageSizeCapped")!.GetValue(ok.Value)!;
        Assert.False(capped);
    }

    [Fact, Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public async Task Query_FiltersReturnedInPayload()
    {
        var ctx = await MakeAdminContextAsync();
        var r = await MakeController(ctx).Query("k1", "actor1", null, null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var filters = ok.Value!.GetType().GetProperty("filters")!.GetValue(ok.Value)!;
        Assert.Equal("k1", filters.GetType().GetProperty("kind")!.GetValue(filters));
        Assert.Equal("actor1", filters.GetType().GetProperty("actor")!.GetValue(filters));
    }
}
