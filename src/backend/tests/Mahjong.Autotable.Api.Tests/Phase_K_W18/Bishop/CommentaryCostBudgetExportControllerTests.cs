using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Bishop;

/// <summary>
/// Phase K Wave 18 — Bishop. Contract tests for the new
/// commentary cost-budget historical export endpoint
/// (<see cref="CommentaryCostBudgetExportController"/>). Covers
/// admin auth gate, parameter parsing (YYYY-MM), window
/// validation, max-window clamp, CSV header + body shape, and
/// audit-row side effect.
/// </summary>
[Collection("DbSerial")]
public sealed class CommentaryCostBudgetExportControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w18-commentary-export-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public CommentaryCostBudgetExportControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w18-export-{Guid.NewGuid():N}.sqlite");
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

    private CommentaryCostBudgetExportController MakeController(
        HttpContext httpContext,
        AuthCookieService cookies)
    {
        var controller = new CommentaryCostBudgetExportController(
            cookies,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommentaryCostBudgetExportController>.Instance,
            options: null);
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

    private async Task SeedRowAsync(int year, int month, long inputTokens, long outputTokens, long requestCount)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CommentaryUsage.Add(new CommentaryUsageRecord
        {
            Id = Guid.NewGuid(),
            PeriodYear = year,
            PeriodMonth = month,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            RequestCount = requestCount,
            CreatedAt = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(year, month, 28, 0, 0, 0, DateTimeKind.Utc),
            RowVersion = new byte[] { 1 },
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> CountAuditRowsAsync(string kind)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries.CountAsync(e => e.Kind == kind);
    }

    // ─── auth gate ─────────────────────────────────────────────

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", "2025-03", null, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", "2025-03", null, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    // ─── parameter parsing ─────────────────────────────────────

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_NullFrom_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export(null, "2025-03", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_NullTo_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_InvalidFrom_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("not-a-date", "2025-03", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_FromAfterTo_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-06", "2025-03", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_WindowAboveMax_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2020-01", "2030-12", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void TryParseMonth_Valid_Parses()
    {
        Assert.True(CommentaryCostBudgetExportController.TryParseMonth("2025-03", out var y, out var m));
        Assert.Equal(2025, y);
        Assert.Equal(3, m);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void TryParseMonth_BareYear_Fails()
    {
        Assert.False(CommentaryCostBudgetExportController.TryParseMonth("2025", out _, out _));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void TryParseMonth_MonthOutOfRange_Fails()
    {
        Assert.False(CommentaryCostBudgetExportController.TryParseMonth("2025-13", out _, out _));
        Assert.False(CommentaryCostBudgetExportController.TryParseMonth("2025-00", out _, out _));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void TryParseMonth_YearBelowFloor_Fails()
    {
        Assert.False(CommentaryCostBudgetExportController.TryParseMonth("1999-01", out _, out _));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void TryParseMonth_Empty_Fails()
    {
        Assert.False(CommentaryCostBudgetExportController.TryParseMonth("", out _, out _));
        Assert.False(CommentaryCostBudgetExportController.TryParseMonth("  ", out _, out _));
        Assert.False(CommentaryCostBudgetExportController.TryParseMonth(null, out _, out _));
    }

    // ─── happy path + CSV shape ────────────────────────────────

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_Empty_ReturnsCsvWithHeaderOnly()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", "2025-03", null, CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(r);
        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.StartsWith(CommentaryCostBudgetExportController.CsvHeader, text);
        Assert.Equal(CommentaryCostBudgetExportController.CsvContentType, file.ContentType);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_WithRows_StreamsCsvBody()
    {
        await SeedRowAsync(2025, 1, 100_000, 50_000, 5);
        await SeedRowAsync(2025, 2, 200_000, 80_000, 10);
        await SeedRowAsync(2025, 3, 300_000, 100_000, 12);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", "2025-03", null, CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(r);
        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 rows.
        Assert.Equal(4, lines.Length);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_WindowFiltersOutOfRange()
    {
        await SeedRowAsync(2024, 12, 100, 200, 1);
        await SeedRowAsync(2025, 1, 100, 200, 1);
        await SeedRowAsync(2025, 2, 100, 200, 1);
        await SeedRowAsync(2025, 6, 100, 200, 1);
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", "2025-02", null, CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(r);
        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 rows
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_WritesAuditRow()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", "2025-02", null, CancellationToken.None);
        Assert.IsType<FileContentResult>(r);
        Assert.Equal(1, await CountAuditRowsAsync(ReconnectAuditEntry.KindCommentaryCostBudgetExport));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public async Task Export_AuditDetail_CapturesTenant()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Export("2025-01", "2025-02", tenant: "acme-corp", CancellationToken.None);
        Assert.IsType<FileContentResult>(r);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectAuditEntries.FirstAsync(e =>
            e.Kind == ReconnectAuditEntry.KindCommentaryCostBudgetExport);
        Assert.Contains("tenant=acme-corp", row.Detail);
        Assert.Contains("from=2025-01", row.Detail);
        Assert.Contains("to=2025-02", row.Detail);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void BuildCsv_Empty_OnlyHeader()
    {
        var csv = CommentaryCostBudgetExportController.BuildCsv(
            Array.Empty<CommentaryUsageRecord>(), 200_000L, 0m, 0.8);
        Assert.Equal(CommentaryCostBudgetExportController.CsvHeader + Environment.NewLine, csv);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void BuildCsv_PopulatesAllColumns()
    {
        var row = new CommentaryUsageRecord
        {
            Id = Guid.NewGuid(),
            PeriodYear = 2025,
            PeriodMonth = 4,
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            RequestCount = 42,
            CreatedAt = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 4, 28, 0, 0, 0, DateTimeKind.Utc),
        };
        var csv = CommentaryCostBudgetExportController.BuildCsv(
            new[] { row }, 200_000L, 10m, 0.8);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("2025,4,1000000,500000,1500000,42,200000,10,7.5,75", lines[1]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void BuildCsv_State_Healthy_BelowWarn()
    {
        var row = new CommentaryUsageRecord
        {
            PeriodYear = 2025, PeriodMonth = 1,
            InputTokens = 0, OutputTokens = 0,
        };
        var csv = CommentaryCostBudgetExportController.BuildCsv(
            new[] { row }, 200_000L, 10m, 0.8);
        Assert.Contains("Healthy", csv);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void BuildCsv_State_Warning_AtThreshold()
    {
        var row = new CommentaryUsageRecord
        {
            PeriodYear = 2025, PeriodMonth = 1,
            InputTokens = 1_600_000, OutputTokens = 0,
        };
        var csv = CommentaryCostBudgetExportController.BuildCsv(
            new[] { row }, 200_000L, 10m, 0.8);
        Assert.Contains("Warning", csv);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void BuildCsv_State_Exhausted_AtOrAboveCap()
    {
        var row = new CommentaryUsageRecord
        {
            PeriodYear = 2025, PeriodMonth = 1,
            InputTokens = 2_000_000, OutputTokens = 200_000,
        };
        var csv = CommentaryCostBudgetExportController.BuildCsv(
            new[] { row }, 200_000L, 10m, 0.8);
        Assert.Contains("Exhausted", csv);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void BuildCsv_NoCap_RowsAreHealthy()
    {
        var row = new CommentaryUsageRecord
        {
            PeriodYear = 2025, PeriodMonth = 1,
            InputTokens = 1_000_000, OutputTokens = 1_000_000,
        };
        var csv = CommentaryCostBudgetExportController.BuildCsv(
            new[] { row }, 200_000L, 0m, 0.8);
        Assert.Contains("Healthy", csv);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Constants_AreWireStable()
    {
        Assert.Equal(60, CommentaryCostBudgetExportController.MaxWindowMonths);
        Assert.Equal("text/csv; charset=utf-8", CommentaryCostBudgetExportController.CsvContentType);
        Assert.Contains("periodYear", CommentaryCostBudgetExportController.CsvHeader);
        Assert.Contains("usdSpent", CommentaryCostBudgetExportController.CsvHeader);
        Assert.Contains("state", CommentaryCostBudgetExportController.CsvHeader);
    }
}
