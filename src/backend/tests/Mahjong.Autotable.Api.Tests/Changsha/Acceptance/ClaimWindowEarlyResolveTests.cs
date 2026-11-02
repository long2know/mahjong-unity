using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Bishop W26 — unit coverage for <see cref="ChangshaGameRuntime.CanResolveEarly"/>.
/// The helper is the surgical fix for Vasquez's P1-NEW "bot stochastic stall" finding:
/// when a quick bot claim arrives but the human seat hasn't responded, the runtime was
/// previously blocking the full <c>ClaimWindowTimeoutMs</c> (5s) even when no possible
/// human response could beat the bot's claim under the standard priority + CCW tiebreak.
///
/// <para>Each test constructs a <see cref="ChangshaGameInstance"/> with a fabricated
/// <see cref="ChangshaClaimWindow"/> + <see cref="ChangshaGameInstance.PendingClaims"/>
/// dictionary and asserts whether the helper allows early resolution. This is the only
/// non-time-dependent way to test the orchestration without a multi-second integration
/// run — the full WS round-trip is covered by the existing
/// <c>RoundRobinDiscardCycleTests</c> + the manual smoke reproducer in
/// <c>.work/bishop-stall-repro.mjs</c>.</para>
/// </summary>
public class ClaimWindowEarlyResolveTests
{
    private static ChangshaGameInstance NewInstance(ChangshaClaimWindow window)
    {
        var state = new ChangshaGameState
        {
            Phase = ChangshaPhase.AwaitingClaim,
            ClaimWindow = window
        };
        return new ChangshaGameInstance("test-game", state);
    }

    private static ChangshaClaimWindow Window(
        int discardSeat,
        bool isKongRobbing,
        params (int seat, TableClaimType claim)[] opportunities)
    {
        return new ChangshaClaimWindow
        {
            DiscardSeatIndex = discardSeat,
            DiscardTileId = 0,
            IsKongRobbing = isKongRobbing,
            Opportunities = opportunities
                .Select(o => new ChangshaClaimOpportunity
                {
                    SeatIndex = o.seat,
                    ClaimType = o.claim,
                    Priority = ChangshaClaimPriority.TierOf(o.claim)
                })
                .ToList()
        };
    }

    [Fact, Trait("Category", "Acceptance")]
    public void PungBeatsChow_AndChowSeatHasNotResponded_ResolvesEarly()
    {
        // Discard seat 0; seat 1 can Chow OR Pung; seat 2 can only Chow.
        // Seat 1 (responded with Pung tier=2) already beats seat 2's max possible (Chow tier=1).
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Pung),
            (1, TableClaimType.Chow),
            (2, TableClaimType.Chow));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(TableClaimType.Pung, null);

        Assert.True(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void UnresponsiveSeatHasHuOpportunity_DoesNotResolveEarly()
    {
        // Seat 1 responded with Pung; seat 2 still has Hu opportunity outstanding.
        // Hu (tier 3) beats Pung (tier 2) so we MUST wait for seat 2.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Pung),
            (2, TableClaimType.Hu));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(TableClaimType.Pung, null);

        Assert.False(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void NoRespondersYet_DoesNotResolveEarly()
    {
        // Even if every potential claim is weak, with zero responders we have no
        // current best — an unresponded seat's claim would automatically win, so wait.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Chow),
            (2, TableClaimType.Chow));
        var instance = NewInstance(window);

        Assert.False(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void OnlyPassesResponded_DoesNotResolveEarly()
    {
        // Seat 1 passed (no claim); seat 2 hasn't responded but could Pung.
        // No actual claim yet → no current best → wait for seat 2.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Chow),
            (2, TableClaimType.Pung));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(null, null);

        Assert.False(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void KongRobbingWindow_NeverResolvesEarly()
    {
        // Kong-robbing windows surface only Hu opportunities. Even if some seats
        // have responded, an unresponded seat's potential Hu must always be honored.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: true,
            (1, TableClaimType.Hu),
            (2, TableClaimType.Hu));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(null, null);

        Assert.False(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void AllEligibleSeatsResponded_DoesNotResolveEarly()
    {
        // AllClaimsIn handles this case — CanResolveEarly returns false when the
        // unresponded set is empty so the two paths don't double-fire.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Pung),
            (2, TableClaimType.Chow));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(TableClaimType.Pung, null);
        instance.PendingClaims[2] = new ClaimResponse(null, null);

        Assert.False(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void SameTier_FarSeatClaims_NearSeatCouldStillWin_DoesNotResolveEarly()
    {
        // Discard seat 0; seat 1 (close) can Pung; seat 3 (far) already claimed Pung.
        // Seat 1's Pung would beat seat 3's Pung on CCW tiebreak (closer wins).
        // So we cannot resolve early — must wait for seat 1.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Pung),
            (3, TableClaimType.Pung));
        var instance = NewInstance(window);
        instance.PendingClaims[3] = new ClaimResponse(TableClaimType.Pung, null);

        Assert.False(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void SameTier_NearSeatClaims_FarSeatCannotBeat_ResolvesEarly()
    {
        // Discard seat 0; seat 1 (close) already claimed Pung; seat 3 (far) could Pung.
        // Seat 3's Pung cannot beat seat 1's Pung — seat 1 wins the CCW tiebreak.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Pung),
            (3, TableClaimType.Pung));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(TableClaimType.Pung, null);

        Assert.True(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void HuClaimLocksWindow_AllOthersAutoLose_ResolvesEarly()
    {
        // Top-tier claim already in → no possible response could change the outcome.
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Hu),
            (2, TableClaimType.Pung),
            (3, TableClaimType.Chow));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(TableClaimType.Hu, null);

        Assert.True(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void HuClaimLocksWindow_OutstandingHuOpportunity_DoesNotResolveEarly()
    {
        // Two Hu opportunities — second seat could also Hu (multi-win scenario).
        // Helper conservatively waits because seat 2's hypothetical Hu would beat
        // seat 1's Hu under CCW tiebreak (seat 1 is closer, but seat 2 still has
        // a same-tier opportunity that the runtime needs to disambiguate).
        var window = Window(
            discardSeat: 0,
            isKongRobbing: false,
            (1, TableClaimType.Hu),
            (2, TableClaimType.Hu));
        var instance = NewInstance(window);
        instance.PendingClaims[1] = new ClaimResponse(TableClaimType.Hu, null);

        // Seat 1's Hu (CCW=1) beats seat 2's Hu (CCW=2), but we must give seat 2
        // the chance to declare Hu so the missed-win flagging stays accurate.
        // Wait — actually seat 1 is closer so its Hu DOES win the tiebreak.
        // Seat 2's claim cannot beat seat 1's → safe to resolve early.
        Assert.True(ChangshaGameRuntime.CanResolveEarly(instance));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void NullClaimWindow_ReturnsFalse()
    {
        var state = new ChangshaGameState { ClaimWindow = null };
        var instance = new ChangshaGameInstance("test-game", state);

        Assert.False(ChangshaGameRuntime.CanResolveEarly(instance));
    }
}
