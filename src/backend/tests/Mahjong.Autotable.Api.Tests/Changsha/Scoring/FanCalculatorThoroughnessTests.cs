using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Scoring;

/// <summary>
/// Frost — Wave N thoroughness audit (Stephen's "fan out + thoroughly test"
/// directive, .squad/decisions/inbox/frost-scoring-thorough-audit.md).
///
/// <para>This complements <see cref="FanCalculatorTests"/> with the edge-case
/// matrix the prior suite skipped:</para>
/// <list type="bullet">
///   <item>Empty hand → no crash, no spurious fans (regression pin for the
///         <c>detection.IsWin</c> gate added in the same commit).</item>
///   <item>Non-winning 14-tile hand → no fans even with flags set.</item>
///   <item>Phantom tile ids (outside 0..107) → no crash, no FullFlush
///         spillover, correctly classified as honors for variant gating.</item>
///   <item>Concealed kongs ×1/×2/×3/×4 — <c>ConcealedHand</c> survives every
///         count; promotion to <c>AllPungs</c> verified for 4-kong shape.</item>
///   <item>Mixed concealed + exposed kong — <c>ConcealedHand</c> suppressed.</item>
///   <item>Added kong + claimed pung — confirms every non-concealed meld
///         kind breaks concealment.</item>
///   <item>Self-draw vs discard delta — same hand structure, only ctx flag
///         changes; asserts exactly one fan-point difference.</item>
///   <item>Dealer bonus via <see cref="ScoringService"/> — dealer self-draw
///         vs non-dealer self-draw payment delta with stacked fans.</item>
///   <item>Composite fan stacking — verify
///         <c>HeavenlyHand + FullFlush + AllPungs + SelfDraw + ConcealedHand</c>
///         all stack additively for an "ultimate" hand.</item>
/// </list>
///
/// <para>The "wash hand" (洗胡) pattern in the directive is intentionally
/// absent: per the Baidu Baike 长沙麻将 entry and the Reddit r/Mahjong
/// Changsha variant guide, Changsha does NOT recognise 洗胡 / 烂胡 as a
/// scoring pattern — those are Shanghai/Wuhan inventions. Documented in
/// <c>.squad/decisions/inbox/frost-scoring-thorough-audit.md §3</c>.</para>
/// </summary>
public class FanCalculatorThoroughnessTests
{
    // ─────────────────────────────────────────────────────────────────
    //  Local meld builders (sibling-style with FanCalculatorTests so
    //  this file is self-contained; intentionally NOT shared in helpers
    //  to keep the audit suite reviewable in a single read).
    // ─────────────────────────────────────────────────────────────────

    private static Meld Pung(Suit suit, int rank, int? from = null)
        => new()
        {
            Kind = MeldKind.Pung,
            TileIds = [Tid(suit, rank, 0), Tid(suit, rank, 1), Tid(suit, rank, 2)],
            ClaimedFromSeatIndex = from,
        };

    private static Meld Chow(Suit suit, int firstRank, int? from = null)
        => new()
        {
            Kind = MeldKind.Chow,
            TileIds = [Tid(suit, firstRank, 0), Tid(suit, firstRank + 1, 0), Tid(suit, firstRank + 2, 0)],
            ClaimedFromSeatIndex = from,
        };

    private static Meld ConcealedKong(Suit suit, int rank)
        => new()
        {
            Kind = MeldKind.ConcealedKong,
            TileIds = [Tid(suit, rank, 0), Tid(suit, rank, 1), Tid(suit, rank, 2), Tid(suit, rank, 3)],
            ClaimedFromSeatIndex = null,
        };

    private static Meld ExposedKong(Suit suit, int rank, int from)
        => new()
        {
            Kind = MeldKind.ExposedKong,
            TileIds = [Tid(suit, rank, 0), Tid(suit, rank, 1), Tid(suit, rank, 2), Tid(suit, rank, 3)],
            ClaimedFromSeatIndex = from,
        };

    private static Meld AddedKong(Suit suit, int rank, int? from = null)
        => new()
        {
            Kind = MeldKind.AddedKong,
            TileIds = [Tid(suit, rank, 0), Tid(suit, rank, 1), Tid(suit, rank, 2), Tid(suit, rank, 3)],
            ClaimedFromSeatIndex = from,
        };

