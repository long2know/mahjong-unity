using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha.Acceptance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TH = Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #137 / #139 (P0) regression — a BOT Chow claimed with NO explicit tileIds must resolve through
/// the runtime's lowest-rank chow fallback AND the game must keep advancing (the chowing bot then
/// takes its turn and discards). Every bot claim is emitted with <c>tileIds=null</c>
/// (<see cref="ChangshaGameRuntime.BotClaimAsync"/> line "PendingClaims[seat] = new
/// ClaimResponse(decided, null)"), so a bot Chow always drives
/// <see cref="ChangshaGameStateMachine"/>'s <c>RemoveChowTilesByLowestPattern</c> fallback.
///
/// <para>The integrated-main #137 stall was <c>handEnds=0</c>: play wedged inside a claim window
/// and the runtime stopped scheduling the next turn. The coordinator's release-blocking hypothesis
/// was specifically a bot Chow-without-tileIds hitting the lowest-rank fallback and never
/// advancing. This test reproduces exactly that path deterministically through the REAL runtime
/// bot-claim scheduling — no direct WS UPDATE and no mutation of the claim itself — and proves the
/// window resolves into a valid chow meld and the chower then discards, for every one of the three
/// fallback chow patterns (the claimed discard sitting at the low / middle / high end of the run).
/// If the fallback ever throws (adjudicator/fallback pattern divergence) or the fire-and-forget bot
/// claim silently swallows a fault, the window would stay open and this test fails with the exact
/// wedged phase — the deterministic analogue of the gate's <c>handEnds=0</c> stall.</para>
/// </summary>
public sealed class BotChowAdvancesTests
{
    private const int HumanDiscarderSeat = 0;
    private const int ChowBotSeat = 1; // (discarder + 1) % 4 — the only chow-eligible seat.

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"bot-chow-{Guid.NewGuid():N}.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(configureOptions));
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

