using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Tests.Hub;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// Phase J Wave 7 — runtime → DB replay-persistence contract test (Vasquez).
///
/// <para>Bishop's Wave-7 task hooks
/// <see cref="ChangshaGameRuntime.PersistReplayAsync"/> into
/// <see cref="ChangshaGameRuntime.EmitGameCompletedAsync"/> so a row lands in
/// <see cref="Mahjong.Autotable.Api.Data.Entities.ChangshaGameReplay"/> as soon
/// as the SignalR <c>GameCompleted</c> event fires. The
/// <see cref="ChangshaReplayEndpointTests"/> suite covers the read-path
/// contract independently with pre-seeded data; this companion exercises
/// the write path through the real hub flow so a regression where the
/// hook is dropped from <c>EmitGameCompletedAsync</c> shows up here.</para>
///
/// <para><b>Strategy.</b> Reuses <see cref="ChangshaHubTestHarness"/> (4-bot
/// 4-hand match, fast-mode runtime options) and the same pattern as
/// <see cref="GameCompletionLifecycleTests.GameCompletedEvent_Fires_OnceOnly"/>.
/// After observing the <c>GameCompleted</c> SignalR event, we open a fresh
/// DB scope and assert exactly one <c>ChangshaGameReplay</c> row exists for
/// the game id, with non-empty <c>EventsJson</c> deserialising to a
/// non-empty events array.</para>
/// </summary>
[Collection("DbSerial")]
public class ChangshaReplayPersistenceTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "ChangshaHubE2E"), Trait("Wave", "Phase-J-7")]
    public async Task GameCompletion_PersistsReplaySnapshot()
    {
        await using var harness = new ChangshaHubTestHarness();
        var conn = await harness.ConnectAsync();

        var completedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("GameCompleted", _ => completedTcs.TrySetResult());

        var create = await conn.InvokeAsync<CreateGameResultLite>(
            "CreateGame", "changsha-v1", new int[] { 0, 1, 2, 3 }, 12345);
        Assert.False(string.IsNullOrEmpty(create.GameId));

        await conn.InvokeAsync("StartGame", create.GameId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await completedTcs.Task.WaitAsync(cts.Token);

        // Allow PersistReplayAsync (fired inside EmitGameCompletedAsync,
        // after the SignalR broadcast) to land on disk. The hook is awaited
        // synchronously within the same async sequence so a short wait is
        // sufficient — bump it generously to absorb CI agent jitter.
        var gameGuid = Guid.Parse(create.GameId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        Mahjong.Autotable.Api.Data.Entities.ChangshaGameReplay? replay = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = harness.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            replay = await db.ChangshaGameReplays
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.GameId == gameGuid);
            if (replay is not null) break;
            await Task.Delay(100);
        }

        Assert.NotNull(replay);
        output.WriteLine($"Replay row written: gameId={replay!.GameId}, " +
                         $"createdAt={replay.CreatedAt:O}, " +
                         $"eventsJson length={replay.EventsJson.Length}.");

        // Sanity check the EventsJson payload — must parse as a v2
        // envelope object ({ schemaVersion: 2, events: [...] }) with a
        // non-empty events array carrying the Wave-7 wire fields.
        // Phase J Wave 9 — writer flipped from bare array (v1) to
        // envelope (v2); the read controller normalises both shapes,
        // so this assertion is intentionally version-aware.
        using var doc = JsonDocument.Parse(replay.EventsJson);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out var schemaProp)
                 && schemaProp.GetInt32() >= 2,
            "Persisted replay must carry schemaVersion >= 2.");
        Assert.True(doc.RootElement.TryGetProperty("events", out var eventsArray)
                 && eventsArray.ValueKind == JsonValueKind.Array,
            "Persisted replay must carry an 'events' array.");
        Assert.True(eventsArray.GetArrayLength() > 0,
            "Persisted replay must contain at least one event after a full game.");

        var first = eventsArray.EnumerateArray().First();
        Assert.True(first.TryGetProperty("turn", out _),
            "Persisted replay event missing 'turn'.");
        Assert.True(first.TryGetProperty("phase", out _),
            "Persisted replay event missing 'phase'.");
        Assert.True(first.TryGetProperty("actor", out _),
            "Persisted replay event missing 'actor'.");
        Assert.True(first.TryGetProperty("action", out _),
            "Persisted replay event missing 'action'.");
        Assert.True(first.TryGetProperty("tilesJson", out _),
            "Persisted replay event missing 'tilesJson'.");
        Assert.True(first.TryGetProperty("timestampUtc", out _),
            "Persisted replay event missing 'timestampUtc'.");
    }

    // Local DTO; GameCompletionLifecycleTests has its own copy in a separate
    // file. Kept private to this test class to avoid an unrelated cross-file
    // coupling.
    private sealed record CreateGameResultLite(string GameId);
}
