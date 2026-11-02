using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Bishop;

/// <summary>
/// Phase K Wave 11 — Bishop. Hard-asserted contract for the
/// pluggable <see cref="ICommentaryStore"/> persistence seam.
///
/// <list type="number">
///   <item><see cref="InMemoryCommentaryStore"/> implements
///         <see cref="ICommentaryStore"/>.</item>
///   <item><see cref="EfCommentaryStore"/> implements
///         <see cref="ICommentaryStore"/>.</item>
///   <item>Append → Read round-trips a single record (in-memory).</item>
///   <item>Append → Read round-trips a single record (EF).</item>
///   <item>AppendRange persists multiple records (in-memory).</item>
///   <item>AppendRange persists multiple records (EF).</item>
///   <item>Read returns records ordered by GeneratedAt ascending
///         (in-memory).</item>
///   <item>Read returns records ordered by GeneratedAt ascending
///         (EF).</item>
///   <item>Read with <c>afterUtc</c> filter excludes earlier
///         records (in-memory).</item>
///   <item>Read with <c>afterUtc</c> filter excludes earlier
///         records (EF).</item>
///   <item>Read with <c>limit</c> caps the page size (in-memory).</item>
///   <item>Read with <c>limit</c> caps the page size (EF).</item>
///   <item>Count returns the total record count (in-memory).</item>
///   <item>Count returns the total record count (EF).</item>
///   <item>SweepExpired drops records older than the cutoff
///         (in-memory).</item>
///   <item>SweepExpired drops records older than the cutoff (EF).</item>
///   <item><see cref="CommentaryStorageOptions"/> default
///         <c>RetentionDays = 30</c>.</item>
///   <item><see cref="CommentaryStorageOptions"/> default
///         <c>StorageImpl = "InMemory"</c>.</item>
/// </list>
/// </summary>
public sealed class CommentaryStorePersistenceFacts : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w11-comm-{Guid.NewGuid():N}.db");
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

    private EfCommentaryStore NewEfStore(int retentionDays = 30) =>
        new(_factory!.Services.GetRequiredService<IServiceScopeFactory>(),
            new CommentaryStorageOptions { RetentionDays = retentionDays },
            NullLogger<EfCommentaryStore>.Instance);

    private static CommentaryRecord MakeRecord(Guid gameId, DateTimeOffset? at = null, string text = "hello") =>
        new(
            GameId: gameId.ToString("N"),
            TurnNumber: 1,
            Phase: CommentaryPhases.Discard,
            Speaker: "narrator",
            Text: text,
            EmotionIntensity: 0.5,
            TileReferences: Array.Empty<TileReference>(),
            GeneratedAt: at ?? DateTimeOffset.UtcNow);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void InMemoryStore_ImplementsInterface()
    {
        Assert.True(typeof(ICommentaryStore).IsAssignableFrom(typeof(InMemoryCommentaryStore)));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void EfStore_ImplementsInterface()
    {
        Assert.True(typeof(ICommentaryStore).IsAssignableFrom(typeof(EfCommentaryStore)));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task InMemory_AppendRead_RoundTrips()
    {
        var store = new InMemoryCommentaryStore();
        var gameId = Guid.NewGuid();
        var rec = MakeRecord(gameId);
        await store.AppendAsync(gameId, rec);
        var read = await store.ReadAsync(gameId, afterUtc: null, limit: 10);
        Assert.Single(read);
        Assert.Equal(rec.Text, read[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task Ef_AppendRead_RoundTrips()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        var rec = MakeRecord(gameId);
        await store.AppendAsync(gameId, rec);
        var read = await store.ReadAsync(gameId, afterUtc: null, limit: 10);
        Assert.Single(read);
        Assert.Equal(rec.Text, read[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task InMemory_AppendRange_PersistsMultiple()
    {
        var store = new InMemoryCommentaryStore();
        var gameId = Guid.NewGuid();
        await store.AppendRangeAsync(gameId, new[]
        {
            MakeRecord(gameId, DateTimeOffset.UtcNow.AddSeconds(-2)),
            MakeRecord(gameId, DateTimeOffset.UtcNow.AddSeconds(-1)),
            MakeRecord(gameId, DateTimeOffset.UtcNow),
        });
        Assert.Equal(3, await store.CountAsync(gameId));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task Ef_AppendRange_PersistsMultiple()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        await store.AppendRangeAsync(gameId, new[]
        {
            MakeRecord(gameId, DateTimeOffset.UtcNow.AddSeconds(-2)),
            MakeRecord(gameId, DateTimeOffset.UtcNow.AddSeconds(-1)),
            MakeRecord(gameId, DateTimeOffset.UtcNow),
        });
        Assert.Equal(3, await store.CountAsync(gameId));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task InMemory_Read_OrdersByGeneratedAtAscending()
    {
        var store = new InMemoryCommentaryStore();
        var gameId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await store.AppendAsync(gameId, MakeRecord(gameId, t0.AddSeconds(2), "third"));
        await store.AppendAsync(gameId, MakeRecord(gameId, t0, "first"));
        await store.AppendAsync(gameId, MakeRecord(gameId, t0.AddSeconds(1), "second"));

        var read = await store.ReadAsync(gameId, afterUtc: null, limit: 10);
        Assert.Equal(new[] { "first", "second", "third" }, read.Select(r => r.Text).ToArray());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task Ef_Read_OrdersByGeneratedAtAscending()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await store.AppendAsync(gameId, MakeRecord(gameId, t0.AddSeconds(2), "third"));
        await store.AppendAsync(gameId, MakeRecord(gameId, t0, "first"));
        await store.AppendAsync(gameId, MakeRecord(gameId, t0.AddSeconds(1), "second"));

        var read = await store.ReadAsync(gameId, afterUtc: null, limit: 10);
        Assert.Equal(new[] { "first", "second", "third" }, read.Select(r => r.Text).ToArray());
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task InMemory_ReadAfterUtc_FiltersEarlier()
    {
        var store = new InMemoryCommentaryStore();
        var gameId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await store.AppendAsync(gameId, MakeRecord(gameId, t0, "before"));
        await store.AppendAsync(gameId, MakeRecord(gameId, t0.AddSeconds(2), "after"));

        var read = await store.ReadAsync(gameId, afterUtc: t0.AddSeconds(1), limit: 10);
        Assert.Single(read);
        Assert.Equal("after", read[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task Ef_ReadAfterUtc_FiltersEarlier()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await store.AppendAsync(gameId, MakeRecord(gameId, t0, "before"));
        await store.AppendAsync(gameId, MakeRecord(gameId, t0.AddSeconds(2), "after"));

        var read = await store.ReadAsync(gameId, afterUtc: t0.AddSeconds(1), limit: 10);
        Assert.Single(read);
        Assert.Equal("after", read[0].Text);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task InMemory_ReadLimit_CapsPage()
    {
        var store = new InMemoryCommentaryStore();
        var gameId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(gameId, MakeRecord(gameId, DateTimeOffset.UtcNow.AddSeconds(i)));
        }
        var read = await store.ReadAsync(gameId, afterUtc: null, limit: 2);
        Assert.Equal(2, read.Count);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task Ef_ReadLimit_CapsPage()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(gameId, MakeRecord(gameId, DateTimeOffset.UtcNow.AddSeconds(i)));
        }
        var read = await store.ReadAsync(gameId, afterUtc: null, limit: 2);
        Assert.Equal(2, read.Count);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task InMemory_Count_ReturnsTotal()
    {
        var store = new InMemoryCommentaryStore();
        var gameId = Guid.NewGuid();
        await store.AppendAsync(gameId, MakeRecord(gameId));
        await store.AppendAsync(gameId, MakeRecord(gameId));
        await store.AppendAsync(gameId, MakeRecord(gameId));
        Assert.Equal(3, await store.CountAsync(gameId));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task Ef_Count_ReturnsTotal()
    {
        var store = NewEfStore();
        var gameId = Guid.NewGuid();
        await store.AppendAsync(gameId, MakeRecord(gameId));
        await store.AppendAsync(gameId, MakeRecord(gameId));
        await store.AppendAsync(gameId, MakeRecord(gameId));
        Assert.Equal(3, await store.CountAsync(gameId));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task InMemory_SweepExpired_DropsOldRecords()
    {
        var store = new InMemoryCommentaryStore();
        var gameId = Guid.NewGuid();
        var oldRec = MakeRecord(gameId, DateTimeOffset.UtcNow.AddDays(-31));
        var freshRec = MakeRecord(gameId, DateTimeOffset.UtcNow);
        await store.AppendAsync(gameId, oldRec);
        await store.AppendAsync(gameId, freshRec);

        var removed = await store.SweepExpiredAsync(DateTime.UtcNow.AddDays(-7));
        Assert.Equal(1, removed);
        Assert.Equal(1, await store.CountAsync(gameId));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public async Task Ef_SweepExpired_DropsOldRecords()
    {
        // EF impl uses ExpiresAtUtc (GeneratedAt + retentionDays).
        // Insert with a 1-day retention so we can sweep aggressively.
        var store = NewEfStore(retentionDays: 1);
        var gameId = Guid.NewGuid();
        var oldRec = MakeRecord(gameId, DateTimeOffset.UtcNow.AddDays(-2));
        var freshRec = MakeRecord(gameId, DateTimeOffset.UtcNow);
        await store.AppendAsync(gameId, oldRec);
        await store.AppendAsync(gameId, freshRec);

        // Sweep cutoff: now. The old record has ExpiresAtUtc =
        // (now-2d)+1d = now-1d < now, so it qualifies.
        var removed = await store.SweepExpiredAsync(DateTime.UtcNow);
        Assert.Equal(1, removed);
        Assert.Equal(1, await store.CountAsync(gameId));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryStorageOptions_DefaultRetentionDays_Is7()
    {
        Assert.Equal(7, CommentaryStorageOptions.DefaultRetentionDays);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryStorageOptions_DefaultStorageImpl_IsInMemory()
    {
        var opts = new CommentaryStorageOptions();
        Assert.Equal("InMemory", opts.StorageImpl);
    }
}
