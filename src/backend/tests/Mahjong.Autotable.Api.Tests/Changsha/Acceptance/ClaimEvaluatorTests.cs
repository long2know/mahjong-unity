using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase J Wave 1 — pins Bishop's shanten-aware claim acceptance gate in
/// <see cref="HardStrategy"/>. Pre-J the strategy's <c>DecideClaimPhase</c>
/// loop took Hu/Kong/Pung greedily and Chow whenever a small heuristic
/// (<c>Melds.Count &lt; 2 &amp;&amp; CountLooseTiles &lt;= 3</c>) cleared; the
/// rigorous <see cref="HandEvaluator.MinShantenToHu"/> counter shipped in Phase
/// I Wave 4 was never consulted at the claim gate. Bishop's J-1 wiring
/// (commit <c>361d805</c>) promotes the counter to the gate: every non-Hu
/// claim is now simulated, and the strategy only accepts when post-claim
/// shanten strictly drops. Tie-breaker is Hu &gt; Kong &gt; Pung &gt; Chow
/// (matches <see cref="ChangshaClaimPriority.TierOf"/> with an explicit
/// Kong-over-Pung lift since both share tier 2 in the resolver).
///
/// <para><b>Test posture (Phase F style):</b> uses reflection-defensive
/// invocation via <see cref="ChangshaBotEngine.Resolve(string?)"/> so the
/// suite continues to compile even if a future refactor renames the
/// concrete <c>HardStrategy</c> type. Each fact constructs a deterministic
/// 13-tile hand, builds a <see cref="ChangshaClaimWindow"/> for a specific
/// discard, and asserts the resulting <see cref="BotAction"/>.</para>
///
/// <para><b>Why these fixtures probe shanten as expected:</b> the rigorous
/// counter clamps at zero, so naïve "near-winning" hands all read shanten=0
/// and can't differentiate accept-vs-refuse. The fixtures here deliberately
/// sit at shanten ≥ 1 (SevenPairs-leaning for the refuse path; chained
/// partials for the accept path) so the gate's strict-drop comparison is
/// observable in both directions.</para>
/// </summary>
public class ClaimEvaluatorTests
{
    private const string BotNs = "Mahjong.Autotable.Api.Changsha.Bot";
    private static readonly Assembly ApiAssembly = typeof(ChangshaGameState).Assembly;

    private static (object Strategy, Type StrategyInterface) ResolveStrategy(string difficulty)
    {
        var engineType = ApiAssembly.GetType($"{BotNs}.ChangshaBotEngine")
            ?? throw new InvalidOperationException($"Missing type {BotNs}.ChangshaBotEngine — Phase F bot engine not shipped?");
        var resolve = engineType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("ChangshaBotEngine.Resolve(string) missing.");
        var strategy = resolve.Invoke(null, new object[] { difficulty })!;
        var iface = ApiAssembly.GetType($"{BotNs}.IChangshaBotStrategy")
            ?? throw new InvalidOperationException($"Missing type {BotNs}.IChangshaBotStrategy.");
        return (strategy, iface);
    }

    private static BotAction InvokeOnOtherDiscard(object strategy, Type iface,
        ChangshaGameState state, int seat, int discarderSeat, int discardedTileId)
    {
        var method = iface.GetMethod("OnOtherDiscard")
            ?? throw new InvalidOperationException("IChangshaBotStrategy.OnOtherDiscard missing.");
        var raw = method.Invoke(strategy, new object[] { state, seat, discarderSeat, discardedTileId });
        return ToBotAction(raw);
    }

    private static BotAction ToBotAction(object? raw)
    {
        if (raw is BotAction action) return action;
        if (raw is null) return BotAction.Pass();
        var t = raw.GetType();
        var typeName = t.GetProperty("Type")?.GetValue(raw)?.ToString();
        var claim = t.GetProperty("ClaimType")?.GetValue(raw);
        return typeName switch
        {
            "Claim" => claim is TableClaimType ct ? BotAction.Claim(ct) : BotAction.Pass(),
            "Pass" => BotAction.Pass(),
            "Wait" => BotAction.Wait(),
            _ => BotAction.Pass()
        };
    }

