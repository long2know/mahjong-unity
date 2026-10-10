using Mahjong.Autotable.Api.Changsha.Audit;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Audit;

/// <summary>
/// Phase J Wave 10 — <see cref="AuditPruningService"/> contract tests.
///
/// <para>The service is wired as a <see cref="BackgroundService"/> in
/// production but exposes <c>PruneOnceAsync</c> for synchronous test
/// invocation so we don't need to spin its timer.</para>
///
/// <para>Each fact seeds a mix of fresh + stale rows into
/// <c>ReconnectAuditEntries</c> + <c>CspViolations</c>, runs one
/// prune pass, and asserts on the remaining row set.</para>
/// </summary>
[Collection("DbSerial")]
public class AuditPruningServiceTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-audit-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            // The hosted service tick stays off — we drive PruneOnceAsync
            // directly. Without Enabled=false the BackgroundService would
            // schedule its 24h timer against the test DB which is harmless
            // but pollutes the host's task list during teardown.
            b.UseSetting("Audit:Enabled", "false");
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private async Task SeedReconnectAsync(DateTime at)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "p-" + Guid.NewGuid().ToString("N")[..8],
            OldTokenId = Guid.NewGuid(),
            NewTokenId = Guid.NewGuid(),
            Ipv4Hash = new string('0', 64),
            UserAgentHash = new string('0', 64),
            At = at,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedCspAsync(DateTime receivedAt)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CspViolations.Add(new CspViolation
        {
            DocumentUri = "https://example.test/",
            ViolatedDirective = "script-src",
            EffectiveDirective = "script-src",
            BlockedUri = "inline",
            RawJson = "{}",
            ReceivedAt = receivedAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task PruneOnce_DeletesEntriesOlderThanReconnectRetention()
    {
        Assert.NotNull(_factory);
        var now = DateTime.UtcNow;
        // 31 days old — should be pruned at default 30d retention.
        await SeedReconnectAsync(now.AddDays(-31));
        // 5 days old — well inside retention, must survive.
        await SeedReconnectAsync(now.AddDays(-5));

        var pruner = _factory!.Services.GetRequiredService<AuditPruningService>();
        var report = await pruner.PruneOnceAsync();
        Assert.Equal(1, report.ReconnectDeleted);

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.ReconnectAuditEntries.ToListAsync();
        Assert.Single(remaining);
        Assert.True(remaining[0].At > now.AddDays(-30));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task PruneOnce_DeletesEntriesOlderThanCspRetention()
    {
        Assert.NotNull(_factory);
        var now = DateTime.UtcNow;
        // 91 days old — should be pruned at default 90d retention.
        await SeedCspAsync(now.AddDays(-91));
        // 10 days old — must survive.
        await SeedCspAsync(now.AddDays(-10));

        var pruner = _factory!.Services.GetRequiredService<AuditPruningService>();
        var report = await pruner.PruneOnceAsync();
        Assert.Equal(1, report.CspDeleted);

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.CspViolations.ToListAsync();
        Assert.Single(remaining);
        Assert.True(remaining[0].ReceivedAt > now.AddDays(-90));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task PruneOnce_IsIdempotent()
    {
        Assert.NotNull(_factory);
        var now = DateTime.UtcNow;
        await SeedReconnectAsync(now.AddDays(-100));
        await SeedCspAsync(now.AddDays(-200));

        var pruner = _factory!.Services.GetRequiredService<AuditPruningService>();
        var first = await pruner.PruneOnceAsync();
        Assert.Equal(1, first.ReconnectDeleted);
        Assert.Equal(1, first.CspDeleted);

        // Second pass — nothing left older than the cutoff.
        var second = await pruner.PruneOnceAsync();
        Assert.Equal(0, second.ReconnectDeleted);
        Assert.Equal(0, second.CspDeleted);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task PruneOnce_KeepsAllFreshEntries()
    {
        Assert.NotNull(_factory);
        var now = DateTime.UtcNow;
        // Five fresh rows in each table; none should be pruned.
        for (var i = 0; i < 5; i++)
        {
            await SeedReconnectAsync(now.AddDays(-i));
            await SeedCspAsync(now.AddDays(-i));
        }

        var pruner = _factory!.Services.GetRequiredService<AuditPruningService>();
        var report = await pruner.PruneOnceAsync();
        Assert.Equal(0, report.ReconnectDeleted);
        Assert.Equal(0, report.CspDeleted);

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(5, await db.ReconnectAuditEntries.CountAsync());
        Assert.Equal(5, await db.CspViolations.CountAsync());
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task PruneOnce_HandlesEmptyTables()
    {
        Assert.NotNull(_factory);
        var pruner = _factory!.Services.GetRequiredService<AuditPruningService>();
        var report = await pruner.PruneOnceAsync();
        Assert.Equal(0, report.ReconnectDeleted);
        Assert.Equal(0, report.CspDeleted);
    }
}
