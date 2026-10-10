using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// #142 CI-reliability regression — deterministic manual-deal pickup drive.
///
/// <para>Frost proved a recurring WS manual-deal acceptance race across
/// <see cref="RoundRobinDiscardCycleTests"/>,
/// <see cref="DealerDiscardBroadcastAuditA2Tests"/>, and
/// <see cref="DealerExtraTransitionsToAwaitingDiscardTests"/>: under a loaded
/// full-suite / SqlServer CI run the thread pool is saturated, so the runtime's
/// bot-pickup scheduler — which <c>await Task.Delay(BotPickupDelayMs)</c> then
/// resumes on the pool — STARVES. The manual deal then wedges at
/// <see cref="ChangshaPhase.PickupRound1"/> with 0 discards; the affected tests'
/// bounded waits time out. Isolated runs have a free pool, so they pass — a
/// classic Heisenbug that only bites in the loaded suite.</para>
///
/// <para>The root fix (in <see cref="ManualDealPickupDriver"/>) removes the timing
/// assumption: acceptance tests now drive the bot pickups explicitly through the
/// SAME public production API the scheduler itself calls
/// (<see cref="IChangshaGameRuntime.TakeTilesFromWallAsync"/>), keyed on observable
/// progress, so the ceremony no longer depends on the pool servicing the bot
/// <c>Task.Delay</c> continuations in time.</para>
///
/// <para>This class is the RED→GREEN guard for that fix. It sets
/// <see cref="ChangshaRuntimeOptions.BotPickupDelayMs"/> to ten minutes, which
/// DETERMINISTICALLY reproduces the starved condition (the auto-scheduler cannot
/// fire within the test window). The single test first PINS that the auto-scheduler
/// alone leaves the deal wedged (RED: cursor parked on a bot, still PickupRound1),
/// then proves the deterministic driver completes the ceremony to
/// <see cref="ChangshaPhase.AwaitingDiscard"/> anyway (GREEN). Reverting the fix —
/// i.e. waiting on the auto-scheduler — would time out here.</para>
/// </summary>
public sealed class ManualDealPickupStarvationRegressionTests : IAsyncLifetime
{
    // Ten minutes — far beyond any test window. With this delay the runtime's
    // bot-pickup scheduler is guaranteed NOT to advance the deal on its own, which
    // is exactly the loaded-CI starvation Frost captured (the Task.Delay continuation
    // never runs in time). The deal must therefore be completed by the deterministic
    // driver, not the scheduler.
    private const int StarvedBotPickupDelayMs = 600_000;

    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"pickup-starvation-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.BotPickupDelayMs = StarvedBotPickupDelayMs; // starved scheduler
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
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

    [Fact, Trait("Category", "Acceptance"), Trait("Reliability", "ManualDealPickup")]
    public async Task ManualDeal_WithStarvedBotPickupScheduler_DriverStillCompletesToAwaitingDiscard()
    {
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        // Generous ceiling only so a genuine hang fails fast; the deterministic path
        // completes in well under a second. It is NOT tuned to mask timing — the
        // starved scheduler could never finish within it (its next tick is 10 min out).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Manual game, dealer = seat 0 (human), seats 1/2/3 bots.
        var gameId = await runtime.CreateGameAsync(seed: 91142, botSeatIndexes: new[] { 1, 2, 3 },
            hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var pre));
        pre!.DealerSeatIndex = 0;
        foreach (var seat in pre.Seats) seat.IsDealer = seat.SeatIndex == 0;

        Assert.True(await runtime.ApplyDealModeAsync(gameId, DealMode.Manual, cts.Token));
        await runtime.StartGameAsync(gameId, cts.Token);
        await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token);

        // Dealer (seat 0) takes round 1. The cursor advances to seat 1 (a bot) and the
        // runtime schedules that bot's pickup — but on a 10-minute delay.
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 4, cts.Token);

        // ── RED: the starved auto-scheduler alone does NOT advance the deal ─────────
        // A brief settle proves the point: without the deterministic driver, the
        // ceremony is wedged here — cursor parked on a bot seat, still PickupRound1,
        // 0 discards. This is precisely Frost's loaded-CI failure signature.
        await Task.Delay(250, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var stalled) && stalled is not null);
        Assert.Equal(ChangshaPhase.PickupRound1, stalled!.Phase);
        Assert.NotNull(stalled.PickupSeatIndex);
        Assert.NotEqual(0, stalled.PickupSeatIndex!.Value);
        Assert.True(stalled.Seats[stalled.PickupSeatIndex.Value].IsBot,
            "expected the pickup cursor to be parked on a starved bot seat");

        // ── GREEN: the deterministic driver completes the ceremony regardless ───────
        // Drive each round's bot pickups via the production pickup API, interleaved
        // with the human dealer's own takes (4/4/4/1/1), until AwaitingDiscard.
        await ManualDealPickupDriver.DriveBotPickupsToPhaseAsync(
            runtime, gameId, ChangshaPhase.PickupRound2, ct: cts.Token);

        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 4, cts.Token);
        await ManualDealPickupDriver.DriveBotPickupsToPhaseAsync(
            runtime, gameId, ChangshaPhase.PickupRound3, ct: cts.Token);

        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 4, cts.Token);
        await ManualDealPickupDriver.DriveBotPickupsToPhaseAsync(
            runtime, gameId, ChangshaPhase.SingleTilePickup, ct: cts.Token);

        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 1, cts.Token);
        await ManualDealPickupDriver.DriveBotPickupsToPhaseAsync(
            runtime, gameId, ChangshaPhase.DealerExtra, ct: cts.Token);

        // Dealer takes the +1 tile → the runtime transitions to AwaitingDiscard.
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 1, cts.Token);

        // The deal is complete and correct even though the bot scheduler never fired:
        // AwaitingDiscard, dealer at 14 tiles, pickup cursor cleared, every bot dealt in.
        Assert.True(runtime.TryGetSnapshot(gameId, out var post) && post is not null);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, post!.Phase);
        Assert.Equal(0, post.ActiveSeatIndex);
        Assert.Null(post.PickupSeatIndex);
        Assert.Equal(14, post.Hands.Single(h => h.SeatIndex == 0).ConcealedTiles.Count);
        foreach (var seat in post.Seats.Where(s => s.IsBot))
        {
            Assert.Equal(13, post.Hands.Single(h => h.SeatIndex == seat.SeatIndex).ConcealedTiles.Count);
        }
    }
}