    // Suit.Tong discard at (discardRank) chowed by the next seat holding (buddyA, buddyB):
    //   low    : discard 3, buddies 4+5  -> lowest-rank fallback pattern (d+1, d+2)
    //   middle : discard 5, buddies 4+6  -> lowest-rank fallback pattern (d-1, d+1)
    //   high   : discard 7, buddies 5+6  -> lowest-rank fallback pattern (d-2, d-1)
    // Each pattern is exercised under BOTH deal modes: the post-claim bot-scheduling path
    // (ScheduleBotIfNeededAsync's AwaitingDiscard branch) is deal-mode-independent, and the gate
    // runs Manual, so proving both closes any "your test used Auto" gap.
    [Theory, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    [InlineData(3, 4, 5, false)]
    [InlineData(5, 4, 6, false)]
    [InlineData(7, 5, 6, false)]
    [InlineData(3, 4, 5, true)]
    [InlineData(5, 4, 6, true)]
    [InlineData(7, 5, 6, true)]
    public async Task BotChow_NoTileIds_ResolvesViaLowestRankFallback_AndPlayAdvances(
        int discardRank, int buddyA, int buddyB, bool manualDeal)
    {
        await using var harness = new RuntimeHarness(o =>
        {
            // A generous claim delay keeps the window observably open after the discard opens it,
            // while the timeout stays well above it so the bot claim (not an auto-pass) resolves it.
            o.BotTurnDelayMs = 5;
            o.BotClaimDelayMs = 120;
            o.BotPickupDelayMs = 5;
            o.ClaimWindowTimeoutMs = 5000;
            o.DealBatchDelayMs = 0;
            o.PersistSnapshots = false;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // seat 0 = human discarder; seats 1..3 = Medium bots. Medium claims ANY chow while it holds
        // fewer than 3 melds, so seat 1 (the discarder's next seat and only chow-eligible seat) will
        // deterministically claim — sending tileIds=null, exactly like every bot claim.
        var gameId = await runtime.CreateGameAsync(
            seed: 4100, botSeatIndexes: new[] { 1, 2, 3 },
            hostPlayerId: "human-0", hostConnectionId: null, cts.Token, maxHands: 4);
        await runtime.SetBotStrategyAsync(gameId, "medium", cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var created) && created is not null);
        created!.DealMode = DealMode.Auto;
        Assert.False(created.Seats[HumanDiscarderSeat].IsBot, "seat 0 must be the human discarder.");

        await runtime.StartGameAsync(gameId, cts.Token);
        var dealt = await WaitForAsync(() =>
            runtime.TryGetSnapshot(gameId, out var s) && s is not null
            && s.Phase == ChangshaPhase.AwaitingDiscard
            && s.Hands.Sum(h => h.ConcealedTiles.Count) == 14 + 13 + 13 + 13,
            TimeSpan.FromSeconds(5));
        Assert.True(dealt, "auto-deal did not reach a quiescent AwaitingDiscard.");

        // Pin the four hands so exactly ONE adjudicated opportunity exists: seat 1 chows the bait.
        // Seat 0 is a human parked at AwaitingDiscard (no bot task running) — a quiescent live edit.
        Assert.True(runtime.TryGetSnapshot(gameId, out var state) && state is not null);
        var bait = TH.Tid(Suit.Tong, discardRank, copy: 2);
        AcceptanceFixture.OverrideHand(state!, HumanDiscarderSeat, DiscarderHand(bait));
        AcceptanceFixture.OverrideHand(state!, ChowBotSeat, ChowClaimerHand(buddyA, buddyB));
        AcceptanceFixture.OverrideHand(state!, 2, InnocuousHand(copy: 1));
        AcceptanceFixture.OverrideHand(state!, 3, InnocuousHand(copy: 2));
        state!.ClaimWindow = null;
        state.MissedWinSeats.Clear();
        state.ActiveSeatIndex = HumanDiscarderSeat;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        // Exercise the claim resolution + post-claim scheduling under the gate's deal mode too.
        state.DealMode = manualDeal ? DealMode.Manual : DealMode.Auto;

        // Seat 0 (human) discards through the runtime's OWN discard path (no direct WS UPDATE, no
        // claim mutation) — this opens a REAL adjudicated window and auto-schedules the bot claim.
        await runtime.DiscardAsync(gameId, HumanDiscarderSeat, bait, cts.Token);

        var opened = await runtime.TryGetSnapshotCopyAsync(gameId, cts.Token);
        Assert.NotNull(opened);
        Assert.Equal(ChangshaPhase.AwaitingClaim, opened!.Phase);
        Assert.NotNull(opened.ClaimWindow);
        // Exactly one opportunity, and it is seat 1's Chow (a deterministic single-claimer window).
        var opp = Assert.Single(opened.ClaimWindow!.Opportunities);
        Assert.Equal(ChowBotSeat, opp.SeatIndex);
        Assert.Equal(TableClaimType.Chow, opp.ClaimType);

        // ── The regression: the bot Chow (tileIds=null) must RESOLVE via the lowest-rank fallback
        //    AND play must ADVANCE — the chowing bot then discards. A fire-and-forget resolution
        //    fault (the #137 "runtime stops scheduling" wedge) would leave the window open forever.
        var advanced = await WaitForSnapshotAsync(runtime, gameId, s =>
            s.Hands[ChowBotSeat].Melds.Any(m => m.Kind == MeldKind.Chow)
            && s.DiscardPile.Any(d => d.SeatIndex == ChowBotSeat),
            TimeSpan.FromSeconds(10));

        Assert.True(advanced is not null,
            $"bot Chow-without-tileIds never resolved+advanced (discardRank={discardRank}, " +
            $"manualDeal={manualDeal}): the runtime wedged in the claim window — the #137 " +
            $"handEnds=0 signature. Final: " + Describe(runtime, gameId));

        // The window resolved into a single valid chow meld that consumed the claimed discard.
        var chows = advanced!.Hands[ChowBotSeat].Melds.Where(m => m.Kind == MeldKind.Chow).ToList();
        var chow = Assert.Single(chows);
        Assert.Equal(3, chow.TileIds.Count);
        Assert.Contains(bait, chow.TileIds);
        // The two consumed concealed tiles are exactly the arranged chow buddies — the lowest-rank
        // fallback matched the only possible pattern (seat 1 held no other Tong tiles).
        Assert.Contains(TH.Tid(Suit.Tong, buddyA, copy: 0), chow.TileIds);
        Assert.Contains(TH.Tid(Suit.Tong, buddyB, copy: 0), chow.TileIds);

        // Play advanced: the chower melded (13 - 2 = 11) then took its turn and discarded (-> 10),
        // and its discard is recorded — the game did NOT wedge in the claim window.
        Assert.Equal(10, advanced.Hands[ChowBotSeat].ConcealedTiles.Count);
        Assert.Contains(advanced.DiscardPile, d => d.SeatIndex == ChowBotSeat);
    }

    // Discarder holds the bait tile plus innocuous, non-colliding fillers (copy 3).
    private static int[] DiscarderHand(int bait) =>
        new[] { bait }.Concat(InnocuousHand(copy: 3)).ToArray();

    // The chow claimer (seat 1): exactly the two Tong chow buddies (copy 0) plus 11 non-Tong fillers,
    // so the ONLY chow pattern the lowest-rank fallback can match is (buddyA, buddyB), and the seat
    // can neither Pung/Kong/Hu the Tong discard nor self-win/kong after chowing. Total = 13 tiles.
    private static int[] ChowClaimerHand(int buddyA, int buddyB) => new[]
    {
        TH.Tid(Suit.Tong, buddyA, copy: 0), TH.Tid(Suit.Tong, buddyB, copy: 0),
        TH.Tid(Suit.Tiao, 1, copy: 0), TH.Tid(Suit.Tiao, 2, copy: 0), TH.Tid(Suit.Tiao, 3, copy: 0),
        TH.Tid(Suit.Tiao, 4, copy: 0), TH.Tid(Suit.Tiao, 5, copy: 0), TH.Tid(Suit.Tiao, 6, copy: 0),
        TH.Tid(Suit.Tiao, 7, copy: 0), TH.Tid(Suit.Tiao, 8, copy: 0), TH.Tid(Suit.Tiao, 9, copy: 0),
        TH.Tid(Suit.Wan, 1, copy: 0), TH.Tid(Suit.Wan, 5, copy: 0),
    };

    // A Tiao/Wan-only hand of a distinct copy — cannot Pung/Chow/Kong/Hu a Tong discard and never
    // collides across seats because each seat uses a different copy index.
    private static int[] InnocuousHand(int copy) => new[]
    {
        TH.Tid(Suit.Tiao, 1, copy), TH.Tid(Suit.Tiao, 4, copy), TH.Tid(Suit.Tiao, 7, copy),
        TH.Tid(Suit.Tiao, 2, copy), TH.Tid(Suit.Tiao, 5, copy), TH.Tid(Suit.Tiao, 8, copy),
        TH.Tid(Suit.Tiao, 3, copy), TH.Tid(Suit.Tiao, 6, copy), TH.Tid(Suit.Tiao, 9, copy),
        TH.Tid(Suit.Wan, 1, copy), TH.Tid(Suit.Wan, 5, copy), TH.Tid(Suit.Wan, 9, copy),
    };

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }
        return predicate();
    }

    private static async Task<ChangshaGameState?> WaitForSnapshotAsync(
        IChangshaGameRuntime runtime, string gameId,
        Func<ChangshaGameState, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snap = await runtime.TryGetSnapshotCopyAsync(gameId);
            if (snap is not null && predicate(snap)) return snap;
            await Task.Delay(20);
        }
        return null;
    }

    private static string Describe(IChangshaGameRuntime runtime, string gameId)
    {
        if (!runtime.TryGetSnapshot(gameId, out var s) || s is null) return "<no snapshot>";
        var h1 = s.Hands.FirstOrDefault(h => h.SeatIndex == ChowBotSeat);
        return $"phase={s.Phase} active={s.ActiveSeatIndex} hand={s.HandNumber} " +
            $"seat1Concealed={h1?.ConcealedTiles.Count} seat1Melds={h1?.Melds.Count} " +
            $"claimWindow={(s.ClaimWindow is null ? "none" : "OPEN")} discards={s.DiscardPile.Count}";
    }
}
