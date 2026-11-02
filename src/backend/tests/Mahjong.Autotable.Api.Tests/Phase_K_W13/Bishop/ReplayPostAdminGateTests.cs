using System.Net;
using System.Net.Http.Json;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Replays;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Bishop;

/// <summary>
/// Phase K Wave 13 — Bishop. Hard-asserted contract for the
/// admin-gated <c>POST /api/replays</c> surface.
///
/// <list type="number">
///   <item><see cref="ReplayOptions"/> exposes
///         <see cref="ReplayOptions.RequireAdminForPost"/>.</item>
///   <item>Default is <c>true</c> (gate engaged out of the box).</item>
///   <item>Anonymous POST → 401 when the gate is on.</item>
///   <item>Anonymous POST → 201 when the gate is disabled
///         (Replays:RequireAdminForPost=false).</item>
///   <item>GET endpoint is NOT admin-gated — anonymous still
///         retrieves an existing replay.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class ReplayPostAdminGateTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> NewFactory(bool requireAdmin)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w13-replay-{Guid.NewGuid():N}.db");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Replays:RequireAdminForPost", requireAdmin ? "true" : "false");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        _ = factory.Server;
        return factory;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Options_HasRequireAdminForPostProperty()
    {
        var opts = new ReplayOptions();
        // Property accessible.
        opts.RequireAdminForPost = true;
        Assert.True(opts.RequireAdminForPost);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Options_DefaultIsTrue()
    {
        var opts = new ReplayOptions();
        Assert.True(opts.RequireAdminForPost);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task AnonymousPost_Returns401_WhenGateEngaged()
    {
        _factory = NewFactory(requireAdmin: true);
        using var client = _factory.CreateClient();
        var body = new
        {
            gameId = Guid.NewGuid(),
            completedAt = DateTime.UtcNow,
            variant = "changsha-v1",
            turnCount = 1,
            payload = new { hello = "world" },
        };
        using var resp = await client.PostAsJsonAsync("/api/replays", body);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task AnonymousPost_Returns201_WhenGateDisabled()
    {
        _factory = NewFactory(requireAdmin: false);
        using var client = _factory.CreateClient();
        var body = new
        {
            gameId = Guid.NewGuid(),
            completedAt = DateTime.UtcNow,
            variant = "changsha-v1",
            turnCount = 1,
            payload = new { hello = "world" },
        };
        using var resp = await client.PostAsJsonAsync("/api/replays", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact, Trait("Category", "Replays"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task AnonymousGet_ReturnsNotFound_NotUnauthorized_WhenGateEngaged()
    {
        // GET path is not admin-gated. An unknown id → 404
        // (the 404 here proves the request reached the controller
        // without an auth challenge).
        _factory = NewFactory(requireAdmin: true);
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync($"/api/replays/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
