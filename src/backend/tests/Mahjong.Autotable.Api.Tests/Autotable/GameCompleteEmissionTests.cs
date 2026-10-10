using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #116 / #122 (Hudson P0) — the authoritative <c>gameComplete</c> collection emission.
///
/// <para>Before this, authoritative gameplay never emitted a <c>gameComplete</c> collection entry
/// over the autotable WS: <see cref="ChangshaCollectionKinds.GameComplete"/> did not exist and the
/// translator only produced <c>result["current"]</c>. The bundle subscribes to
/// <c>gameComplete["current"].isComplete</c> to show <c>#game-complete-modal</c>, so the end-of-game
/// modal was unreachable through real play.</para>
///
/// <para>These tests pin the fix at two layers:
/// <list type="bullet">
///   <item><b>Translator</b> — a terminal state (<see cref="ChangshaGameState.IsGameComplete"/>)
///   emits exactly one <c>gameComplete["current"]</c> whose wire shape matches the frontend
///   <c>GameCompleteEntry</c> (<c>isComplete</c> / <c>totalScores</c> object keyed by seat /
///   <c>maxHands</c>); an in-progress state emits none; and <c>result.score</c> stays a JSON
///   <b>array</b> (locked C-1) — unchanged by this work.</item>
///   <item><b>Runtime integration</b> — a real all-bot Manual game driven to genuine completion
///   fires <c>StateChanged</c> with the terminal snapshot, and translating that exact snapshot (the
///   same call <c>AutotableWsEndpoint.OnStateChanged</c> makes) yields the <c>gameComplete</c> entry
///   with authoritative <c>totalScores</c> == <see cref="ChangshaGameState.CumulativeScores"/>. No
///   synthetic/test bypass — the signal comes from RotateBanker hitting MaxHands.</item>
/// </list></para>
/// </summary>
public sealed class GameCompleteEmissionTests
{
    // ── Translator unit tests ─────────────────────────────────────────────────────

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public void Translate_EmitsGameComplete_WithAuthoritativePayload_WhenComplete()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 7, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.GameComplete;
        state.IsGameComplete = true;
        state.MaxHands = 4;
        state.CumulativeScores[0] = 12;
        state.CumulativeScores[1] = -4;
        state.CumulativeScores[2] = -3;
        state.CumulativeScores[3] = -5;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        var gc = Assert.Single(entries.Where(e => e.Kind == ChangshaCollectionKinds.GameComplete));
        Assert.Equal("current", gc.Key?.ToString());

        // Assert on the WIRE shape (what the bundle actually parses), not just the CLR object.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(gc.Value, AutotableJson.Options));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("isComplete").GetBoolean());
        Assert.Equal(4, root.GetProperty("maxHands").GetInt32());

        var totals = root.GetProperty("totalScores");
        Assert.Equal(JsonValueKind.Object, totals.ValueKind); // seat->score object (NOT the result.score array)
        Assert.Equal(12, totals.GetProperty("0").GetInt32());
        Assert.Equal(-4, totals.GetProperty("1").GetInt32());
        Assert.Equal(-3, totals.GetProperty("2").GetInt32());
        Assert.Equal(-5, totals.GetProperty("3").GetInt32());
    }

    [Theory, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    [InlineData(ChangshaPhase.AwaitingDiscard)]
    [InlineData(ChangshaPhase.RollingDice)]
    [InlineData(ChangshaPhase.EndHand)]
    public void Translate_OmitsGameComplete_DuringActivePlay(ChangshaPhase phase)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 11, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = phase;
        // Not complete: even at EndHand (a hand ended, but the match hasn't).
        Assert.False(state.IsGameComplete);

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);

        Assert.DoesNotContain(entries, e => e.Kind == ChangshaCollectionKinds.GameComplete);
    }

    [Fact, Trait("Category", "Autotable"), Trait("Contract", "C-1")]
    public void Translate_ResultScore_RemainsJsonArray()
    {
        // Regression guard for the locked C-1 result.score contract — the gameComplete work must
        // not perturb result["current"].score, which the frontend spreads and therefore requires
        // to be a JSON array. EndHand (no win = washout) still emits a result with a score array.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 13, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.EndHand;
        state.CumulativeScores[0] = 5;
        state.CumulativeScores[1] = -2;

        var entries = ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var result = Assert.Single(entries.Where(e => e.Kind == ChangshaCollectionKinds.Result));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, AutotableJson.Options));
        var score = doc.RootElement.GetProperty("score");
        Assert.Equal(JsonValueKind.Array, score.ValueKind);
        // And it is genuinely the {seat,delta} array shape, not an object.
        Assert.All(score.EnumerateArray(), el =>
        {
            Assert.True(el.TryGetProperty("seat", out _));
            Assert.True(el.TryGetProperty("delta", out _));
        });
    }

    // ── Runtime integration — real completion drives the emission ──────────────────

    [Fact, Trait("Category", "Acceptance")]
    public async Task RuntimeGame_ReachesGameComplete_StateChanged_EmitsAuthoritativeGameComplete()
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

        // Capture the gameComplete entry the WS path would emit: translate the exact terminal
        // snapshot pushed by StateChanged (the same call AutotableWsEndpoint.OnStateChanged makes).
        var emitted = new TaskCompletionSource<GameCompleteEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(string gid, ChangshaGameState s)
        {
            // #137 — StateChanged now hands us the snapshot the runtime froze at the
            // mutation instant (identical to what AutotableWsEndpoint.OnStateChanged
            // translates), so we translate THAT rather than re-reading the live state.
            if (!s.IsGameComplete) return;
            var entries = ChangshaToAutotableTranslator.Translate(s, viewerSeat: null);
            if (entries.FirstOrDefault(e => e.Kind == ChangshaCollectionKinds.GameComplete)?.Value is GameCompleteEntry gc)
                emitted.TrySetResult(gc);
        }
        runtime.StateChanged += OnChanged;

        try
        {
            // All-bot Manual table, dealer 0, default MaxHands = 4 — self-drives to completion.
            var gameId = await runtime.CreateGameAsync(
                seed: 24680, botSeatIndexes: new[] { 0, 1, 2, 3 },
                hostPlayerId: null, hostConnectionId: null, cts.Token);
            Assert.True(runtime.TryGetSnapshot(gameId, out var created));
            created!.DealMode = DealMode.Manual;

            await runtime.StartGameAsync(gameId, cts.Token);
            await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token); // hand 1 kick; 2..N auto-drive

            var gc = await emitted.Task.WaitAsync(TimeSpan.FromSeconds(115), cts.Token);

            Assert.True(gc.IsComplete);
            Assert.Equal(4, gc.MaxHands);
            Assert.Equal(4, gc.TotalScores.Count);

            // Authoritative: the emitted totals equal the runtime's cumulative scores at completion.
            Assert.True(runtime.TryGetSnapshot(gameId, out var final) && final is not null);
            Assert.True(final!.IsGameComplete);
            foreach (var kv in final.CumulativeScores)
                Assert.Equal(kv.Value, gc.TotalScores[kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            // Changsha is zero-sum — the final totals must net to zero.
            Assert.Equal(0, gc.TotalScores.Values.Sum());
        }
        finally
        {
            runtime.StateChanged -= OnChanged;
        }
    }

    // ── Inline runtime harness (per-test configurable bot delays; off the DB hot path) ──
    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public IChangshaGameRuntime Runtime { get; }
        private readonly WebApplicationFactory<Program> _factory;
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"gamecomplete-{Guid.NewGuid():N}.db");
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
