using System.Collections.Concurrent;
using System.Linq;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance (#116 · WP-A) — the manual deal ceremony must re-run for <b>every</b> hand,
/// not just hand 1, and a Manual + bots game must reach <see cref="ChangshaPhase.GameComplete"/>.
///
/// <para><b>Defect (P1-3).</b> <see cref="ChangshaGameRuntime"/>'s <c>StartNextHandOrEndAsync</c>
/// used to unconditionally auto-deal hands 2..N (roll dice + <c>Deal()</c> atomically) regardless
/// of <see cref="DealMode"/>. Because the WS default is <see cref="DealMode.Manual"/>, a
/// human-vs-bots game got the physical wall-build / dice-break / batch-of-4 pickup ritual exactly
/// once and then silently auto-dealt every later hand — a fidelity gap vs the canonical Changsha
/// spec. The locked decision is to re-run the full 6-state ceremony
/// (BreakPointMarked → PickupRound1..3 → SingleTilePickup → DealerExtra) every hand under Manual,
/// while <see cref="DealMode.Auto"/> keeps dealing atomically; both converge to identical
/// post-deal state.</para>
///
/// <para><b>Defect (P1-4).</b> Full completion under the WS-default transport (Manual) had no
/// end-to-end guardrail — only <see cref="DealMode.Auto"/> was proven to reach GameComplete. This
/// test closes that gap: an all-bot Manual game plays all four hands to completion with the
/// runtime auto-driving each new hand's bot-dealer dice roll + pickup chain.</para>
///
/// <para><b>How re-entry is asserted.</b> A lightweight background monitor samples the live
/// snapshot and records, per <see cref="ChangshaGameState.HandNumber"/>, whether a manual pickup
/// phase (<see cref="ChangshaGameStateMachine.IsPickupPhase"/>) and the pre-roll
/// <see cref="ChangshaPhase.RollingDice"/> phase were observed. The pre-fix runtime would never
/// enter a pickup phase for hands 2..N (it auto-dealt), so requiring a pickup phase for every hand
/// 1..MaxHands is the precise discriminator between the bug and the fix. Each ceremony phase
/// persists at least <see cref="ChangshaRuntimeOptions.BotPickupDelayMs"/> (the bot waits before
/// picking / rolling), which is far longer than the sampler's cadence, so observation is reliable.</para>
/// </summary>
[Collection("ManualDealPerHandCeremony")]
public sealed class ManualDealPerHandCeremonyTests
{
    // ── Inline factory harness (per-test, so the bot delays are configurable) ─────
    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"manual-perhand-{Guid.NewGuid():N}.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s =>
                {
                    s.Configure<ChangshaRuntimeOptions>(configureOptions);
                });
            });
            _ = Factory.Server;
            Runtime = Factory.Services.GetRequiredService<IChangshaGameRuntime>();
        }

        public ValueTask DisposeAsync()
        {
            Factory.Dispose();
            try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private static ChangshaGameState Snapshot(IChangshaGameRuntime runtime, string gameId)
    {
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        return state!;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"#116 manual per-hand ceremony contract violated: {description}.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task Manual_FourBots_PlaysAllFourHands_ToGameComplete_WithCeremonyEveryHand()
    {
        await using var harness = new RuntimeHarness(o =>
        {
            o.BotPickupDelayMs = 20; // each pickup / pre-roll phase persists >= this; sampled below
            o.BotTurnDelayMs = 1;
            o.BotClaimDelayMs = 1;
            o.ClaimWindowTimeoutMs = 20;
            o.DealBatchDelayMs = 0;
            o.PersistSnapshots = false; // keep the test off the DB hot path
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // All-bot Manual table, dealer seat 0, default MaxHands = 4.
        var gameId = await runtime.CreateGameAsync(
            seed: 7316, botSeatIndexes: new[] { 0, 1, 2, 3 },
            hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var created));
        created!.DealMode = DealMode.Manual;
        Assert.Equal(DealMode.Manual, created.DealMode);
        Assert.Equal(4, created.MaxHands);

        // Background monitor: record, per hand, that a pickup phase and RollingDice were seen.
        var pickupHands = new ConcurrentDictionary<int, byte>();
        var rollingHands = new ConcurrentDictionary<int, byte>();
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var monitor = Task.Run(async () =>
        {
            while (!monitorCts.IsCancellationRequested)
            {
                if (runtime.TryGetSnapshot(gameId, out var s) && s is not null)
                {
                    var phase = s.Phase;
                    var hand = s.HandNumber;
                    if (ChangshaGameStateMachine.IsPickupPhase(phase)) pickupHands.TryAdd(hand, 0);
                    else if (phase == ChangshaPhase.RollingDice) rollingHands.TryAdd(hand, 0);
                    if (s.IsGameComplete) break;
                }
                try { await Task.Delay(3, monitorCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, monitorCts.Token);

        await runtime.StartGameAsync(gameId, cts.Token);
        // Manual mode parks at RollingDice awaiting the dealer's roll (no auto-deal).
        Assert.Equal(ChangshaPhase.RollingDice, Snapshot(runtime, gameId).Phase);

        // Hand 1 is client-driven: in production the human dealer rolls; here we trigger the
        // bot dealer's first roll explicitly, exactly as the sibling BotPickupScheduler tests do.
        // Hands 2..N must then re-enter the full ceremony *automatically* via the runtime.
        await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).IsGameComplete,
            TimeSpan.FromSeconds(115),
            "Manual + 4-bot game never reached GameComplete");

        monitorCts.Cancel();
        try { await monitor; } catch (OperationCanceledException) { }

        var final = Snapshot(runtime, gameId);

        // P1-4 — the Manual transport reaches GameComplete after exactly MaxHands hands.
        Assert.True(final.IsGameComplete, "IsGameComplete must read true after MaxHands is exhausted.");
        Assert.Equal(ChangshaPhase.GameComplete, final.Phase);
        // RotateBanker increments HandNumber past the cap on the terminal rotation.
        Assert.Equal(final.MaxHands + 1, final.HandNumber);

        // P1-3 — the manual deal ceremony (pickup phases) re-entered for EVERY hand 1..MaxHands,
        // not just hand 1. Pre-fix, the runtime auto-dealt hands 2..N and never entered a pickup
        // phase for them, so this loop is the precise regression guard.
        for (var hand = 1; hand <= final.MaxHands; hand++)
        {
            Assert.True(pickupHands.ContainsKey(hand),
                $"Manual deal ceremony did not re-enter for hand {hand}: no pickup phase was observed. " +
                $"Observed pickup hands: [{string.Join(",", pickupHands.Keys.OrderBy(k => k))}]. " +
                $"Pre-#116 the runtime auto-dealt hands 2..N, skipping the physical-wall pickup ceremony.");
        }

        // Hands 2..N must re-park in RollingDice on re-entry before the bot dealer auto-rolls
        // (hand 1's RollingDice is entered via StartGame). This proves the runtime hands control
        // back to the ceremony rather than dealing atomically.
        for (var hand = 2; hand <= final.MaxHands; hand++)
        {
            Assert.True(rollingHands.ContainsKey(hand),
                $"Hand {hand} did not re-park in RollingDice on manual re-entry. " +
                $"Observed RollingDice hands: [{string.Join(",", rollingHands.Keys.OrderBy(k => k))}].");
        }
    }
}