    private static ChangshaGameState NewSeatingFixture(int botSeat, IEnumerable<int> botHand)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 4242, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.AwaitingClaim;
        // Strip the dealt hands — we only care about the bot's hand for the
        // claim decision; the other seats' hands aren't read by
        // HardStrategy.DecideClaimPhase.
        foreach (var hand in state.Hands)
            hand.ConcealedTiles.Clear();
        state.Hands[botSeat].ConcealedTiles = botHand.ToList();
        return state;
    }

    /// <summary>
    /// Bishop's gate refuses a Pung whose post-claim shanten is ≥ pre-claim
    /// shanten. Fixture: 5 pairs + 3 lone tiles (shanten=1 via SevenPairs).
    /// Pung'ing one of the pairs breaks the SevenPairs path entirely (a hand
    /// with any declared meld is disqualified by <c>ComputeSevenPairsShanten</c>),
    /// so post-claim must use the Standard path which can only achieve
    /// shanten=2 from the remaining 4 pairs + 3 lones. Strict-drop fails →
    /// Hard must <see cref="BotAction.Pass"/>.
    /// </summary>
    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-1")]
    public void Hard_RefusesPung_WhenItRaisesShanten()
    {
        var (hard, iface) = ResolveStrategy("hard");

        // 5 pairs (SevenPairs candidate, shanten=1) + 3 lone tiles.
        // Wan-1,1 is the pair we'll consume; Pung claim destroys SevenPairs path.
        var botHand = Tiles(
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 7), (Suit.Wan, 7),
            (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 1), (Suit.Tong, 5), (Suit.Tiao, 9));
        var state = NewSeatingFixture(botSeat: 1, botHand);

        // Sanity-pin the fixture: pre-claim shanten=1, post-claim shanten=2.
        // If a future refactor of MinShantenToHu shifts these numbers the test
        // surfaces the regression with a clearer message than the bare action
        // assertion below.
        var preShanten = HandEvaluator.MinShantenToHu(state.Hands[1], Array.Empty<int>());
        Assert.Equal(1, preShanten);

        var discardedTile = Tid(Suit.Wan, 1, 2); // third copy of Wan-1, from seat 0
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 0,
            DiscardTileId = discardedTile,
            Opportunities = new List<ChangshaClaimOpportunity>
            {
                new()
                {
                    SeatIndex = 1,
                    ClaimType = TableClaimType.Pung,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Pung)
                }
            }
        };

        var action = InvokeOnOtherDiscard(hard, iface, state,
            seat: 1, discarderSeat: 0, discardedTileId: discardedTile);

        Assert.Equal(BotActionType.Pass, action.Type);
    }

    /// <summary>
    /// Bishop's gate accepts a Pung whose post-claim shanten strictly drops.
    /// Fixture: a 13-tile hand with two partial chows in Wan, a gap partial
    /// in Tong, a pair head in Tong-5, a partial chow in Tiao, and the Tiao-7
    /// pair we'll Pung. Pre-claim shanten=3; claiming Pung locks the Tiao-7
    /// meld and post-claim shanten drops to 2.
    /// </summary>
    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-1")]
    public void Hard_AcceptsPung_WhenItDropsShanten()
    {
        var (hard, iface) = ResolveStrategy("hard");

        // 2 partials in Wan (2-3, 5-6) + gap (Tong-1,3) + pair head Tong-5,5 +
        // partial Tiao-4,5 + pair Tiao-7,7 + junk Wan-9. 13 tiles, shanten=3.
        var botHand = Tiles(
            (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 3),
            (Suit.Tong, 5), (Suit.Tong, 5),
            (Suit.Tiao, 4), (Suit.Tiao, 5),
            (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Wan, 9));
        var state = NewSeatingFixture(botSeat: 1, botHand);

        var preShanten = HandEvaluator.MinShantenToHu(state.Hands[1], Array.Empty<int>());
        Assert.Equal(3, preShanten);

        var discardedTile = Tid(Suit.Tiao, 7, 2); // third copy of Tiao-7
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 0,
            DiscardTileId = discardedTile,
            Opportunities = new List<ChangshaClaimOpportunity>
            {
                new()
                {
                    SeatIndex = 1,
                    ClaimType = TableClaimType.Pung,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Pung)
                }
            }
        };

        var action = InvokeOnOtherDiscard(hard, iface, state,
            seat: 1, discarderSeat: 0, discardedTileId: discardedTile);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Pung, action.ClaimType);
    }

    /// <summary>
    /// Hu is unconditional — the gate must never refuse a winning claim. Uses
    /// the canonical <see cref="AcceptanceFixture.ThirteenTileWaitingForWan1"/>
    /// hand (shanten=0, completes on Wan-1) with a Hu-only opportunity. Even
    /// though pre-claim shanten=0 (so the "strict drop" predicate would refuse
    /// EVERY non-Hu claim), the Hu fast-path in
    /// <see cref="HardStrategy"/>.<c>DecideClaimPhase</c> must fire first.
    /// </summary>
    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-1")]
    public void Hard_AlwaysAcceptsHu_RegardlessOfShantenCheck()
    {
        var (hard, iface) = ResolveStrategy("hard");

        var botHand = AcceptanceFixture.ThirteenTileWaitingForWan1();
        var state = NewSeatingFixture(botSeat: 1, botHand);

        // Pre-claim shanten is already 0 (clamped) — without the unconditional
        // Hu fast-path the strict-drop check would refuse Hu too. Pinning this
        // value ensures the regression alarm fires correctly if the clamp
        // semantics ever shift.
        var preShanten = HandEvaluator.MinShantenToHu(state.Hands[1], Array.Empty<int>());
        Assert.Equal(0, preShanten);

        var winningTile = Tid(Suit.Wan, 1, 0);
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 2,
            DiscardTileId = winningTile,
            Opportunities = new List<ChangshaClaimOpportunity>
            {
                new()
                {
                    SeatIndex = 1,
                    ClaimType = TableClaimType.Hu,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Hu)
                }
            }
        };

        var action = InvokeOnOtherDiscard(hard, iface, state,
            seat: 1, discarderSeat: 2, discardedTileId: winningTile);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Hu, action.ClaimType);
    }

    /// <summary>
    /// Among multiple shanten-dropping claims, the gate's tie-breaker prefers
    /// the higher tier — Pung &gt; Chow per Bishop's
    /// <c>ClaimAcceptanceRank</c> (Pung=2, Chow=1). Mirrors
    /// <see cref="ChangshaClaimPriority.TierOf"/> ordering. Fixture is
    /// crafted so both options are simultaneously legal AND both strictly
    /// drop pre-claim shanten=2 to post-claim shanten=1, so the only
    /// remaining decision is the tier rank.
    ///
    /// <para><b>Why Pung-vs-Chow and not Kong-vs-Pung:</b> the shanten
    /// counter treats any 3-of-a-kind in the concealed list as a complete
    /// pung group already. Promoting that pung to a declared Kong (which
    /// requires 3 in concealed by definition) cannot strictly drop shanten
    /// — the group is "moved" rather than gained. So Bishop's explicit
    /// Kong-over-Pung lift in <c>ClaimAcceptanceRank</c> is reachable in
    /// theory but not exercisable through realistic adjudicator output;
    /// pinning the same tie-breaker mechanism via Pung-over-Chow proves
    /// the rank-comparison logic without relying on an unrealisable
    /// fixture.</para>
    ///
    /// <para><b>Fixture mechanics:</b> bot holds 3xTong-5, Tong-4, Tong-6
    /// plus three ryanmen partials and a Tiao-7 pair. The discard is
    /// Tong-5 (the fourth copy in the deck). Pung claim removes 2 Tong-5
    /// (leaving 1 dangling that forms a Tong-4–5–6 chow with the
    /// neighbours); Chow Tong-4–5–6 claim removes Tong-4 + Tong-6
    /// (leaving the original 3-of-a-kind as a concealed pung group).
    /// Both decompositions land at the same shanten (1), so Bishop's
    /// rank tie-breaker decides — and it must pick Pung.</para>
    /// </summary>
    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-1")]
    public void Hard_PrefersHigherPriorityTier_AmongShantenDroppingClaims()
    {
        var (hard, iface) = ResolveStrategy("hard");

        var botHand = Tiles(
            (Suit.Tong, 5), (Suit.Tong, 5), (Suit.Tong, 5),
            (Suit.Tong, 4), (Suit.Tong, 6),
            (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7));
        var state = NewSeatingFixture(botSeat: 1, botHand);

        var preShanten = HandEvaluator.MinShantenToHu(state.Hands[1], Array.Empty<int>());
        Assert.Equal(2, preShanten);

        var discardedTile = Tid(Suit.Tong, 5, 3); // fourth copy of Tong-5
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 0,
            DiscardTileId = discardedTile,
            Opportunities = new List<ChangshaClaimOpportunity>
            {
                new()
                {
                    SeatIndex = 1,
                    ClaimType = TableClaimType.Pung,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Pung)
                },
                new()
                {
                    SeatIndex = 1,
                    ClaimType = TableClaimType.Chow,
                    Priority = ChangshaClaimPriority.TierOf(TableClaimType.Chow)
                }
            }
        };

        var action = InvokeOnOtherDiscard(hard, iface, state,
            seat: 1, discarderSeat: 0, discardedTileId: discardedTile);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Pung, action.ClaimType);
    }
}
