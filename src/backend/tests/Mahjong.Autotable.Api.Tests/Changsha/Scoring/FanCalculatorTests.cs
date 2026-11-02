using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Scoring;

/// <summary>
/// Frost — Fan catalog coverage for <see cref="FanCalculator.EvaluateHand"/>.
/// One positive + one negative case per fan (≈35 tests). All cases use the
/// canonical 108-tile Changsha deck (no honors, no dragons) unless the fan is
/// variant-gated. Variant-gated fans (混一色 / 大三元) are exercised with
/// <see cref="FanContext.Variant"/> == <see cref="FanVariant.ExpandedChinese"/>
/// and confirmed to be SUPPRESSED under <see cref="FanVariant.Changsha"/>.
/// </summary>
public class FanCalculatorTests
{
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

    // ─────────────────────────────────────────────────────────────────
    //  Catalog integrity
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void Catalog_HasEntryForEveryFan()
    {
        foreach (Fan fan in Enum.GetValues(typeof(Fan)))
        {
            Assert.True(FanCatalog.Entries.ContainsKey(fan),
                $"FanCatalog missing entry for {fan}");
            var info = FanCatalog.Get(fan);
            Assert.False(string.IsNullOrWhiteSpace(info.Chinese), $"{fan} missing Chinese");
            Assert.False(string.IsNullOrWhiteSpace(info.Pinyin), $"{fan} missing Pinyin");
            Assert.False(string.IsNullOrWhiteSpace(info.English), $"{fan} missing English");
            Assert.True(info.Points > 0, $"{fan} has non-positive points");
        }
    }

    [Fact, Trait("Category", "Changsha")]
    public void Catalog_VariantGatedFans_ExactlyMixedOneSuitAndBigThreeDragons()
    {
        var gated = FanCatalog.Entries.Values
            .Where(i => i.Variant == FanVariant.ExpandedChinese)
            .Select(i => i.Fan)
            .OrderBy(f => (int)f)
            .ToList();
        Assert.Equal(new[] { Fan.MixedOneSuit, Fan.BigThreeDragons }, gated);
    }

