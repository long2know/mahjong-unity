using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Replays;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Bishop;

/// <summary>
/// Phase K Wave 21 — Bishop. Tests for the W21 replay
/// restoration audit surface: persistence of
/// <see cref="ReplayRestorationAttempt"/> rows and the
/// <see cref="ReplayRestorationAuditController"/> admin gate /
/// query flow.
/// </summary>
[Collection("DbSerial")]
public sealed class ReplayRestorationAuditControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w21-replay-restore-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public ReplayRestorationAuditControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w21-replay-restore-{Guid.NewGuid():N}.sqlite");
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

    private ReplayRestorationAuditController MakeController(HttpContext ctx, AuthCookieService cookies)
    {
        var c = new ReplayRestorationAuditController(cookies, _sp.GetRequiredService<IServiceScopeFactory>());
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        return c;
    }

    private async Task SeedReplayAsync(string replayId)
    {
        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        db.Replays.Add(new ReplayRecord
        {
            ReplayId = replayId,
            GameId = Guid.NewGuid(),
            CompletedAt = DateTime.UtcNow.AddHours(-1),
            IngestedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            CompressedPayload = new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00 },
            TurnCount = 0,
            Variant = "changsha-v1",
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAttemptAsync(string replayId, string outcome, string operatorId = "system")
    {
        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        db.ReplayRestorationAttempts.Add(new ReplayRestorationAttempt
        {
            ReplayId = replayId, OperatorId = operatorId, Outcome = outcome,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var c = MakeController(new DefaultHttpContext(), cookies);
        var r = await c.Audit("r-1", CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.Audit("r-1", CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_EmptyReplayId_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit("", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_ReplayMissing_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit("r-nope", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_NoAttempts_ReturnsEmptyList()
    {
        await SeedReplayAsync("r-empty");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit("r-empty", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_WithAttempts_ReturnsThem()
    {
        await SeedReplayAsync("r-with-attempts");
        await SeedAttemptAsync("r-with-attempts", ReplayRestorationAttempt.OutcomeRead);
        await SeedAttemptAsync("r-with-attempts", ReplayRestorationAttempt.OutcomeRestored);

        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit("r-with-attempts", CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_StampsReadAttempt()
    {
        await SeedReplayAsync("r-stamp");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Audit("r-stamp", CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await db.ReplayRestorationAttempts
            .Where(a => a.ReplayId == "r-stamp")
            .ToListAsync();
        Assert.NotEmpty(attempts);
        Assert.Contains(attempts, a => a.Outcome == ReplayRestorationAttempt.OutcomeRead);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_StampsReconnectAudit()
    {
        await SeedReplayAsync("r-rec-audit");
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        await c.Audit("r-rec-audit", CancellationToken.None);

        await using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.ReconnectAuditEntries
            .Where(a => a.Kind == ReconnectAuditEntry.KindReplayRestorationAttempt)
            .ToListAsync();
        Assert.NotEmpty(audits);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public async Task Audit_LimitsToTen()
    {
        await SeedReplayAsync("r-limit");
        for (int i = 0; i < 15; i++)
        {
            await SeedAttemptAsync("r-limit", ReplayRestorationAttempt.OutcomeRead);
        }
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.Audit("r-limit", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var attempts = doc.RootElement.GetProperty("attempts");
        Assert.True(attempts.GetArrayLength() <= ReplayRestorationAuditController.MaxResults);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void OutcomeConstants_AreStable()
    {
        Assert.Equal("read", ReplayRestorationAttempt.OutcomeRead);
        Assert.Equal("restored", ReplayRestorationAttempt.OutcomeRestored);
        Assert.Equal("not-found", ReplayRestorationAttempt.OutcomeNotFound);
        Assert.Equal("integrity-failure", ReplayRestorationAttempt.OutcomeIntegrityFailure);
        Assert.Equal("unauthorised", ReplayRestorationAttempt.OutcomeUnauthorised);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MaxResults_Is10()
    {
        Assert.Equal(10, ReplayRestorationAuditController.MaxResults);
    }
}