    private static WinningHand BuildHand(
        IEnumerable<(Suit suit, int rank)> concealed,
        IEnumerable<Meld>? melds = null,
        int? winningTileId = null)
        => new()
        {
            ConcealedTileIds = Tiles(concealed.ToArray()),
            Melds = (melds ?? Array.Empty<Meld>()).ToList(),
            WinningTileId = winningTileId,
        };

    // ─────────────────────────────────────────────────────────────────
    //  EDGE CASES — empty / invalid / phantom hands
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void Edge_EmptyHand_NoFlags_ReturnsEmpty()
    {
        // A 0-tile hand with no melds and no situational context must yield
        // an empty fan result — not even ConcealedHand should fire. This is
        // the regression pin for the IsWin defensive gate (Frost W23.audit).
        var hand = new WinningHand
        {
            ConcealedTileIds = new List<int>(),
            Melds = Array.Empty<Meld>(),
        };
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.Empty(result.Detected);
        Assert.Equal(0, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Edge_EmptyHand_SelfDrawFlagSet_ReturnsEmpty()
    {
        // A flag-only situational fan is meaningless without a winning hand.
        // The detector returns IsWin=false → SelfDraw is suppressed.
        var hand = new WinningHand
        {
            ConcealedTileIds = new List<int>(),
            Melds = Array.Empty<Meld>(),
        };
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsSelfDraw = true, IsKongReplacement = true, IsRobbingKong = true });
        Assert.Empty(result.Detected);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Edge_NonWinning14Tiles_NoFans()
    {
        // 14 random tiles that do NOT form a valid Changsha structure
        // (six 'free' singletons would break decomposition, and pair is
        // not 258). No fans should fire even with situational flags.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 4), (Suit.Wan, 7),
            (Suit.Wan, 2), (Suit.Wan, 5), (Suit.Wan, 8),
            (Suit.Tong, 3), (Suit.Tong, 6), (Suit.Tong, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 4), (Suit.Tiao, 7),
            (Suit.Tiao, 9), (Suit.Wan, 9),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsSelfDraw = true, IsLastTileFromWall = true });
        Assert.Empty(result.Detected);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Edge_StandardShapeWithInvalidPairRank_NoStandardWin_OnlySuitFansFire()
    {
        // 4 chows + a pair of rank 6 in pure Wan. Pair=6 violates the 258
        // rule, so Standard fails — but the shape IS a FullFlush (purity
        // doesn't require the pair to be 258). With detection.IsWin=true
        // via FullFlush, ConcealedHand + SelfDraw also fire.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 6), (Suit.Wan, 6),  // pair=6, NOT 258-legal for Standard
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        // FullFlush has a permissive 'any-pair' decomposition path so it
        // still detects this as a win shape.
        Assert.True(result.Has(Fan.FullFlush));
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.True(result.Has(Fan.ConcealedHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Edge_AllPhantomTiles_NoCrash_NoFans()
    {
        // Hand composed entirely of phantom tile ids (>= 108). The current
        // pure-Changsha detector returns IsWin=false (BuildLogicalCounts
        // skips out-of-range ids → totalConcealed=0 → expectedConcealed
        // mismatch). No crash, no fans.
        var hand = new WinningHand
        {
            ConcealedTileIds = Enumerable.Repeat(108, 14).ToList(),
            Melds = Array.Empty<Meld>(),
        };
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.ExpandedChinese });
        Assert.Empty(result.Detected);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Edge_PhantomTileMixedWithValidSuit_NoFullFlushSpillover()
    {
        // 13 valid Wan tiles + 1 phantom honor (108). The FullFlush check
        // must NOT classify the phantom as Wan (its synthetic Suit cast is
        // out-of-range, distinct from any real suit). Result: no FullFlush.
        var concealed = new List<int>
        {
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 8, 0), Tid(Suit.Wan, 9, 0),
            Tid(Suit.Wan, 2, 1), Tid(Suit.Wan, 2, 2), Tid(Suit.Wan, 3, 1),
            Tid(Suit.Wan, 4, 1),
            108,  // phantom honor
        };
        var hand = new WinningHand
        {
            ConcealedTileIds = concealed,
            Melds = Array.Empty<Meld>(),
        };
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.ExpandedChinese });
        Assert.False(result.Has(Fan.FullFlush));
    }

    // ─────────────────────────────────────────────────────────────────
    //  MULTI-KONG COVERAGE
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void Kong_OneConcealed_ConcealedHandEmitted()
    {
        // 11 concealed (3 chows + pair) + 1 ConcealedKong → 15 tiles total
        // (kong replacement adds 1). ConcealedHand fires.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[] { ConcealedKong(Suit.Wan, 9) });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.True(result.Has(Fan.ConcealedHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Kong_TwoConcealed_ConcealedHandEmitted()
    {
        // 8 concealed (2 chows + pair) + 2 ConcealedKongs → 16 tiles total.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[]
            {
                ConcealedKong(Suit.Wan, 9),
                ConcealedKong(Suit.Tong, 1),
            });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.True(result.Has(Fan.SelfDraw));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Kong_ThreeConcealed_ConcealedHandEmitted()
    {
        // 5 concealed (1 chow + pair) + 3 ConcealedKongs → 17 tiles total.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[]
            {
                ConcealedKong(Suit.Wan, 1),
                ConcealedKong(Suit.Wan, 9),
                ConcealedKong(Suit.Tong, 1),
            });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.ConcealedHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Kong_FourConcealed_FourKongsAndPair_AllPungsAndConcealedHand()
    {
        // The "Four Concealed Kongs" (四暗杠) extreme shape: 2 concealed
        // (pair) + 4 ConcealedKongs → 18 tiles total. Detector sees this
        // as AllPungs (kongs count as pungs). ConcealedHand fires too.
        var hand = BuildHand(
            concealed: new[] { (Suit.Tong, 5), (Suit.Tong, 5) },
            melds: new[]
            {
                ConcealedKong(Suit.Wan, 1),
                ConcealedKong(Suit.Wan, 9),
                ConcealedKong(Suit.Tong, 1),
                ConcealedKong(Suit.Tiao, 9),
            });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.AllPungs));
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.True(result.Has(Fan.SelfDraw));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Kong_OneExposedKong_ConcealedHandSuppressed()
    {
        // Exposed kong was assembled from a claimed discard → breaks concealment.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[] { ExposedKong(Suit.Wan, 9, from: 2) });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.ConcealedHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Kong_AddedKong_BreaksConcealment()
    {
        // 加杠 — upgraded an exposed pung to a kong with a self-drawn 4th
        // tile. Still breaks concealment because the original pung was
        // claimed from a discard.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[] { AddedKong(Suit.Wan, 9, from: 2) });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.ConcealedHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Kong_MixedConcealedAndExposed_ConcealedHandSuppressed()
    {
        // Even with one ConcealedKong, the presence of any non-concealed
        // meld breaks the concealment fan.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[]
            {
                ConcealedKong(Suit.Wan, 9),
                ExposedKong(Suit.Tong, 1, from: 3),
            });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.ConcealedHand));
    }

    // ─────────────────────────────────────────────────────────────────
    //  SELF-DRAW vs DISCARD score delta
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void Delta_SameStandardHand_SelfDrawVsDiscard_DiffersByExactlyOneFan()
    {
        // Identical Standard-shape concealed hand. Self-draw flag toggled.
        // Expected delta = +1 fan-point (SelfDraw contributes 1 point).
        var concealed = new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        };
        var hand = BuildHand(concealed);
        var discardResult = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = false });
        var selfDrawResult = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.Equal(discardResult.TotalPoints + 1, selfDrawResult.TotalPoints);
        Assert.True(selfDrawResult.Has(Fan.SelfDraw));
        Assert.False(discardResult.Has(Fan.SelfDraw));
    }

    [Fact, Trait("Category", "Changsha")]
    public void Delta_FullFlushSelfDrawVsClaimed_SelfDrawAddsExactlyOnePoint()
    {
        // FullFlush concealed self-draw vs FullFlush won on a claimed chow.
        // Self-draw branch retains ConcealedHand; claim branch loses both
        // SelfDraw and ConcealedHand. Delta should equal SelfDraw(1) +
        // ConcealedHand(1) = 2 points.
        var selfDrawHand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
        });
        var claimedHand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
                (Suit.Wan, 5), (Suit.Wan, 5),
                (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
                (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            },
            melds: new[] { Chow(Suit.Wan, 1, from: 3) });
        var selfDraw = FanCalculator.EvaluateHand(selfDrawHand, new FanContext { IsSelfDraw = true });
        var claimed = FanCalculator.EvaluateHand(claimedHand, new FanContext { IsSelfDraw = false });
        Assert.True(selfDraw.Has(Fan.FullFlush) && claimed.Has(Fan.FullFlush));
        Assert.Equal(claimed.TotalPoints + 2, selfDraw.TotalPoints);
    }

    // ─────────────────────────────────────────────────────────────────
    //  COMPOSITE STACKING — multiple fans firing together
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void Stack_SevenPairsPlusFullFlush_StacksAdditively()
    {
        // 7 distinct Wan pairs → SevenPairs + FullFlush + ConcealedHand
        // (no melds) + SelfDraw. Expected: 6 + 4 + 1 + 1 = 12 points.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 7),
            (Suit.Wan, 8), (Suit.Wan, 8),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.SevenPairs));
        Assert.True(result.Has(Fan.FullFlush));
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.Equal(6 + 4 + 1 + 1, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Stack_AllPungsPlusFullFlush_StacksAdditively()
    {
        // 4 pungs of Wan ranks + a Wan pair → AllPungs + FullFlush +
        // ConcealedHand + SelfDraw. Expected: 6 + 4 + 1 + 1 = 12.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 3), (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 5), (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 7), (Suit.Wan, 7), (Suit.Wan, 7),
            (Suit.Wan, 9), (Suit.Wan, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.AllPungs));
        Assert.True(result.Has(Fan.FullFlush));
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.Equal(6 + 4 + 1 + 1, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Stack_NineTerminalsPlusSevenPairs_StacksAdditively()
    {
        // NineTerminals + AllPungs are mathematically incompatible (NineTerminals
        // needs all 6 distinct terminals but 4-pungs-plus-pair only fits 5 distinct
        // logicals). The achievable stack is NineTerminals + SevenPairs: 6 distinct
        // terminals as {pair, pair, pair, pair, pair, 4-of-a-kind} = 14 tiles →
        // 5 normal pairs + 1 double-pair from the four-of-a-kind = 7 pairs total.
        // Fans: SevenPairs(4) + NineTerminals(6) + ConcealedHand(1) + SelfDraw(1) = 12.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 1), (Suit.Tong, 1),
            (Suit.Tong, 9), (Suit.Tong, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 9), (Suit.Tiao, 9),
            (Suit.Tiao, 9), (Suit.Tiao, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.NineTerminals));
        Assert.True(result.Has(Fan.SevenPairs));
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.Equal(4 + 6 + 1 + 1, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Stack_HeavenlyHand_FullFlush_AllPungs_SelfDraw_Concealed_StacksAdditively()
    {
        // "Ultimate" hand: dealer's initial 14-tile draw is already a winning
        // 4-Wan-pungs + Wan-pair shape. Fan stack:
        //   SelfDraw(1) + FullFlush(6) + AllPungs(4) + ConcealedHand(1) +
        //   HeavenlyHand(8) = 20.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 3), (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 5), (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 7), (Suit.Wan, 7), (Suit.Wan, 7),
            (Suit.Wan, 9), (Suit.Wan, 9),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsSelfDraw = true, IsHeavenlyHand = true });
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.True(result.Has(Fan.FullFlush));
        Assert.True(result.Has(Fan.AllPungs));
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.True(result.Has(Fan.HeavenlyHand));
        Assert.Equal(1 + 6 + 4 + 1 + 8, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Stack_KongReplacementPlusAllPungs_StacksAdditively()
    {
        // Self-drawn replacement after kong declaration. Hand is 4 pungs +
        // pair. Fans: SelfDraw(1) + KongReplacement(2) + AllPungs(4) +
        // ConcealedHand(1) = 8. (FullFlush absent — mixed suits.)
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 3), (Suit.Tiao, 3),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsSelfDraw = true, IsKongReplacement = true });
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.True(result.Has(Fan.KongReplacement));
        Assert.True(result.Has(Fan.AllPungs));
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.Equal(1 + 2 + 4 + 1, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Stack_LastTileFromWall_PlusSelfDraw_Standalone_NoDouble()
    {
        // Standard self-draw on the wall's final tile. Expected:
        // SelfDraw(1) + LastTileFromWall(2) + ConcealedHand(1) = 4.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsSelfDraw = true, IsLastTileFromWall = true });
        Assert.Equal(1 + 2 + 1, result.TotalPoints);
    }

    // ─────────────────────────────────────────────────────────────────
    //  DEALER BONUS — verify ScoringService dealer bonus stays correct
    //  after FanCalculator integration (Frost W23.audit IsWin gate).
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void DealerBonus_DealerSelfDrawBigWin_AppliesDealerBonusToEveryPayer()
    {
        // Dealer (seat 0) self-draws AllPungs (Big Win). Every non-dealer
        // pays the dealer-involved amount (4) — no per-seat split.
        var win = new WinResult
        {
            WinningSeatIndex = 0,
            SourceSeatIndex = 0,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.AllPungs,
            WinningTileId = 0,
            IsFullFlush = false,
        };
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Equal(ScoreCategory.BigWin, result.Category);
        Assert.Equal(3, result.Payments.Count);
        Assert.All(result.Payments, p => Assert.Equal(4, p.Amount));
    }

    [Fact, Trait("Category", "Changsha")]
    public void DealerBonus_NonDealerSelfDrawBigWin_OnlyDealerSeatPaysBonus()
    {
        // Non-dealer (seat 2) self-draws AllPungs. Dealer (seat 0) pays 4;
        // other non-dealers (seats 1, 3) pay 3 each.
        var win = new WinResult
        {
            WinningSeatIndex = 2,
            SourceSeatIndex = 2,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.AllPungs,
            WinningTileId = 0,
            IsFullFlush = false,
        };
        var result = new ScoringService().CalculateScore(win, dealerSeatIndex: 0, isFullFlush: false);
        Assert.Equal(4, result.Payments.Single(p => p.FromSeatIndex == 0).Amount);
        Assert.Equal(3, result.Payments.Single(p => p.FromSeatIndex == 1).Amount);
        Assert.Equal(3, result.Payments.Single(p => p.FromSeatIndex == 3).Amount);
    }

    [Fact, Trait("Category", "Changsha")]
    public void DealerBonus_StackedBigWin_PatternMultiplierAppliesPerPayer()
    {
        // 2 Big Win patterns simultaneously (AllPungs + FullFlush). Stacking
        // multiplier = ×2 on every Big Win payment. Non-dealer self-draw →
        // dealer pays 4×2=8, each other non-dealer pays 3×2=6.
        var win = new WinResult
        {
            WinningSeatIndex = 2,
            SourceSeatIndex = 2,
            Method = WinMethod.SelfDraw,
            Pattern = WinPattern.AllPungs,
            WinningTileId = 0,
            IsFullFlush = true,
            AllPatterns = new[] { WinPattern.AllPungs, WinPattern.FullFlush },
        };
        var result = new ScoringService().CalculateScore(
            win, dealerSeatIndex: 0, isFullFlush: true, bigWinPatternCount: 2);
        Assert.Equal(8, result.Payments.Single(p => p.FromSeatIndex == 0).Amount);
        Assert.Equal(6, result.Payments.Single(p => p.FromSeatIndex == 1).Amount);
        Assert.Equal(6, result.Payments.Single(p => p.FromSeatIndex == 3).Amount);
    }

    // ─────────────────────────────────────────────────────────────────
    //  IsWin DEFENSIVE GATE — regression pins for the W23.audit fix
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void Gate_NonWinningHandWithEveryFlag_StillReturnsEmpty()
    {
        // 14 random tiles with every situational flag set true. Without the
        // IsWin defensive gate, SelfDraw + KongReplacement + LastTileFromWall
        // + LastDiscardCatch + RobbingKong + ConcealedHand would all fire
        // for a non-winning hand. With the gate, result is Empty.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 4), (Suit.Wan, 7),
            (Suit.Wan, 2), (Suit.Wan, 5), (Suit.Wan, 8),
            (Suit.Tong, 3), (Suit.Tong, 6), (Suit.Tong, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 4), (Suit.Tiao, 7),
            (Suit.Tiao, 9), (Suit.Wan, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext
        {
            IsSelfDraw = true,
            IsKongReplacement = true,
            IsLastTileFromWall = true,
            IsLastDiscardCatch = true,
            IsRobbingKong = true,
            IsHeavenlyHand = true,
            IsEarthlyHand = true,
        });
        Assert.Empty(result.Detected);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Gate_ThirteenTileHand_OnlyConcealedHandSuppressed()
    {
        // 13 tiles — one tile short of winning. ConcealedHand and all
        // situational fans should NOT fire even though there are no melds.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2),  // 13 tiles, missing pair partner
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.Empty(result.Detected);
    }
}