    // ─────────────────────────────────────────────────────────────────
    //  自摸 (SelfDraw)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void SelfDraw_FlagSet_FanEmitted()
    {
        // Example: any structurally winning hand drawn from the wall.
        // Hand: 123 / 456 / 789 wan + 22 tong + 234 tiao (Standard, pair=2).
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Tong, 2), (Suit.Tong, 2),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.SelfDraw));
    }

    [Fact, Trait("Category", "Changsha")]
    public void SelfDraw_NotFlagged_FanAbsent()
    {
        // Negative example — same hand won by claiming a discard.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Tong, 2), (Suit.Tong, 2),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = false });
        Assert.False(result.Has(Fan.SelfDraw));
    }

    // ─────────────────────────────────────────────────────────────────
    //  杠上开花 (KongReplacement)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void KongReplacement_FlagSet_FanEmitted()
    {
        // Example: hand wins on the replacement tile after declaring a kong.
        // Three chows + concealed kong of 9 wan + pair of 5 tong.
        // Concealed kong stays in melds; remaining 14-4 = 10 tiles + 4 kong-meld = winning shape.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[] { ConcealedKong(Suit.Wan, 9) });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsSelfDraw = true, IsKongReplacement = true });
        Assert.True(result.Has(Fan.KongReplacement));
        Assert.True(result.Has(Fan.SelfDraw));
    }

    [Fact, Trait("Category", "Changsha")]
    public void KongReplacement_NotFlagged_FanAbsent()
    {
        // Negative — same shape, but the replacement flag is not set.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[] { ConcealedKong(Suit.Wan, 9) });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.False(result.Has(Fan.KongReplacement));
    }

    // ─────────────────────────────────────────────────────────────────
    //  海底捞月 (LastTileFromWall)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void LastTileFromWall_FlagSet_FanEmitted()
    {
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
        Assert.True(result.Has(Fan.LastTileFromWall));
    }

    [Fact, Trait("Category", "Changsha")]
    public void LastTileFromWall_RegularDraw_FanAbsent()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.False(result.Has(Fan.LastTileFromWall));
    }

    // ─────────────────────────────────────────────────────────────────
    //  河底捞鱼 (LastDiscardCatch)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void LastDiscardCatch_FlagSet_FanEmitted()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsLastDiscardCatch = true });
        Assert.True(result.Has(Fan.LastDiscardCatch));
    }

    [Fact, Trait("Category", "Changsha")]
    public void LastDiscardCatch_OrdinaryDiscard_FanAbsent()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.LastDiscardCatch));
    }

    // ─────────────────────────────────────────────────────────────────
    //  抢杠 (RobbingKong)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void RobbingKong_FlagSet_FanEmitted()
    {
        // The winning tile is the 4th tile being added to an opponent's exposed pung.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsRobbingKong = true });
        Assert.True(result.Has(Fan.RobbingKong));
    }

    [Fact, Trait("Category", "Changsha")]
    public void RobbingKong_NotFlagged_FanAbsent()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.RobbingKong));
    }

    // ─────────────────────────────────────────────────────────────────
    //  清一色 (FullFlush)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void FullFlush_AllWan_FanEmitted()
    {
        // 12 wan tiles forming 4 chows + pair of 5.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.True(result.Has(Fan.FullFlush));
    }

    [Fact, Trait("Category", "Changsha")]
    public void FullFlush_MixedSuits_FanAbsent()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.FullFlush));
    }

    // ─────────────────────────────────────────────────────────────────
    //  混一色 (MixedOneSuit) — variant-gated
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void MixedOneSuit_Changsha_AlwaysSuppressed()
    {
        // Even a pure-suit hand cannot trigger MixedOneSuit under pure
        // Changsha because the rule requires honor tiles, which don't exist
        // in the 108-tile deck.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Wan, 2), (Suit.Wan, 2),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.Changsha });
        Assert.False(result.Has(Fan.MixedOneSuit));
    }

    [Fact, Trait("Category", "Changsha")]
    public void MixedOneSuit_ExpandedChinese_NoHonorsInPureChangshaDeck_StillSuppressed()
    {
        // Even with the variant flipped to ExpandedChinese, a hand drawn
        // exclusively from the pure-Changsha id space (0..107) carries no
        // honor tiles, so the fan still cannot fire. This is the negative
        // case for the variant gate — verifies the future-deck hook is
        // correctly inactive until expanded ids land.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
            (Suit.Wan, 2), (Suit.Wan, 2),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.ExpandedChinese });
        Assert.False(result.Has(Fan.MixedOneSuit));
    }

    [Fact, Trait("Category", "Changsha")]
    public void MixedOneSuit_ExpandedChineseWithHonors_FanEmitted()
    {
        // Synthesise an honor-bearing hand by injecting tile ids outside the
        // pure-Changsha range. The calculator's IsHonorTile predicate treats
        // anything outside [0,107] as an honor — this is the forward-compat
        // hook for the future expanded deck. 11 suit tiles + 3 honor copies.
        var concealedTileIds = new List<int>
        {
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 8, 0), Tid(Suit.Wan, 9, 0),
            Tid(Suit.Wan, 2, 1), Tid(Suit.Wan, 2, 2),
            // Three honor-tile copies (id 108 reserved for 红中 in the future
            // deck; the calculator only needs ids outside [0,107] to count as
            // honors for the MixedOneSuit check.).
            108, 108, 108,
        };
        var hand = new WinningHand
        {
            ConcealedTileIds = concealedTileIds,
            Melds = Array.Empty<Meld>(),
        };
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.ExpandedChinese });
        Assert.True(result.Has(Fan.MixedOneSuit));
        // And NOT FullFlush — there are honors present.
        Assert.False(result.Has(Fan.FullFlush));
    }

    // ─────────────────────────────────────────────────────────────────
    //  七对 (SevenPairs)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void SevenPairs_SevenDistinctPairs_FanEmitted()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Tong, 6), (Suit.Tong, 6),
            (Suit.Tong, 7), (Suit.Tong, 7),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 9), (Suit.Tiao, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.True(result.Has(Fan.SevenPairs));
    }

    [Fact, Trait("Category", "Changsha")]
    public void SevenPairs_SixPairsPlusTwoSingles_FanAbsent()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 3), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Tong, 6), (Suit.Tong, 6),
            (Suit.Tong, 7), (Suit.Tong, 7),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 8), (Suit.Tiao, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.SevenPairs));
    }

    // ─────────────────────────────────────────────────────────────────
    //  碰碰胡 (AllPungs)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void AllPungs_FourPungsPlusPair_FanEmitted()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 3), (Suit.Tiao, 3),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.True(result.Has(Fan.AllPungs));
    }

    [Fact, Trait("Category", "Changsha")]
    public void AllPungs_ChowPresent_FanAbsent()
    {
        // 3 pungs + 1 chow + pair → not AllPungs.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
                (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
                (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
                (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
                (Suit.Tong, 2), (Suit.Tong, 2),
            });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.AllPungs));
    }

    // ─────────────────────────────────────────────────────────────────
    //  门清 (ConcealedHand)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void ConcealedHand_NoMelds_FanEmitted()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.True(result.Has(Fan.ConcealedHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void ConcealedHand_OnlyConcealedKong_StillConcealed()
    {
        // Concealed kong is self-drawn → preserves concealment.
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
    public void ConcealedHand_ClaimedPungBreaksConcealment_FanAbsent()
    {
        // Claimed pung from another seat → no longer concealed.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[] { Pung(Suit.Wan, 9, from: 2) });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.ConcealedHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void ConcealedHand_ClaimedChowBreaksConcealment_FanAbsent()
    {
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
                (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
                (Suit.Tong, 5), (Suit.Tong, 5),
            },
            melds: new[] { Chow(Suit.Wan, 1, from: 3) });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.ConcealedHand));
    }

    // ─────────────────────────────────────────────────────────────────
    //  大三元 (BigThreeDragons) — variant-gated
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void BigThreeDragons_PureChangsha_NeverFires()
    {
        // Pure Changsha has no dragon tiles — the fan can never fire.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 3), (Suit.Tiao, 3),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.Changsha });
        Assert.False(result.Has(Fan.BigThreeDragons));
    }

    [Fact, Trait("Category", "Changsha")]
    public void BigThreeDragons_ExpandedChineseWithAllDragonPungs_FanEmitted()
    {
        // Inject three dragon pungs at the reserved future-deck ids
        // (108=红中, 112=發, 116=白). With ExpandedChinese variant, the fan fires.
        var melds = new List<Meld>
        {
            new() { Kind = MeldKind.Pung, TileIds = new() { 108, 109, 110 } },
            new() { Kind = MeldKind.Pung, TileIds = new() { 112, 113, 114 } },
            new() { Kind = MeldKind.Pung, TileIds = new() { 116, 117, 118 } },
        };
        var hand = new WinningHand
        {
            ConcealedTileIds = Tiles((Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Tong, 5), (Suit.Tong, 5)),
            Melds = melds,
        };
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.ExpandedChinese });
        Assert.True(result.Has(Fan.BigThreeDragons));
    }

    [Fact, Trait("Category", "Changsha")]
    public void BigThreeDragons_ExpandedChineseButTwoDragonsOnly_FanAbsent()
    {
        // Only 中 and 發 pungs — 白 missing → big-three-dragons not satisfied.
        var melds = new List<Meld>
        {
            new() { Kind = MeldKind.Pung, TileIds = new() { 108, 109, 110 } },
            new() { Kind = MeldKind.Pung, TileIds = new() { 112, 113, 114 } },
            Pung(Suit.Tong, 4),
        };
        var hand = new WinningHand
        {
            ConcealedTileIds = Tiles((Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Tong, 5), (Suit.Tong, 5)),
            Melds = melds,
        };
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { Variant = FanVariant.ExpandedChinese });
        Assert.False(result.Has(Fan.BigThreeDragons));
    }

    // ─────────────────────────────────────────────────────────────────
    //  天和 (HeavenlyHand)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void HeavenlyHand_FlagSetAndStructurallyValid_FanEmitted()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsHeavenlyHand = true, IsSelfDraw = true });
        Assert.True(result.Has(Fan.HeavenlyHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void HeavenlyHand_StructurallyInvalid_FanSuppressed()
    {
        // Hand isn't a win → HeavenlyHand cannot promote it to a fan.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 1), (Suit.Tong, 7), // pair-mismatched, not a win
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsHeavenlyHand = true, IsSelfDraw = true });
        Assert.False(result.Has(Fan.HeavenlyHand));
    }

    // ─────────────────────────────────────────────────────────────────
    //  地和 (EarthlyHand)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void EarthlyHand_FlagSet_FanEmitted()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand,
            new FanContext { IsEarthlyHand = true });
        Assert.True(result.Has(Fan.EarthlyHand));
    }

    [Fact, Trait("Category", "Changsha")]
    public void EarthlyHand_FlagNotSet_FanAbsent()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.EarthlyHand));
    }

    // ─────────────────────────────────────────────────────────────────
    //  九幺 (NineTerminals)
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void NineTerminals_AllTerminals_FanEmitted()
    {
        // Six distinct terminals — 3 pungs + 2 pairs + 1 single — Changsha's
        // loose 九幺 reading (matches existing WinDetector.CheckNineTerminals).
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 1), (Suit.Tong, 1),
            (Suit.Tong, 9), (Suit.Tong, 9), (Suit.Tong, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 9), (Suit.Tiao, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.True(result.Has(Fan.NineTerminals));
    }

    [Fact, Trait("Category", "Changsha")]
    public void NineTerminals_OneMiddleTile_FanAbsent()
    {
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 1), (Suit.Tong, 1),
            (Suit.Tong, 9), (Suit.Tong, 9), (Suit.Tong, 9),
            (Suit.Tiao, 1), (Suit.Tiao, 1),
            (Suit.Tiao, 5), (Suit.Tiao, 9), // 5 tiao breaks the all-terminals rule
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.False(result.Has(Fan.NineTerminals));
    }

    // ─────────────────────────────────────────────────────────────────
    //  Combinatorial / smoke
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha")]
    public void EvaluateHand_StandardSelfDrawConcealed_StacksSelfDrawAndConcealed()
    {
        // A plain 258-pair Standard win drawn from the wall with no melds:
        // expected fans = SelfDraw + ConcealedHand (the most common compound).
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 2), (Suit.Tong, 2),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.True(result.Has(Fan.ConcealedHand));
        // Expected total = 1 (SelfDraw) + 1 (ConcealedHand) = 2.
        Assert.Equal(2, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void EvaluateHand_DiscardWinWithChow_NoSelfDrawNoConcealed()
    {
        // Discard-claim win with an opponent-claimed chow → neither SelfDraw
        // nor ConcealedHand should fire; calculator returns an empty result.
        var hand = BuildHand(
            concealed: new[]
            {
                (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
                (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
                (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
                (Suit.Tong, 2), (Suit.Tong, 2),
            },
            melds: new[] { Chow(Suit.Tiao, 4, from: 2) });
        var result = FanCalculator.EvaluateHand(hand, new FanContext());
        Assert.Empty(result.Detected);
        Assert.Equal(0, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void EvaluateHand_FullFlushSelfDrawConcealed_AllThreeFansEmitted()
    {
        // Pure-suit hand drawn from the wall, no melds: FullFlush + SelfDraw + ConcealedHand.
        var hand = BuildHand(new[]
        {
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9),
        });
        var result = FanCalculator.EvaluateHand(hand, new FanContext { IsSelfDraw = true });
        Assert.True(result.Has(Fan.FullFlush));
        Assert.True(result.Has(Fan.SelfDraw));
        Assert.True(result.Has(Fan.ConcealedHand));
        Assert.Equal(6 + 1 + 1, result.TotalPoints);
    }

    [Fact, Trait("Category", "Changsha")]
    public void EvaluateHand_DeterministicEnumOrder()
    {
        // SelfDraw (0) + FullFlush (5) + SevenPairs (7) + ConcealedHand (9)
        // should appear in enum-declaration order regardless of insertion order.
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
        var order = result.Detected.Select(d => (int)d.Fan).ToList();
        Assert.Equal(order.OrderBy(i => i).ToList(), order);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Empty_ReusableAndIdempotent()
    {
        Assert.Empty(FanResult.Empty.Detected);
        Assert.Equal(0, FanResult.Empty.TotalPoints);
        Assert.False(FanResult.Empty.Has(Fan.SelfDraw));
    }
}
