using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TournamentEntity = Mahjong.Autotable.Api.Data.Entities.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. HTTP-shape tests for the new
/// <see cref="SwissPairingAdminController"/> surface. Verifies
/// the admin auth gate, the X-Admin-Reason header contract,
/// and the error-code → HTTP-shape mapping.
/// </summary>
[Collection("DbSerial")]
public sealed class SwissPairingAdminControllerTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w20-swiss-admin-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SwissPairingAdminControllerTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w20-swiss-admin-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<ISwissPairingService, FideC04SwissPairingService>();
        services.AddSingleton<SwissPairingService>();
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SwissPairingAdminController MakeController(
        HttpContext httpContext,
        AuthCookieService cookies,
        string? adminReason = "swiss-pair-next-test")
    {
        var controller = new SwissPairingAdminController(
            cookies,
            _sp.GetRequiredService<SwissPairingService>());
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        if (adminReason is not null)
        {
            httpContext.Request.Headers[SwissPairingAdminController.AdminReasonHeader] = adminReason;
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

    private async Task<Guid> SeedTournamentAsync(string format = "swiss", int playerCount = 4)
    {
        var tid = Guid.NewGuid();
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tournaments.Add(new TournamentEntity
        {
            Id = tid,
            Name = "swiss-admin-test",
            Format = format,
            Status = "in-progress",
        });
        for (var i = 0; i < playerCount; i++)
        {
            db.TournamentRegistrations.Add(new TournamentRegistration
            {
                TournamentId = tid,
                PlayerId = $"player-{i:D2}",
                Seed = i,
            });
        }
        await db.SaveChangesAsync();
        return tid;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_NoSession_Returns401()
    {
        var cookies = new AuthCookieService(_sp.GetRequiredService<IServiceScopeFactory>(), new AuthOptions());
        var ctx = new DefaultHttpContext();
        var c = MakeController(ctx, cookies);
        var r = await c.PairNextRound(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_NonAdmin_Returns403()
    {
        var (cookies, ctx) = await MakeSessionAsync(role: "player");
        var c = MakeController(ctx, cookies);
        var r = await c.PairNextRound(Guid.NewGuid(), CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, s.StatusCode);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_MissingReasonHeader_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, adminReason: null);
        var r = await c.PairNextRound(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_EmptyReasonHeader_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies, adminReason: "   ");
        var r = await c.PairNextRound(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_TournamentNotFound_Returns404()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.PairNextRound(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_NonSwissFormat_Returns400()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var tid = await SeedTournamentAsync(format: "round-robin");
        var c = MakeController(ctx, cookies);
        var r = await c.PairNextRound(tid, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(r);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public async Task PairNextRound_HappyPath_Returns200WithPairings()
    {
        var (cookies, ctx) = await MakeSessionAsync();
        var tid = await SeedTournamentAsync();
        var c = MakeController(ctx, cookies);
        var r = await c.PairNextRound(tid, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r);
    }
}
