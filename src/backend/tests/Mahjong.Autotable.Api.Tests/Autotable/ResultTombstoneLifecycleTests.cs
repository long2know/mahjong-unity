using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #132 (P0) — the authoritative <c>result['current']</c> tombstone between hands.
///
/// <para><b>Defect.</b> <see cref="ChangshaToAutotableTranslator.Translate"/> emitted
/// <c>result['current']</c> only while <see cref="ChangshaPhase.EndHand"/> and never a tombstone
/// (<see cref="ChangshaCollectionEncoder.EncodeHandResultCleared"/> was dead code). <c>result</c> is
/// a NON-ephemeral collection (<c>client.ts</c>: <c>new Collection('result', this)</c>), so
/// <see cref="AutotableGameState"/> stores it and the WS re-ships it on every <c>StateChanged</c>
/// full-snapshot. The bundle's <c>result</c> update handler re-fires and re-opens
/// <c>#result-modal</c> on every hand-2 broadcast — multi-hand play was blocked after hand 1.</para>
///
/// <para><b>Fix under test (two layers):</b>
/// <list type="bullet">
///   <item><b>Translator</b> — emits <c>result['current']={…}</c> at <see cref="ChangshaPhase.EndHand"/>
///   and an explicit <c>result['current']=null</c> tombstone otherwise, so the stored entry is cleared
///   and the client hides the modal exactly when the hand advances.</item>
///   <item><b>WS snapshot assembly</b> — <see cref="AutotableConnectionManager.MergeRuntimeEphemerals"/>
///   forwards the explicit <c>result</c> tombstone to clients. <see cref="AutotableGameState.ApplyUpdate"/>
///   removes the stored entry on the null, but the full-snapshot broadcast otherwise drops the
///   "just-removed" signal, and the bundle hides <c>#result-modal</c> ONLY on an explicit
///   <c>result['current']=null</c> (an omitted/empty result slice does not fire its hide).</item>
/// </list></para>
///
/// <para><c>gameComplete</c> and the <c>result.score</c> array (locked C-1) are preserved.</para>
/// </summary>
public sealed class ResultTombstoneLifecycleTests
{
    // ── Translator unit tests ─────────────────────────────────────────────────────

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public void Translate_AtEndHand_EmitsResultCurrent_NonNull()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 5, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.EndHand;
        state.CumulativeScores[0] = 2;
        state.CumulativeScores[1] = -1;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var result = Assert.Single(entries.Where(e => e.Kind == ChangshaCollectionKinds.Result));
        Assert.Equal("current", result.Key?.ToString());
        Assert.NotNull(result.Value); // populated hand result — the modal must show.
    }

    [Theory, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    [InlineData(ChangshaPhase.RollingDice)]
    [InlineData(ChangshaPhase.Dealing)]
    [InlineData(ChangshaPhase.PickupRound1)]
    [InlineData(ChangshaPhase.DealerExtra)]
    [InlineData(ChangshaPhase.AwaitingDiscard)]
    [InlineData(ChangshaPhase.AwaitingClaim)]
    [InlineData(ChangshaPhase.RotatingBanker)]
    public void Translate_WhenNotEndHand_EmitsResultCurrent_NullTombstone(ChangshaPhase phase)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 5, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = phase;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        // Exactly one result entry, and it is the explicit null tombstone. Without this the
        // stale hand-1 result stays stored and re-opens #result-modal on every hand-2 broadcast.
        var result = Assert.Single(entries.Where(e => e.Kind == ChangshaCollectionKinds.Result));
        Assert.Equal("current", result.Key?.ToString());
        Assert.Null(result.Value);
    }

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public void Translate_AtGameComplete_TombstonesResult_ButStillEmitsGameComplete()
    {
        // Terminal handoff: the per-hand result modal hides (tombstone) while the end-of-match
        // #game-complete-modal renders. GameComplete is not EndHand, so result is cleared.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 5, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.GameComplete;
        state.IsGameComplete = true;
        state.MaxHands = 4;
        state.CumulativeScores[0] = 6;
        state.CumulativeScores[1] = -2;
        state.CumulativeScores[2] = -1;
        state.CumulativeScores[3] = -3;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var result = Assert.Single(entries.Where(e => e.Kind == ChangshaCollectionKinds.Result));
        Assert.Null(result.Value); // hand modal hidden ...

        var gc = Assert.Single(entries.Where(e => e.Kind == ChangshaCollectionKinds.GameComplete));
        Assert.NotNull(gc.Value); // ... but the end-of-match modal still renders.
    }

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public void Translate_ResultScore_RemainsJsonArray_AtEndHand()
    {
        // Locked C-1 regression guard — the tombstone work must not perturb the EndHand
        // result.score wire shape (the frontend spreads it and requires a JSON array).
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 13, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.EndHand;
        state.CumulativeScores[0] = 5;
        state.CumulativeScores[1] = -2;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var result = Assert.Single(entries.Where(e => e.Kind == ChangshaCollectionKinds.Result));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, AutotableJson.Options));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("score").ValueKind);
    }

    // ── WS snapshot-assembly unit test — the explicit tombstone reaches clients ─────

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public void SnapshotAssembly_ForwardsExplicitResultTombstone_WhenHandAdvancesPastEndHand()
    {
        // Faithfully reproduce AutotableWsEndpoint.SendFullSnapshotAsync's runtime path across a
        // hand boundary, and prove the client-facing snapshot carries the explicit result tombstone.
        var gameState = new AutotableGameState("g-132");

        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 99, botSeatIndexes: new[] { 0, 1, 2, 3 });

        // Hand 1 EndHand — translator emits result['current']={…}; ApplyUpdate stores it.
        state.Phase = ChangshaPhase.EndHand;
        state.CumulativeScores[0] = 3;
        state.CumulativeScores[1] = -1;
        var endHandEntries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        gameState.ApplyUpdate(endHandEntries, UpdateSource.Runtime);
        Assert.Contains(gameState.Snapshot(),
            e => e.Kind == ChangshaCollectionKinds.Result && e.Value is not null);

        // Hand 2 begins (leaves EndHand) — translator emits result['current']=null.
        state.Phase = ChangshaPhase.AwaitingDiscard;
        var nextHandEntries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        gameState.ApplyUpdate(nextHandEntries, UpdateSource.Runtime); // removes the stored entry
        var stored = gameState.Snapshot();
        Assert.DoesNotContain(stored, e => e.Kind == ChangshaCollectionKinds.Result);

        // The snapshot the WS ships must still carry the EXPLICIT [result, current, null] so the
        // bundle fires its hide (an omitted result slice does not).
        var snapshot = AutotableConnectionManager.MergeRuntimeEphemerals(stored, nextHandEntries, gameState);
        var resultEntry = Assert.Single(snapshot.Where(e => e.Kind == ChangshaCollectionKinds.Result));
        Assert.Equal("current", resultEntry.Key?.ToString());
        Assert.Null(resultEntry.Value);
    }

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public void SnapshotAssembly_AtEndHand_ShipsSingleNonNullResult_NoDuplicate()
    {
        // Guard the forwarding change against double-emitting the populated result at EndHand
        // (stored copy + a translator re-attach). Exactly one non-null result must ship.
        var gameState = new AutotableGameState("g-132-eh");
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 77, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.EndHand;
        state.CumulativeScores[0] = 4;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        gameState.ApplyUpdate(entries, UpdateSource.Runtime);
        var snapshot = AutotableConnectionManager.MergeRuntimeEphemerals(gameState.Snapshot(), entries, gameState);

        var resultEntry = Assert.Single(snapshot.Where(e => e.Kind == ChangshaCollectionKinds.Result));
        Assert.NotNull(resultEntry.Value);
    }

    // ── Runtime lifecycle — real all-bot game drives the tombstone ─────────────────

    [Fact, Trait("Category", "Acceptance")]
    public async Task RuntimeGame_MultiHand_TombstonesResultCurrent_WhenLeavingEndHand()
    {
        await using var harness = new RuntimeHarness(o =>
        {
            o.BotPickupDelayMs = 5;
            o.BotTurnDelayMs = 1;
            o.BotClaimDelayMs = 1;
            o.ClaimWindowTimeoutMs = 20;
            o.DealBatchDelayMs = 0;
            o.PersistSnapshots = false;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Translate the exact snapshot each StateChanged pushes (the same call OnStateChanged makes)
        // and prove the result lifecycle: a hand reaches EndHand with a non-null result, and once the
        // game leaves that EndHand the translated snapshot carries result['current']=null (tombstone).
        var sawResultAtEndHand = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(string gid, ChangshaGameState s)
        {
            // #137 — translate the snapshot StateChanged froze at the mutation instant
            // (was: re-read the live state, which could already have advanced past the
            // transient EndHand — the very race this fix closes).
            var entries = ChangshaToAutotableTranslator.Translate(s, viewerSeat: null);
            var result = entries.FirstOrDefault(e => e.Kind == ChangshaCollectionKinds.Result);

            if (s.Phase == ChangshaPhase.EndHand)
            {
                if (result is not null && result.Value is not null) sawResultAtEndHand = true;
            }
            else if (sawResultAtEndHand)
            {
                // Left EndHand after a scored hand — the result MUST now be an explicit tombstone.
                if (result is not null && result.Value is null) done.TrySetResult();
            }
        }
        runtime.StateChanged += OnChanged;

        try
        {
            var gameId = await runtime.CreateGameAsync(
                seed: 314159, botSeatIndexes: new[] { 0, 1, 2, 3 },
                hostPlayerId: null, hostConnectionId: null, cts.Token);
            Assert.True(runtime.TryGetSnapshot(gameId, out var created));
            created!.DealMode = DealMode.Manual;

            await runtime.StartGameAsync(gameId, cts.Token);
            await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token); // hand 1 kick; 2..N auto-drive

            await done.Task.WaitAsync(TimeSpan.FromSeconds(115), cts.Token);
        }
        finally
        {
            runtime.StateChanged -= OnChanged;
        }

        Assert.True(sawResultAtEndHand,
            "no EndHand snapshot ever carried a non-null result['current'] (a hand never scored/washed).");
        Assert.True(done.Task.IsCompletedSuccessfully,
            "after a hand's EndHand, no subsequent non-EndHand snapshot carried result['current']=null — " +
            "the stale result would re-open #result-modal on every next-hand broadcast (#132).");
    }

    // ── Inline runtime harness (mirrors GameCompleteEmissionTests; off the DB hot path) ──
    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public IChangshaGameRuntime Runtime { get; }
        private readonly WebApplicationFactory<Program> _factory;
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"result-tombstone-{Guid.NewGuid():N}.db");
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(configureOptions));
            });
            _ = _factory.Server;
            Runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        }

        public ValueTask DisposeAsync()
        {
            _factory.Dispose();
            try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
