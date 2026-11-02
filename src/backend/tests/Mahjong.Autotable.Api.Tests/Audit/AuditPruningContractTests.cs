using System.Linq;
using Mahjong.Autotable.Api.Changsha.Audit;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Audit;

/// <summary>
/// Phase J Wave 10 — supplemental contract tests for the
/// <see cref="AuditPruningService"/> beyond the row-counting facts that
/// Bishop ships in <see cref="AuditPruningServiceTests"/>. These pin the
/// surrounding wiring contracts that Bishop's row-counting tests skirt:
/// <list type="bullet">
///   <item>The service is registered as a hosted <see cref="IHostedService"/>
///         AND resolvable directly so test harnesses can drive it without
///         spinning its timer.</item>
///   <item><see cref="AuditPruningOptions"/> binds from configuration —
///         non-default values applied at host build time are reflected
///         on the resolved options instance.</item>
///   <item>Default retention values match the Wave 10 brief (30 / 90).</item>
///   <item><see cref="AuditPruningOptions.Enabled"/> = false at boot
///         prevents the BackgroundService loop from running (no spurious
///         deletes happen on the in-memory DB across the suite).</item>
///   <item>The prune report shape is forward-compatible — adding new
///         counter fields doesn't break the public read surface.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public class AuditPruningContractTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-audit-contract-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Audit:Enabled", "false");
            // Override retention values so the binding contract test can
            // confirm IConfiguration → IOptions wiring works end-to-end.
            b.UseSetting("Audit:ReconnectRetentionDays", "7");
            b.UseSetting("Audit:CspRetentionDays", "14");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
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

    // ────────────────────────────────────────────────────────────────────
    //  1. Service is registered as IHostedService AND directly resolvable
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public void Service_RegisteredAsHostedAndDirect()
    {
        Assert.NotNull(_factory);

        // Direct resolve — the row-counting tests call
        // `GetRequiredService<AuditPruningService>()`, which only works
        // if the service is registered concretely.
        var direct = _factory!.Services.GetService<AuditPruningService>();
        Assert.NotNull(direct);

        // Hosted-service registration — needed for the daily prune timer
        // to actually fire in production. The same instance can be both
        // (typical pattern: AddSingleton<AuditPruningService>() +
        // AddHostedService(sp => sp.GetRequiredService<AuditPruningService>())).
        var hosted = _factory.Services
            .GetServices<IHostedService>()
            .OfType<AuditPruningService>()
            .ToList();
        Assert.NotEmpty(hosted);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. AuditPruningOptions binds from IConfiguration
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public void Options_BindFromConfiguration()
    {
        Assert.NotNull(_factory);
        var opts = _factory!.Services.GetRequiredService<IOptions<AuditPruningOptions>>().Value;

        // Values set via UseSetting above must be reflected on the
        // IOptions instance. This is the wiring contract for operators
        // tuning retention via environment variables / appsettings.
        Assert.Equal(7, opts.ReconnectRetentionDays);
        Assert.Equal(14, opts.CspRetentionDays);
        Assert.False(opts.Enabled, "Audit:Enabled=false must propagate to the options instance.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Default retention matches Wave 10 brief (30 / 90 days)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public void Options_DefaultValues_MatchWave10Brief()
    {
        var defaults = new AuditPruningOptions();
        Assert.Equal(30, defaults.ReconnectRetentionDays);
        Assert.Equal(90, defaults.CspRetentionDays);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Disabled boot leaves rows untouched across a settle window
    //
    //  The background timer should NOT fire when Enabled=false; if it
    //  did, the seeded "ancient" rows would vanish even though we never
    //  invoked PruneOnceAsync.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task DisabledBoot_BackgroundTimerStaysOff()
    {
        Assert.NotNull(_factory);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = "p-disabled",
                OldTokenId = Guid.NewGuid(),
                NewTokenId = Guid.NewGuid(),
                Ipv4Hash = new string('0', 64),
                UserAgentHash = new string('0', 64),
                At = DateTime.UtcNow.AddYears(-1),  // very old; would prune
            });
            await db.SaveChangesAsync();
        }

        // Give a 250ms settle window for any rogue scheduling.
        await Task.Delay(250);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ageOldRows = await verifyDb.ReconnectAuditEntries
            .Where(r => r.PlayerId == "p-disabled")
            .CountAsync();
        Assert.Equal(1, ageOldRows);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Prune report shape — has ReconnectDeleted + CspDeleted counters
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task PruneReport_ExposesBothCounters()
    {
        Assert.NotNull(_factory);
        var pruner = _factory!.Services.GetRequiredService<AuditPruningService>();
        var report = await pruner.PruneOnceAsync();

        // The report is a value type (record / struct); both counters
        // must be readable as int via reflection so operators can wire
        // them to observability sinks.
        var t = report.GetType();
        var rec = t.GetProperty("ReconnectDeleted") ?? t.GetField("ReconnectDeleted") as System.Reflection.MemberInfo;
        var csp = t.GetProperty("CspDeleted") ?? t.GetField("CspDeleted") as System.Reflection.MemberInfo;
        Assert.NotNull(rec);
        Assert.NotNull(csp);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. PruneOnceAsync returns synchronously (no fire-and-forget leak)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-J-10")]
    public async Task PruneOnce_CompletesWithinReasonableTime()
    {
        Assert.NotNull(_factory);
        var pruner = _factory!.Services.GetRequiredService<AuditPruningService>();

        // An empty-table prune should be near-instantaneous; if Bishop
        // accidentally awaits a 24h timer here it would block forever.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pruner.PruneOnceAsync();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5_000,
            $"PruneOnceAsync took {sw.ElapsedMilliseconds}ms; expected < 5s.");
    }
}
