using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Bishop;

/// <summary>
/// Phase K Wave 12 — Bishop. Hard-asserted contract for the
/// persisted SignalR sequence-store
/// (<see cref="ISignalRSequenceStore"/>).
///
/// <list type="number">
///   <item><see cref="InMemorySignalRSequenceStore"/>
///         implements <see cref="ISignalRSequenceStore"/>.</item>
///   <item><see cref="EfSignalRSequenceStore"/> implements
///         <see cref="ISignalRSequenceStore"/>.</item>
///   <item><see cref="SignalRSequenceStoreOptions"/> default
///         SequenceStoreImpl = "InMemory".</item>
///   <item><see cref="SignalRSequenceStoreOptions"/> default
///         RetentionMinutes = 60.</item>
///   <item>Append→ReadFromAck round-trips (in-memory).</item>
///   <item>Append→ReadFromAck round-trips (EF).</item>
///   <item>ReadFromAck excludes entries ≤ last-acked
///         sequence (in-memory).</item>
///   <item>ReadFromAck honours the limit cap.</item>
///   <item>Append pins ExpiresAt = CreatedAt + Retention
///         (in-memory).</item>
///   <item>SweepExpiredAsync drops old rows (in-memory).</item>
///   <item>SweepExpiredAsync drops old rows (EF).</item>
///   <item>SignalRSequencePayloadSerializer round-trips a
///         dictionary.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class SignalRSequenceStorePersistenceFacts : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w12-sig-{Guid.NewGuid():N}.db");
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

    private EfSignalRSequenceStore NewEfStore(SignalRSequenceStoreOptions? opts = null) =>
        new(_factory!.Services.GetRequiredService<IServiceScopeFactory>(),
            opts ?? new SignalRSequenceStoreOptions(),
            NullLogger<EfSignalRSequenceStore>.Instance);

    private static SignalRSequenceEntry MakeEntry(
        string hub = "ChangshaHub",
        string conn = "conn-1",
        long seq = 1,
        DateTime createdAt = default) =>
        new()
        {
            HubName = hub,
            ConnectionId = conn,
            GroupName = "game-x",
            Method = "PublishUpdate",
            Sequence = seq,
            CreatedAt = createdAt == default ? DateTime.UtcNow : createdAt,
            PayloadJson = "{}",
        };

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void InMemoryStore_ImplementsInterface()
    {
        Assert.True(typeof(ISignalRSequenceStore).IsAssignableFrom(typeof(InMemorySignalRSequenceStore)));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void EfStore_ImplementsInterface()
    {
        Assert.True(typeof(ISignalRSequenceStore).IsAssignableFrom(typeof(EfSignalRSequenceStore)));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void Options_DefaultSequenceStoreImplIsInMemory()
    {
        var opts = new SignalRSequenceStoreOptions();
        Assert.Equal("InMemory", opts.SequenceStoreImpl);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void Options_DefaultRetentionMinutesIs60()
    {
        var opts = new SignalRSequenceStoreOptions();
        Assert.Equal(60, opts.RetentionMinutes);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_AppendReadRoundTrip()
    {
        var store = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions());
        await store.AppendAsync(MakeEntry(seq: 1));
        await store.AppendAsync(MakeEntry(seq: 2));
        var rows = await store.ReadFromAckAsync("ChangshaHub", "conn-1", 0, 10);
        Assert.Equal(2, rows.Count);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_AppendReadRoundTrip()
    {
        var store = NewEfStore();
        await store.AppendAsync(MakeEntry(seq: 1));
        await store.AppendAsync(MakeEntry(seq: 2));
        var rows = await store.ReadFromAckAsync("ChangshaHub", "conn-1", 0, 10);
        Assert.Equal(2, rows.Count);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_ReadFromAck_ExcludesLowerOrEqualSequence()
    {
        var store = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions());
        await store.AppendAsync(MakeEntry(seq: 1));
        await store.AppendAsync(MakeEntry(seq: 2));
        await store.AppendAsync(MakeEntry(seq: 3));
        var rows = await store.ReadFromAckAsync("ChangshaHub", "conn-1", 1, 10);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2L, rows[0].Sequence);
        Assert.Equal(3L, rows[1].Sequence);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_ReadFromAck_HonoursLimit()
    {
        var store = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions());
        for (var i = 1; i <= 10; i++) await store.AppendAsync(MakeEntry(seq: i));
        var rows = await store.ReadFromAckAsync("ChangshaHub", "conn-1", 0, 3);
        Assert.Equal(3, rows.Count);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_AppendPinsExpiresAt()
    {
        var store = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions { RetentionMinutes = 15 });
        var created = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var stored = await store.AppendAsync(MakeEntry(seq: 1, createdAt: created));
        Assert.Equal(created.AddMinutes(15), stored.ExpiresAt);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_SweepExpiredDropsOldRows()
    {
        var store = new InMemorySignalRSequenceStore(new SignalRSequenceStoreOptions { RetentionMinutes = 5 });
        await store.AppendAsync(MakeEntry(seq: 1, createdAt: DateTime.UtcNow.AddHours(-2)));
        await store.AppendAsync(MakeEntry(seq: 2, createdAt: DateTime.UtcNow));
        var dropped = await store.SweepExpiredAsync(DateTime.UtcNow);
        Assert.Equal(1, dropped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_SweepExpiredDropsOldRows()
    {
        var store = NewEfStore(new SignalRSequenceStoreOptions { RetentionMinutes = 5 });
        await store.AppendAsync(MakeEntry(seq: 1, createdAt: DateTime.UtcNow.AddHours(-2)));
        await store.AppendAsync(MakeEntry(seq: 2, createdAt: DateTime.UtcNow));
        var dropped = await store.SweepExpiredAsync(DateTime.UtcNow);
        Assert.Equal(1, dropped);
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void PayloadSerializer_RoundTripsDictionary()
    {
        var payload = new Dictionary<string, object?>
        {
            ["a"] = "hello",
            ["b"] = 42,
        };
        var json = SignalRSequencePayloadSerializer.Serialize(payload);
        var back = SignalRSequencePayloadSerializer.Deserialize(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, back.ValueKind);
        Assert.True(back.TryGetProperty("a", out _));
        Assert.True(back.TryGetProperty("b", out _));
    }
}
