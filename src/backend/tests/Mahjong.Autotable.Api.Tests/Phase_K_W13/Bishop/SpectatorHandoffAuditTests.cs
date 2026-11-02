using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Spectator;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Bishop;

/// <summary>
/// Phase K Wave 13 — Bishop. Hard-asserted contract for the
/// spectator handoff audit trail
/// (<see cref="ISpectatorHandoffAuditStore"/>).
///
/// <list type="number">
///   <item><see cref="SpectatorHandoffAuditRecord"/> exists.</item>
///   <item><see cref="ISpectatorHandoffAuditStore"/> exists.</item>
///   <item><see cref="InMemorySpectatorHandoffAuditStore"/>
///         implements the interface.</item>
///   <item><see cref="EfSpectatorHandoffAuditStore"/>
///         implements the interface.</item>
///   <item>Default storage impl = "InMemory".</item>
///   <item>Default retention = 30 days.</item>
///   <item>Insert + List round-trips a row (in-memory).</item>
///   <item>Insert + List round-trips a row (EF).</item>
///   <item>ListByGame orders rows descending by IssuedAt.</item>
///   <item>SweepExpiredAsync drops rows older than cutoff.</item>
///   <item>SweepExpiredAsync leaves recent rows alone.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class SpectatorHandoffAuditTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w13-spec-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
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

    private EfSpectatorHandoffAuditStore NewEfStore() =>
        new(_factory!.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfSpectatorHandoffAuditStore>.Instance);

    private static SpectatorHandoffAuditRecord MakeRow(
        Guid gameId, string userId = "player-A", DateTime? when = null) =>
        new()
        {
            UserId = userId,
            GameId = gameId,
            TokenJti = Guid.NewGuid().ToString("D"),
            IssuedAt = when ?? DateTime.UtcNow,
            Scope = $"spectator:{gameId:D}",
            ClientIp = "127.0.0.1",
            UserAgent = "test-agent/1.0",
        };

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Entity_TypeExists()
    {
        Assert.NotNull(typeof(SpectatorHandoffAuditRecord));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Interface_TypeExists()
    {
        Assert.NotNull(typeof(ISpectatorHandoffAuditStore));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void InMemoryStore_ImplementsInterface()
    {
        Assert.True(typeof(ISpectatorHandoffAuditStore)
            .IsAssignableFrom(typeof(InMemorySpectatorHandoffAuditStore)));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void EfStore_ImplementsInterface()
    {
        Assert.True(typeof(ISpectatorHandoffAuditStore)
            .IsAssignableFrom(typeof(EfSpectatorHandoffAuditStore)));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Options_DefaultStorageImplIsInMemory()
    {
        var opts = new SpectatorHandoffAuditOptions();
        Assert.Equal("InMemory", opts.StorageImpl);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Options_DefaultRetentionIs30Days()
    {
        var opts = new SpectatorHandoffAuditOptions();
        Assert.Equal(30, opts.RetentionDays);
        Assert.Equal(30, SpectatorHandoffAuditOptions.DefaultRetentionDays);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task InMemory_InsertAndList_RoundTrips()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        var gameId = Guid.NewGuid();
        await store.InsertAsync(MakeRow(gameId));
        var rows = await store.ListByGameAsync(gameId);
        Assert.Single(rows);
        Assert.Equal(gameId, rows[0].GameId);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Ef_InsertAndList_RoundTrips()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        await store.InsertAsync(MakeRow(gameId));
        var rows = await store.ListByGameAsync(gameId);
        Assert.Single(rows);
        Assert.Equal(gameId, rows[0].GameId);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task ListByGame_OrdersByIssuedAtDescending()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        var gameId = Guid.NewGuid();
        var t0 = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        await store.InsertAsync(MakeRow(gameId, "u1", t0));
        await store.InsertAsync(MakeRow(gameId, "u2", t0.AddMinutes(1)));
        await store.InsertAsync(MakeRow(gameId, "u3", t0.AddMinutes(2)));
        var rows = await store.ListByGameAsync(gameId);
        Assert.Equal(3, rows.Count);
        Assert.Equal("u3", rows[0].UserId);
        Assert.Equal("u2", rows[1].UserId);
        Assert.Equal("u1", rows[2].UserId);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Sweep_DropsExpiredRows()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        var gameId = Guid.NewGuid();
        var ancient = DateTime.UtcNow.AddDays(-60);
        await store.InsertAsync(MakeRow(gameId, "u-old", ancient));
        await store.InsertAsync(MakeRow(gameId, "u-new"));
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var removed = await store.SweepExpiredAsync(cutoff);
        Assert.Equal(1, removed);
        var rows = await store.ListByGameAsync(gameId);
        Assert.Single(rows);
        Assert.Equal("u-new", rows[0].UserId);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Sweep_LeavesRecentRowsAlone()
    {
        var store = new InMemorySpectatorHandoffAuditStore();
        var gameId = Guid.NewGuid();
        await store.InsertAsync(MakeRow(gameId));
        await store.InsertAsync(MakeRow(gameId));
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var removed = await store.SweepExpiredAsync(cutoff);
        Assert.Equal(0, removed);
        Assert.Equal(2, await store.CountAsync());
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Ef_Sweep_Roundtrips()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        await store.InsertAsync(MakeRow(gameId, "u-old", DateTime.UtcNow.AddDays(-60)));
        await store.InsertAsync(MakeRow(gameId, "u-new"));
        var removed = await store.SweepExpiredAsync(DateTime.UtcNow.AddDays(-30));
        Assert.Equal(1, removed);
    }
}
