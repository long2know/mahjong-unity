using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Replays;
using Mahjong.Autotable.Api.Tests.Collections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Bishop;

/// <summary>
/// Phase K Wave 12 — Bishop. Hard-asserted contract for the
/// replay-by-id surface — both the persistence seam
/// (<see cref="IReplayStore"/>) and the round-trip gzip codec
/// on <see cref="ReplayRecord"/>.
///
/// <list type="number">
///   <item><see cref="ReplayOptions"/> exists with default
///         RetentionDays=90.</item>
///   <item><see cref="ReplayOptions.MaxCompressedBytes"/>
///         default 8 MiB.</item>
///   <item><see cref="InMemoryReplayStore"/> implements
///         <see cref="IReplayStore"/>.</item>
///   <item><see cref="EfReplayStore"/> implements
///         <see cref="IReplayStore"/>.</item>
///   <item>Insert mints a synthetic id when the caller leaves
///         it blank (in-memory).</item>
///   <item>Insert mints a synthetic id when the caller leaves
///         it blank (EF).</item>
///   <item>The minted id matches the canonical prefix
///         <c>r-</c>.</item>
///   <item>Insert→Get round-trips the row (in-memory).</item>
///   <item>Insert→Get round-trips the row (EF).</item>
///   <item>CompressPayload+DecompressPayload round-trips a JSON
///         payload byte-for-byte.</item>
///   <item>Insert pins ExpiresAt = CompletedAt + RetentionDays
///         (in-memory).</item>
///   <item>SweepExpiredAsync drops rows older than the cutoff
///         (in-memory).</item>
///   <item>SweepExpiredAsync drops rows older than the cutoff
///         (EF).</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class ReplayStorePersistenceFacts : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w12-replay-{Guid.NewGuid():N}.db");
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

    private EfReplayStore NewEfStore(ReplayOptions? opts = null) =>
        new(_factory!.Services.GetRequiredService<IServiceScopeFactory>(),
            opts ?? new ReplayOptions(),
            NullLogger<EfReplayStore>.Instance);

    private static ReplayRecord MakeRecord(string payload = "{\"turns\":[]}", DateTime? completedAt = null) =>
        new()
        {
            GameId = Guid.NewGuid(),
            CompletedAt = completedAt ?? DateTime.UtcNow,
            Variant = "changsha-v1",
            TurnCount = 0,
            CompressedPayload = ReplayRecord.CompressPayload(payload),
        };

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public void ReplayOptions_DefaultRetentionDaysIs90()
    {
        var opts = new ReplayOptions();
        Assert.Equal(90, opts.RetentionDays);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public void ReplayOptions_DefaultMaxCompressedBytesIs8MiB()
    {
        var opts = new ReplayOptions();
        Assert.Equal(8 * 1024 * 1024, opts.MaxCompressedBytes);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public void InMemoryStore_ImplementsInterface()
    {
        Assert.True(typeof(IReplayStore).IsAssignableFrom(typeof(InMemoryReplayStore)));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public void EfStore_ImplementsInterface()
    {
        Assert.True(typeof(IReplayStore).IsAssignableFrom(typeof(EfReplayStore)));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_InsertMintsId()
    {
        var store = new InMemoryReplayStore();
        var record = MakeRecord();
        var stored = await store.InsertAsync(record);
        Assert.False(string.IsNullOrWhiteSpace(stored.ReplayId));
        Assert.StartsWith("r-", stored.ReplayId);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_InsertMintsId()
    {
        var store = NewEfStore();
        var record = MakeRecord();
        var stored = await store.InsertAsync(record);
        Assert.False(string.IsNullOrWhiteSpace(stored.ReplayId));
        Assert.StartsWith("r-", stored.ReplayId);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public void ReplayIdGenerator_MintsCanonicalShape()
    {
        var id = ReplayIdGenerator.Mint();
        Assert.StartsWith("r-", id);
        Assert.Equal(2 + 8, id.Length);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_InsertGetRoundTrip()
    {
        var store = new InMemoryReplayStore();
        var record = MakeRecord("{\"turns\":[1,2,3]}");
        var stored = await store.InsertAsync(record);
        var fetched = await store.GetAsync(stored.ReplayId);
        Assert.NotNull(fetched);
        Assert.Equal(record.GameId, fetched!.GameId);
        Assert.Equal("{\"turns\":[1,2,3]}", fetched.DecompressPayload());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_InsertGetRoundTrip()
    {
        var store = NewEfStore();
        var record = MakeRecord("{\"turns\":[10,20,30]}");
        var stored = await store.InsertAsync(record);
        var fetched = await store.GetAsync(stored.ReplayId);
        Assert.NotNull(fetched);
        Assert.Equal(record.GameId, fetched!.GameId);
        Assert.Equal("{\"turns\":[10,20,30]}", fetched.DecompressPayload());
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public void CompressDecompress_RoundTrip()
    {
        const string payload = "{\"hello\":\"world\",\"items\":[1,2,3]}";
        var compressed = ReplayRecord.CompressPayload(payload);
        Assert.NotEmpty(compressed);
        var decompressed = ReplayRecord.DecompressPayload(compressed);
        Assert.Equal(payload, decompressed);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_InsertPinsExpiresAt()
    {
        var store = new InMemoryReplayStore();
        var completed = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var stored = await store.InsertAsync(MakeRecord(completedAt: completed));
        Assert.Equal(completed.AddDays(90), stored.ExpiresAt);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_SweepExpiredDropsOldRows()
    {
        var store = new InMemoryReplayStore();
        var oldRow = await store.InsertAsync(MakeRecord(completedAt: DateTime.UtcNow.AddDays(-200)));
        var freshRow = await store.InsertAsync(MakeRecord(completedAt: DateTime.UtcNow));
        var dropped = await store.SweepExpiredAsync(DateTime.UtcNow);
        Assert.Equal(1, dropped);
        Assert.Null(await store.GetAsync(oldRow.ReplayId));
        Assert.NotNull(await store.GetAsync(freshRow.ReplayId));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_SweepExpiredDropsOldRows()
    {
        var store = NewEfStore();
        var oldRow = await store.InsertAsync(MakeRecord(completedAt: DateTime.UtcNow.AddDays(-200)));
        var freshRow = await store.InsertAsync(MakeRecord(completedAt: DateTime.UtcNow));
        var dropped = await store.SweepExpiredAsync(DateTime.UtcNow);
        Assert.Equal(1, dropped);
        Assert.Null(await store.GetAsync(oldRow.ReplayId));
        Assert.NotNull(await store.GetAsync(freshRow.ReplayId));
    }
}
