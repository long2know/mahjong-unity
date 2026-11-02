using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tests.Changsha.Acceptance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TH = Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #134 (P0) — the human claim wire contract. The shipped bundle (game-ui.ts sendClaim) writes
/// <c>claim[seat] = { action: 'claim', type: 'Pung'|'Chow'|'Kong'|'Hu' }</c> for a meld/win and
/// <c>{ action: 'pass', type: null }</c> to decline. Pre-fix, <c>TryHandleClaimActionAsync</c> read
/// the <c>action</c> field as the claim type and passed the literal <c>"claim"</c> to
/// <c>ParseClaimType</c> (throw "Unknown claim type claim", swallowed) — so every non-Pass human
/// claim was dropped and the claim window stalled. Only Pass worked.
///
/// <para>These WS-level tests drive the ACTUAL shipped payload for Pung / Chow / Kong / Hu / Pass
/// plus malformed / no-opportunity / out-of-range-seat inputs, and assert the runtime accepts the
/// claim and advances state (meld formed / win declared / turn advances) rather than stalling.</para>
///
/// <para>The claim window is arranged on the runtime's live state at a quiescent point (seat 0 is a
/// human at AwaitingDiscard after the auto-deal, so no bot task is running — the same edit-while-
/// quiescent pattern as DealerExtraTransitionsToAwaitingDiscardTests). A non-seat-0 seat then
/// discards a pinned tile via the pure state machine, opening a REAL adjudicated window in which
/// only seat 0 has an opportunity.</para>
/// </summary>
public sealed class HumanClaimWireContractTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"human-claim-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(o =>
            {
                // Freeze bots — after the human's claim resolves the turn passes to a bot seat; a
                // large delay keeps the game quiescent so the assertion is not racing a bot playing
                // the rest of the hand (and starving the thread pool across the whole class).
                o.BotTurnDelayMs = 60000;
                o.BotClaimDelayMs = 60000;
                o.BotPickupDelayMs = 60000;
                o.ClaimWindowTimeoutMs = 60000; // window must wait for the human's WS claim
                o.DealBatchDelayMs = 0;
                o.PersistSnapshots = false;
            }));
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

    // ── Pung / Kong (any discarder) ────────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task HumanPungClaim_BundlePayload_IsAccepted_AndFormsMeld()
    {
        var seat0 = new[]
        {
            TH.Tid(Suit.Tong, 9, 0), TH.Tid(Suit.Tong, 9, 1), // Pung pair
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
            TH.Tid(Suit.Tiao, 1, 0), TH.Tid(Suit.Tiao, 2, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 9, 2);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 1, discardTile, seat0);

        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: "Pung");

        var final = await WaitForResolutionAsync(arranged, s =>
            s.Hands[0].Melds.Any(m => m.Kind == MeldKind.Pung));
        Assert.NotEqual(ChangshaPhase.AwaitingClaim, final.Phase);
        Assert.Contains(final.Hands[0].Melds, m => m.Kind == MeldKind.Pung && m.TileIds.Count == 3);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task HumanKongClaim_BundlePayload_IsAccepted_AndFormsMeld()
    {
        var seat0 = new[]
        {
            TH.Tid(Suit.Tong, 9, 0), TH.Tid(Suit.Tong, 9, 1), TH.Tid(Suit.Tong, 9, 3), // Kong triple
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
            TH.Tid(Suit.Tiao, 1, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 9, 2);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 2, discardTile, seat0);

        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: "Kong");

        var final = await WaitForResolutionAsync(arranged, s =>
            s.Hands[0].Melds.Any(m => m.TileIds.Count == 4));
        Assert.NotEqual(ChangshaPhase.AwaitingClaim, final.Phase);
        Assert.Contains(final.Hands[0].Melds, m => m.TileIds.Count == 4); // exposed kong (claimed)
    }

    // ── Chow (left neighbor = seat 3; NO tileIds → runtime lowest-rank fallback) ─────

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task HumanChowClaim_BundlePayload_NoTileIds_IsAccepted_ViaLowestRankFallback()
    {
        var seat0 = new[]
        {
            TH.Tid(Suit.Tong, 4, 0), TH.Tid(Suit.Tong, 6, 0), // chow buddies for Tong-5
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
            TH.Tid(Suit.Tiao, 8, 0), TH.Tid(Suit.Tiao, 9, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 5, 2);
        // Chow only from the left neighbor — for seat 0 that is seat 3.
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 3, discardTile, seat0);

        // The bundle sends NO tileIds — the runtime must fall back to the lowest-rank pattern.
        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: "Chow");

        var final = await WaitForResolutionAsync(arranged, s =>
            s.Hands[0].Melds.Any(m => m.Kind == MeldKind.Chow));
        Assert.NotEqual(ChangshaPhase.AwaitingClaim, final.Phase);
        var chow = Assert.Single(final.Hands[0].Melds.Where(m => m.Kind == MeldKind.Chow));
        Assert.Contains(discardTile, chow.TileIds);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task HumanChowClaim_BundlePayload_WithExplicitTileIds_IsAccepted_UsesChosenPattern()
    {
        // Seat 0 holds BOTH chow patterns for a Tong-5 discard: {3,4,5} and {5,6,7}. The runtime's
        // no-tileIds fallback would pick the lowest-rank pattern ({3,4,5}); an explicit tileIds of
        // {Tong-6, Tong-7} must instead build {5,6,7}, proving the endpoint forwards the chosen tiles.
        var tong3 = TH.Tid(Suit.Tong, 3, 0);
        var tong4 = TH.Tid(Suit.Tong, 4, 0);
        var tong6 = TH.Tid(Suit.Tong, 6, 0);
        var tong7 = TH.Tid(Suit.Tong, 7, 0);
        var seat0 = new[]
        {
            tong3, tong4, tong6, tong7, // two chow options for Tong-5: {3,4,5} and {5,6,7}
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 5, 2);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 3, discardTile, seat0);

        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: "Chow", tileIds: new[] { tong6, tong7 });

        var final = await WaitForResolutionAsync(arranged, s =>
            s.Hands[0].Melds.Any(m => m.Kind == MeldKind.Chow));
        Assert.NotEqual(ChangshaPhase.AwaitingClaim, final.Phase);
        var chow = Assert.Single(final.Hands[0].Melds.Where(m => m.Kind == MeldKind.Chow));
        // The chosen upper pattern was honored — not the lowest-rank {3,4,5} fallback.
        Assert.Equal(new[] { discardTile, tong6, tong7 }.OrderBy(t => t), chow.TileIds.OrderBy(t => t));
        Assert.DoesNotContain(tong3, chow.TileIds);
        Assert.DoesNotContain(tong4, chow.TileIds);
    }

    // ── Hu (win by claiming the discard) ────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task HumanHuClaim_BundlePayload_IsAccepted_AndDeclaresWin()
    {
        // A 13-tile hand waiting on Wan-1 (AcceptanceFixture fixture).
        var seat0 = AcceptanceFixture.ThirteenTileWaitingForWan1().ToArray();
        var discardTile = TH.Tid(Suit.Wan, 1, 0);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 1, discardTile, seat0);

        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: "Hu");

        var final = await WaitForResolutionAsync(arranged, s =>
            s.Phase != ChangshaPhase.AwaitingClaim
            && (s.CurrentWin?.WinningSeatIndex == 0
                || s.EventLog.Any(e => e.EventType == "win-declared" && e.SeatIndex == 0)));
        Assert.NotEqual(ChangshaPhase.AwaitingClaim, final.Phase);
        Assert.True(
            final.CurrentWin?.WinningSeatIndex == 0
            || final.EventLog.Any(e => e.EventType == "win-declared" && e.SeatIndex == 0),
            "seat 0's Hu claim must declare a win.");
    }

    // ── Pass (guard — the bundle's { action:'pass', type:null } must still resolve) ──

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task HumanPassClaim_BundlePayload_ResolvesWindow_NoMeld()
    {
        var seat0 = new[]
        {
            TH.Tid(Suit.Tong, 9, 0), TH.Tid(Suit.Tong, 9, 1),
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
            TH.Tid(Suit.Tiao, 1, 0), TH.Tid(Suit.Tiao, 2, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 9, 2);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 1, discardTile, seat0);

        await arranged.Session.SendClaimAsync(seat: 0, action: "pass", type: null);

        var final = await WaitForResolutionAsync(arranged, s => s.Phase != ChangshaPhase.AwaitingClaim);
        Assert.NotEqual(ChangshaPhase.AwaitingClaim, final.Phase);
        Assert.Empty(final.Hands[0].Melds);
    }

    // ── Malformed / unknown / no-opportunity / out-of-range — reject WITHOUT wedging ─

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task MalformedClaim_MissingType_IsRejected_WithoutWedging_ThenPassResolves()
    {
        var seat0 = new[]
        {
            TH.Tid(Suit.Tong, 9, 0), TH.Tid(Suit.Tong, 9, 1),
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
            TH.Tid(Suit.Tiao, 1, 0), TH.Tid(Suit.Tiao, 2, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 9, 2);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 1, discardTile, seat0);

        // action:'claim' with no type — must NOT form a meld and must NOT wedge the window.
        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: null);
        await Task.Delay(300);
        var afterMalformed = await arranged.Runtime.TryGetSnapshotCopyAsync(arranged.GameId);
        Assert.NotNull(afterMalformed);
        Assert.Empty(afterMalformed!.Hands[0].Melds);
        Assert.Equal(ChangshaPhase.AwaitingClaim, afterMalformed.Phase); // window still open, not broken

        // The window is still usable — a subsequent Pass resolves it (no wedge).
        await arranged.Session.SendClaimAsync(seat: 0, action: "pass", type: null);
        var final = await WaitForResolutionAsync(arranged, s => s.Phase != ChangshaPhase.AwaitingClaim);
        Assert.NotEqual(ChangshaPhase.AwaitingClaim, final.Phase);
        Assert.Empty(final.Hands[0].Melds);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task UnknownClaimType_IsRejected_NoMeld()
    {
        var seat0 = new[]
        {
            TH.Tid(Suit.Tong, 9, 0), TH.Tid(Suit.Tong, 9, 1),
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
            TH.Tid(Suit.Tiao, 1, 0), TH.Tid(Suit.Tiao, 2, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 9, 2);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 1, discardTile, seat0);

        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: "Bogus");
        await Task.Delay(300);
        var after = await arranged.Runtime.TryGetSnapshotCopyAsync(arranged.GameId);
        Assert.NotNull(after);
        Assert.Empty(after!.Hands[0].Melds);
        Assert.Equal(ChangshaPhase.AwaitingClaim, after.Phase);
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    public async Task OutOfRangeSeatKey_IsIgnored_WindowUnaffected()
    {
        var seat0 = new[]
        {
            TH.Tid(Suit.Tong, 9, 0), TH.Tid(Suit.Tong, 9, 1),
            TH.Tid(Suit.Wan, 1, 0), TH.Tid(Suit.Wan, 2, 0), TH.Tid(Suit.Wan, 3, 0),
            TH.Tid(Suit.Wan, 4, 0), TH.Tid(Suit.Wan, 5, 0), TH.Tid(Suit.Wan, 6, 0),
            TH.Tid(Suit.Wan, 7, 0), TH.Tid(Suit.Wan, 8, 0), TH.Tid(Suit.Wan, 9, 0),
            TH.Tid(Suit.Tiao, 1, 0), TH.Tid(Suit.Tiao, 2, 0),
        };
        var discardTile = TH.Tid(Suit.Tong, 9, 2);
        await using var arranged = await ArrangeSeat0WindowAsync(discarderSeat: 1, discardTile, seat0);

        // Seat key 7 is out of range — the endpoint must ignore it without touching the window.
        await arranged.Session.SendClaimAsync(seat: 7, action: "claim", type: "Pung");
        await Task.Delay(300);
        var after = await arranged.Runtime.TryGetSnapshotCopyAsync(arranged.GameId);
        Assert.NotNull(after);
        Assert.Equal(ChangshaPhase.AwaitingClaim, after!.Phase);
        Assert.Empty(after.Hands[0].Melds);

        // And the legitimate seat-0 claim still works afterwards (endpoint not wedged).
        await arranged.Session.SendClaimAsync(seat: 0, action: "claim", type: "Pung");
        var final = await WaitForResolutionAsync(arranged, s => s.Hands[0].Melds.Any(m => m.Kind == MeldKind.Pung));
        Assert.Contains(final.Hands[0].Melds, m => m.Kind == MeldKind.Pung);
    }

    // ── Arrangement + helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Connects a human WS client at seat 0 (bots fill 1-3), auto-deals to a quiescent
    /// AwaitingDiscard, then pins hands and drives a pure-state-machine discard from
    /// <paramref name="discarderSeat"/> so a REAL adjudicated claim window opens with ONLY seat 0
    /// holding an opportunity on <paramref name="discardTile"/>.
    /// </summary>
    private async Task<Arranged> ArrangeSeat0WindowAsync(int discarderSeat, int discardTile, int[] seat0Hand)
    {
        var gameId = $"claim-{Guid.NewGuid():N}";
        var session = await OpenAsync(seat: 0, gameId);
        await session.SendJoinAsync(gameId);
        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // initial full UPDATE
        await session.SendUpdateAsync(new object[] { new object[] { "seats", 0, new { seat = 0 } } });

        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var runtimeGameId = await WaitForBindingAsync(manager, gameId, 5000)
            ?? throw new InvalidOperationException("runtime never bound to the connection.");

        // Bot seats are latched by this fixture's botCount=3 creation request.
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var configured));
        Assert.Equal(new[] { 1, 2, 3 },
            configured!.Seats.Where(seat => seat.IsBot).Select(seat => seat.SeatIndex).OrderBy(seat => seat));
        // BE-3 — the seat-take already server-starts the auto game on seat-fill; a redundant
        // explicit start is a harmless no-op. Guard on Seating AND swallow the benign
        // "already started" InvalidOperationException so BE-3's async start can't race-flake.
        try
        {
            if (runtime.TryGetSnapshot(runtimeGameId, out var preStart) && preStart is not null
                && preStart.Phase == ChangshaPhase.Seating)
            {
                await runtime.StartGameAsync(runtimeGameId);
            }
        }
        catch (InvalidOperationException) { /* BE-3 already started the game (seat-fill race) */ }
        var dealt = await WaitForAsync(() =>
            runtime.TryGetSnapshot(runtimeGameId, out var s) && s is not null
            && s.Phase == ChangshaPhase.AwaitingDiscard
            && s.Hands.Sum(h => h.ConcealedTiles.Count) == 14 + 13 + 13 + 13, 5000);
        Assert.True(dealt, "auto-deal did not reach a quiescent AwaitingDiscard.");
        await DrainAsync(session, 300);

        // Pin the four hands and open a real window from `discarderSeat`. Seat 0 is a human at
        // AwaitingDiscard, so no bot task is running — this live-state edit is quiescent.
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var state) && state is not null);
        var others = new[] { 1, 2, 3 }.Where(s => s != discarderSeat).ToArray();
        AcceptanceFixture.OverrideHand(state!, 0, seat0Hand);
        AcceptanceFixture.OverrideHand(state!, discarderSeat, DiscarderHand(discardTile));
        AcceptanceFixture.OverrideHand(state!, others[0], InnocuousHand(copy: 1));
        AcceptanceFixture.OverrideHand(state!, others[1], InnocuousHand(copy: 2));
        state!.ClaimWindow = null;
        state.MissedWinSeats.Clear();
        state.ActiveSeatIndex = discarderSeat;
        state.Phase = ChangshaPhase.AwaitingDiscard;

        // Open the window through the runtime's OWN discard path so it (and the PendingClaims reset)
        // is committed under the instance lock and therefore published to the WS-processing thread.
        // Driving the pure state machine directly would leave the window visible only on this thread,
        // and the WS ClaimAsync/PassAsync (another thread, acquiring the lock) would read a stale phase.
        await runtime.DiscardAsync(runtimeGameId, discarderSeat, discardTile);

        var opened = await runtime.TryGetSnapshotCopyAsync(runtimeGameId);
        Assert.NotNull(opened);
        Assert.Equal(ChangshaPhase.AwaitingClaim, opened!.Phase);
        Assert.Contains(opened.ClaimWindow!.Opportunities, o => o.SeatIndex == 0);
        Assert.DoesNotContain(opened.ClaimWindow!.Opportunities, o => o.SeatIndex != 0);

        return new Arranged(session, runtime, runtimeGameId);
    }

    // Discarder holds the discard tile + innocuous, non-colliding fillers (copy 3).
    private static int[] DiscarderHand(int discardTile) =>
        new[] { discardTile }.Concat(InnocuousHand(copy: 3)).ToArray();

    // A Tiao-only hand (12 tiles) of a distinct copy — cannot Pung/Chow/Kong/Hu a Tong/Wan discard
    // and never collides across seats because each seat uses a different copy index.
    private static int[] InnocuousHand(int copy) => new[]
    {
        TH.Tid(Suit.Tiao, 1, copy), TH.Tid(Suit.Tiao, 4, copy), TH.Tid(Suit.Tiao, 7, copy),
        TH.Tid(Suit.Tiao, 2, copy), TH.Tid(Suit.Tiao, 5, copy), TH.Tid(Suit.Tiao, 8, copy),
        TH.Tid(Suit.Tiao, 3, copy), TH.Tid(Suit.Tiao, 6, copy), TH.Tid(Suit.Tiao, 9, copy),
        TH.Tid(Suit.Wan, 1, copy), TH.Tid(Suit.Wan, 5, copy), TH.Tid(Suit.Wan, 9, copy),
    };

    private static async Task<ChangshaGameState> WaitForResolutionAsync(Arranged arranged, Func<ChangshaGameState, bool> predicate)
    {
        ChangshaGameState? last = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(5000);
        while (DateTime.UtcNow < deadline)
        {
            last = await arranged.Runtime.TryGetSnapshotCopyAsync(arranged.GameId);
            if (last is not null && predicate(last)) return last;
            await Task.Delay(25);
        }
        return last ?? throw new InvalidOperationException("no snapshot available.");
    }

    private async Task<WsSession> OpenAsync(int seat, string gameId)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}&dealMode=auto&botCount=3";
        var uri = new Uri(server.BaseAddress, path);
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        return new WsSession(ws);
    }

    private static async Task<string?> WaitForBindingAsync(AutotableConnectionManager manager, string relayGameId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var rid = manager.GetRuntimeGameIdBoundTo(relayGameId);
            if (rid is not null) return rid;
            await Task.Delay(25);
        }
        return manager.GetRuntimeGameIdBoundTo(relayGameId);
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private static async Task DrainAsync(WsSession session, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { _ = await session.ReadEnvelopeAsync(timeoutMs: 80); }
            catch { return; }
        }
    }

    private sealed record Arranged(WsSession Session, IChangshaGameRuntime Runtime, string GameId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SendUpdateAsync(object[] entries)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "UPDATE", entries, full = false }));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // Emits the EXACT shipped-bundle claim payload: claim[seat] = { action, type }.
        public Task SendClaimAsync(int seat, string action, string? type) =>
            SendUpdateAsync(new object[] { new object[] { "claim", seat, new { action, type } } });

        // Same shape plus explicit `tileIds` (a client that supplies the chosen Chow tiles). The
        // shipped bundle omits tileIds, but the endpoint must forward them when present.
        public Task SendClaimAsync(int seat, string action, string? type, int[] tileIds) =>
            SendUpdateAsync(new object[] { new object[] { "claim", seat, new { action, type, tileIds } } });

        public async Task<JsonElement> ReadEnvelopeAsync(int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None); }
                catch { }
            }
            _ws.Dispose();
        }
    }
}
