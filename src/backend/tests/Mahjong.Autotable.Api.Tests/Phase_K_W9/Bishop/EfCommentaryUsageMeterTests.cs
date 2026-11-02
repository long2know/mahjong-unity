using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Bishop;

/// <summary>
/// Phase K Wave 9 — Bishop. Hard-asserted facts for the EF-backed
/// commentary usage meter. Drives the meter against a real SQLite
/// database so the optimistic-concurrency + monthly-cap behaviour is
/// exercised end-to-end.
///
/// <list type="number">
///   <item>Record / read round-trips a token tally.</item>
///   <item>Successive records accumulate into the monthly total.</item>
///   <item>Zero-token calls are no-ops.</item>
///   <item>Per-game local tally tracks across calls.</item>
///   <item>The monthly cap surface returns false for cap=0.</item>
///   <item>The monthly cap surface returns true when over the cap.</item>
///   <item>UsageCapExceededException carries the supplied message.</item>
///   <item>Counts persist across meter instances (multi-replica).</item>
///   <item>The async record path applies the same accumulation.</item>
/// </list>
///
/// <para>Phase K Wave 15 — Bishop. <c>[Collection("DbSerial")]</c>
/// applied to close the W12-W14 DbSerial migration thread tracked
/// by Vasquez's <c>Phase_K_W14/Vasquez/db-serial-migration-completion.md</c>
/// (the file lives under <c>Phase_K_W9/Bishop/</c> per the
/// lane-map overrides, so only a Bishop-attributed commit could
/// land it). The SQLite-backed factory mutates a per-test
/// temp-file database; serialising the collection prevents two
/// W9-vintage Bishop tests from racing on the same in-memory
/// EF model cache on cold-start. See
/// <c>Phase_K_W15/Bishop/db-serial-completion.md</c>.</para>
/// </summary>
[Collection("DbSerial")]
public sealed class EfCommentaryUsageMeterTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-meter-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.PersistSnapshots = false;
                    o.BotTurnDelayMs = 1;
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

    private EfCommentaryUsageMeter NewMeter() => new(
        _factory!.Services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<EfCommentaryUsageMeter>.Instance);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void RecordUsage_AndMonthlyRead_RoundTrips()
    {
        var m = NewMeter();
        m.RecordUsage(Guid.NewGuid(), 100, 50);
        var now = DateTime.UtcNow;
        var total = m.MonthlyTokens(now);
        Assert.True(total >= 150, $"expected ≥ 150, got {total}");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void SuccessiveRecords_AccumulateMonthlyTotal()
    {
        var m = NewMeter();
        var before = m.MonthlyTokens(DateTime.UtcNow);
        m.RecordUsage(Guid.NewGuid(), 100, 0);
        m.RecordUsage(Guid.NewGuid(), 0, 200);
        m.RecordUsage(Guid.NewGuid(), 50, 50);
        var after = m.MonthlyTokens(DateTime.UtcNow);
        Assert.Equal(before + 400, after);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void ZeroTokenCalls_AreNoops()
    {
        var m = NewMeter();
        var before = m.MonthlyTokens(DateTime.UtcNow);
        m.RecordUsage(Guid.NewGuid(), 0, 0);
        m.RecordUsage(Guid.NewGuid(), 0, 0);
        Assert.Equal(before, m.MonthlyTokens(DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void NegativeTokens_AreClampedToZero()
    {
        var m = NewMeter();
        var before = m.MonthlyTokens(DateTime.UtcNow);
        m.RecordUsage(Guid.NewGuid(), -1_000, -500);
        Assert.Equal(before, m.MonthlyTokens(DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void PerGameTokens_TracksAcrossCalls()
    {
        var m = NewMeter();
        var g = Guid.NewGuid();
        m.RecordUsage(g, 10, 20);
        m.RecordUsage(g, 30, 40);
        Assert.Equal(100, m.PerGameTokens(g));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void ExceedsMonthlyCap_ZeroCap_ReturnsFalse()
    {
        var m = NewMeter();
        m.RecordUsage(Guid.NewGuid(), 1_000_000, 1_000_000);
        Assert.False(m.ExceedsMonthlyCap(0, DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void ExceedsMonthlyCap_OverCap_ReturnsTrue()
    {
        var m = NewMeter();
        m.RecordUsage(Guid.NewGuid(), 600, 600);
        Assert.True(m.ExceedsMonthlyCap(100, DateTime.UtcNow));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void UsageCapExceededException_CarriesMessage()
    {
        var ex = new UsageCapExceededException("monthly token cap hit");
        Assert.Contains("monthly token cap", ex.Message);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void Counts_PersistAcrossMeterInstances()
    {
        var m1 = NewMeter();
        m1.RecordUsage(Guid.NewGuid(), 100, 50);
        var m2 = NewMeter();
        var total = m2.MonthlyTokens(DateTime.UtcNow);
        Assert.True(total >= 150, $"expected ≥ 150 across replicas, got {total}");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public async Task RecordUsageAsync_AppliesAccumulation()
    {
        var m = NewMeter();
        await m.RecordUsageAsync(Guid.NewGuid(), 25, 25);
        await m.RecordUsageAsync(Guid.NewGuid(), 25, 25);
        var total = m.MonthlyTokens(DateTime.UtcNow);
        Assert.True(total >= 100);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void MaxConcurrencyRetries_IsCanonical()
    {
        Assert.Equal(3, EfCommentaryUsageMeter.MaxConcurrencyRetries);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public async Task PersistedRow_StructureIsConsistent()
    {
        var m = NewMeter();
        m.RecordUsage(Guid.NewGuid(), 100, 50);

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var row = await db.CommentaryUsage
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PeriodYear == now.Year && r.PeriodMonth == now.Month);
        Assert.NotNull(row);
        Assert.True(row!.InputTokens >= 100);
        Assert.True(row.OutputTokens >= 50);
        Assert.True(row.RequestCount >= 1);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void Constructor_NullScopeFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EfCommentaryUsageMeter(null!, NullLogger<EfCommentaryUsageMeter>.Instance));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void InterfaceContract_DefaultAsyncMethod_DelegatesToSync()
    {
        // The default async shape on ICommentaryUsageMeter delegates
        // to the sync RecordUsage. The in-memory meter doesn't
        // override it, so we can drive that path through the
        // interface to confirm the contract.
        ICommentaryUsageMeter mem = new InMemoryCommentaryUsageMeter();
        var t = mem.RecordUsageAsync(Guid.NewGuid(), 1, 1);
        Assert.True(t.IsCompletedSuccessfully);
        Assert.Equal(2, mem.MonthlyTokens(DateTime.UtcNow));
    }
}
