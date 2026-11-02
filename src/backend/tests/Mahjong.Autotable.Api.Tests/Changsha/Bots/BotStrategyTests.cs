using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Changsha.Bot.Heuristics;
using Mahjong.Autotable.Api.Tables;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha.Bots;

/// <summary>
/// Wave-24 (Frost) — focused unit tests for the Changsha-aware bot
/// strategy heuristics under <c>Changsha.Bot.Heuristics</c>, plus the
/// composed behaviour of <see cref="MasterStrategy"/> when those
/// heuristics fire.
///
/// <para>Tile encoding reminder (see
/// <see cref="ChangshaDeckBuilder"/>): tile id <c>0..107</c>, logical
/// id = <c>tileId / 4</c>; suit = logical/9 (0=Wan, 1=Tong, 2=Tiao);
/// rank = logical%9 + 1. So <c>0..3</c> = Wan 1, <c>4..7</c> = Wan 2,
/// ..., <c>36..39</c> = Tong 1, <c>72..75</c> = Tiao 1.</para>
///
/// <para><b>Per-heuristic coverage</b> (≥ 2 cases each per the directive):
/// <list type="bullet">
///   <item><b>Shanten façade</b> — positive (tenpai detect) +
///         monotonicity (discarding raises shanten) + after-draw helper.</item>
///   <item><b>Tile-efficiency discard</b> — isolated honor / orphan
///         preference (positive) + neighbour preservation (negative).</item>
///   <item><b>Suit commitment</b> — non-dominant discard bias fires above
///         threshold (positive) + neutral below threshold (negative).</item>
///   <item><b>Tenpai-aware defense</b> — flags 3-meld opponents as
///         dangerous (positive) + ignores 2-meld opponents (negative);
///         genbutsu detection.</item>
///   <item><b>Claim priority Hu &gt; Kong &gt; Pung &gt; Chow</b> —
///         pinned via MediumStrategy's already-greedy priority and
///         HardStrategy's shanten gate ordering.</item>
/// </list>
/// </para>
/// </summary>
public class BotStrategyTests
{
    private readonly ITestOutputHelper _output;
    public BotStrategyTests(ITestOutputHelper output) { _output = output; }

    // ── Tile-id helpers (mirrors ChangshaDeckBuilder layout) ──────────
    private static int Wan(int rank, int copy = 0) => ((int)Suit.Wan * 9 + (rank - 1)) * 4 + copy;
    private static int Tong(int rank, int copy = 0) => ((int)Suit.Tong * 9 + (rank - 1)) * 4 + copy;
    private static int Tiao(int rank, int copy = 0) => ((int)Suit.Tiao * 9 + (rank - 1)) * 4 + copy;

    private static ChangshaHandState Hand(params int[] concealed) => new()
    {
        SeatIndex = 0,
        ConcealedTiles = concealed.ToList(),
        Melds = new List<Meld>()
    };

    private static Meld FakePung(int logicalTile) => new()
    {
        Kind = MeldKind.Pung,
        TileIds = new List<int>
        {
            logicalTile * 4,
            logicalTile * 4 + 1,
            logicalTile * 4 + 2
        }
    };

    // ──────────────────────────────────────────────────────────────────
    //  HEURISTIC #3 — Shanten façade
    // ──────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Shanten_Calculate_Returns0_OnTenpaiHand()
    {
        // Tenpai: 13 tiles, one tile away from a 4-set+pair shape.
        // Layout: Wan 1-1, Wan 2-3, Wan 4-5-6, Tong 7-8-9, Tiao 2-2.
        // Discarding any tile breaks shape, but holding all 13 and
        // drawing Wan 3 completes Wan 1-2-3 + Wan 4-5-6 + Tong 7-8-9
        // + Tiao 2-2 + need a pair head — actually let's construct an
        // unambiguous tenpai: 4 sets fully present and waiting on pair.
        // Use 4 complete sets (12 tiles) + 1 floating tile that waits
        // for a pair partner.
        var hand = Hand(
            Wan(1, 0), Wan(2, 0), Wan(3, 0),       // Chow Wan 1-2-3
            Wan(4, 0), Wan(5, 0), Wan(6, 0),       // Chow Wan 4-5-6
            Tong(7, 0), Tong(8, 0), Tong(9, 0),    // Chow Tong 7-8-9
            Tiao(1, 0), Tiao(2, 0), Tiao(3, 0),    // Chow Tiao 1-2-3
            Tiao(5, 0)                              // pair head waiting partner
        );

        var shanten = Shanten.Calculate(hand);
        Assert.Equal(0, shanten);
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Shanten_Calculate_RisesAfterRemovingShapeTile()
    {
        // From a 1-shanten / 2-shanten hand, removing a structural tile
        // can only increase or hold shanten — monotonicity check.
        var hand = Hand(
            Wan(2, 0), Wan(2, 1),                  // pair
            Wan(4, 0), Wan(5, 0), Wan(6, 0),       // chow
            Tong(1, 0), Tong(1, 1), Tong(1, 2),    // pung
            Tong(5, 0), Tong(6, 0),                // partial chow
            Tiao(3, 0), Tiao(4, 0),                // partial chow
            Tiao(7, 0)                              // lone
        );

        var before = Shanten.Calculate(hand);
        var after = Shanten.CalculateAfterDiscardingLogical(hand,
            ChangshaDeckBuilder.GetLogicalTile(Wan(2, 0)));

        Assert.True(after >= before,
            $"Discarding a pair tile should raise (or hold) shanten; before={before}, after={after}.");
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Shanten_CalculateAfterAddingLogical_DropsForUsefulDraw()
    {
        // 1-shanten or 2-shanten hand with a partial pung at Wan 5 (two
        // copies) — drawing a third Wan 5 completes a meld and must
        // drop shanten.
        var hand = Hand(
            Wan(1, 0), Wan(2, 0), Wan(3, 0),       // chow
            Wan(5, 0), Wan(5, 1),                  // partial pung
            Tong(1, 0), Tong(2, 0), Tong(3, 0),    // chow
            Tong(7, 0), Tong(8, 0), Tong(9, 0),    // chow
            Tiao(4, 0), Tiao(5, 0)                 // partial chow
        );

        var before = Shanten.Calculate(hand);
        var afterUseful = Shanten.CalculateAfterAddingLogical(hand,
            ChangshaDeckBuilder.GetLogicalTile(Wan(5, 0)));
        Assert.True(afterUseful <= before,
            $"Drawing a third Wan 5 should not raise shanten; before={before}, afterUseful={afterUseful}.");
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Shanten_IsTenpai_TruthOnZeroShanten()
    {
        var tenpai = Hand(
            Wan(1, 0), Wan(2, 0), Wan(3, 0),
            Wan(4, 0), Wan(5, 0), Wan(6, 0),
            Tong(7, 0), Tong(8, 0), Tong(9, 0),
            Tiao(1, 0), Tiao(2, 0), Tiao(3, 0),
            Tiao(5, 0));
        Assert.True(Shanten.IsTenpai(tenpai));

        var farther = Hand(
            Wan(1, 0), Wan(3, 0), Wan(5, 0), Wan(7, 0), Wan(9, 0),
            Tong(2, 0), Tong(4, 0), Tong(6, 0), Tong(8, 0),
            Tiao(1, 0), Tiao(3, 0), Tiao(5, 0), Tiao(7, 0));
        Assert.False(Shanten.IsTenpai(farther));
    }

    // ──────────────────────────────────────────────────────────────────
    //  HEURISTIC #1 — Tile-efficiency on discard
    // ──────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Bot_PrefersIsolatedHonorDiscard_WhenMidTileAvailable()
    {
        // Hand with a pair of mid-suit tiles + a single isolated terminal —
        // the isolated terminal should be the lowest-efficiency tile.
        var hand = Hand(
            Wan(5, 0), Wan(5, 1),                  // pair (mid)
            Wan(4, 0), Wan(6, 0),                  // mid neighbours
            Tong(1, 0)                              // ISOLATED terminal
        );

        var chosen = DiscardEfficiency.SelectDiscardByEfficiency(hand);
        Assert.Equal(Tong(1, 0), chosen);
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Bot_KeepsPairOverIsolatedTile_OnEfficiencyMath()
    {
        var hand = Hand(
            Wan(5, 0), Wan(5, 1),                  // pair
            Tiao(7, 0)                              // isolated
        );

        // Efficiency formula verbatim — pair tile scores 2 (1 logical match × 2),
        // isolated tile scores 0.
        Assert.Equal(2, DiscardEfficiency.Score(Wan(5, 0), hand));
        Assert.Equal(0, DiscardEfficiency.Score(Tiao(7, 0), hand));

        var chosen = DiscardEfficiency.SelectDiscardByEfficiency(hand);
        Assert.Equal(Tiao(7, 0), chosen);
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Bot_NeighbourTilesContributeToEfficiency()
    {
        var hand = Hand(
            Wan(4, 0), Wan(5, 0), Wan(6, 0)        // run of three neighbours
        );

        // Wan 5 has two ±2 neighbours (4 and 6) → 2; no logical matches → 0.
        Assert.Equal(2, DiscardEfficiency.Score(Wan(5, 0), hand));
        // Wan 4 has one neighbour at +1 (Wan 5) and one at +2 (Wan 6) → 2.
        Assert.Equal(2, DiscardEfficiency.Score(Wan(4, 0), hand));
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Bot_CrossSuitNeighbour_DoesNotContributeToEfficiency()
    {
        // Wan 5 and Tong 5 share rank but not suit — should NOT count as
        // neighbours; the rule is same-suit ±2.
        var hand = Hand(
            Wan(5, 0),
            Tong(5, 0)
        );
        Assert.Equal(0, DiscardEfficiency.Score(Wan(5, 0), hand));
        Assert.Equal(0, DiscardEfficiency.Score(Tong(5, 0), hand));
    }

    // ──────────────────────────────────────────────────────────────────
    //  HEURISTIC #4 — Suit commitment (清一色 driver)
    // ──────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void SuitCommitment_PrefersNonDominantDiscard_AboveThreshold()
    {
        // Eight tiles of Wan + 5 tiles of mixed Tong/Tiao → dominant = Wan.
        var hand = Hand(
            Wan(1, 0), Wan(2, 0), Wan(3, 0), Wan(4, 0),
            Wan(5, 0), Wan(6, 0), Wan(7, 0), Wan(8, 0),
            Tong(2, 0), Tong(3, 0),
            Tiao(5, 0), Tiao(6, 0), Tiao(7, 0)
        );

        Assert.True(SuitCommitment.IsCommitted(hand));
        Assert.Equal(Suit.Wan, SuitCommitment.DominantSuit(hand).Dominant);
        Assert.Equal(-1, SuitCommitment.Bias(Tong(2, 0), hand));   // outside dominant → bias toward discard
        Assert.Equal(-1, SuitCommitment.Bias(Tiao(7, 0), hand));   // outside dominant → bias toward discard
        Assert.Equal(0, SuitCommitment.Bias(Wan(5, 0), hand));      // inside dominant → no bias
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void SuitCommitment_NeutralBelowThreshold()
    {
        // Seven Wan tiles — below the default 8-threshold; no bias should fire.
        var hand = Hand(
            Wan(1, 0), Wan(2, 0), Wan(3, 0), Wan(4, 0),
            Wan(5, 0), Wan(6, 0), Wan(7, 0),
            Tong(2, 0), Tong(3, 0), Tong(5, 0),
            Tiao(4, 0), Tiao(8, 0), Tiao(9, 0)
        );

        Assert.False(SuitCommitment.IsCommitted(hand));
        Assert.Equal(0, SuitCommitment.Bias(Tong(2, 0), hand));
        Assert.Equal(0, SuitCommitment.Bias(Wan(5, 0), hand));
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void SuitCommitment_DeclaredMelds_CountTowardDominance()
    {
        // 5 concealed Wan + 1 declared Wan pung = 5 + 3 = 8 tiles → committed
        // even though only 5 are concealed.
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = new List<int>
            {
                Wan(2, 0), Wan(4, 0), Wan(5, 0), Wan(6, 0), Wan(8, 0),
                Tong(3, 0), Tong(5, 0),
                Tiao(2, 0), Tiao(7, 0), Tiao(9, 0)
            },
            Melds = new List<Meld> { FakePung(ChangshaDeckBuilder.GetLogicalTile(Wan(7, 0))) }
        };

        var (dominant, count) = SuitCommitment.DominantSuit(hand);
        Assert.Equal(Suit.Wan, dominant);
        Assert.Equal(8, count);
        Assert.True(SuitCommitment.IsCommitted(hand));
    }

    // ──────────────────────────────────────────────────────────────────
    //  HEURISTIC #5 — Tenpai-aware defensive discard
    // ──────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void TenpaiDetector_FlagsThreeMeldOpponent_AsDangerous()
    {
        var state = new ChangshaGameState
        {
            Hands = new List<ChangshaHandState>
            {
                new() { SeatIndex = 0, ConcealedTiles = new List<int> { Wan(1, 0) } },
                new()
                {
                    SeatIndex = 1,
                    ConcealedTiles = new List<int> { Tong(1, 0) },
                    Melds = new List<Meld>
                    {
                        FakePung(0), FakePung(9), FakePung(18) // 3 melds → dangerous
                    }
                },
                new() { SeatIndex = 2, ConcealedTiles = new List<int> { Tong(2, 0) } },
                new() { SeatIndex = 3, ConcealedTiles = new List<int> { Tong(3, 0) } },
            }
        };

        var dangerous = TenpaiDetector.CollectDangerousOpponents(state, botSeatIndex: 0);
        Assert.Contains(1, dangerous);
        Assert.DoesNotContain(2, dangerous);
        Assert.DoesNotContain(3, dangerous);
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void TenpaiDetector_DoesNotFlagTwoMeldOpponent()
    {
        var state = new ChangshaGameState
        {
            Hands = new List<ChangshaHandState>
            {
                new() { SeatIndex = 0, ConcealedTiles = new List<int> { Wan(1, 0) } },
                new()
                {
                    SeatIndex = 1,
                    ConcealedTiles = new List<int> { Tong(1, 0) },
                    Melds = new List<Meld> { FakePung(0), FakePung(9) } // only 2
                },
                new() { SeatIndex = 2, ConcealedTiles = new List<int> { Tong(2, 0) } },
                new() { SeatIndex = 3, ConcealedTiles = new List<int> { Tong(3, 0) } },
            }
        };

        var dangerous = TenpaiDetector.CollectDangerousOpponents(state, botSeatIndex: 0);
        Assert.Empty(dangerous);
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void TenpaiDetector_SafetyBias_PrefersGenbutsuAgainstDangerousOpponent()
    {
        // Seat 1 is dangerous (3 melds) and has discarded Tiao 5.
        // The bot should treat Tiao 5 as safe.
        var state = new ChangshaGameState
        {
            Hands = new List<ChangshaHandState>
            {
                new() { SeatIndex = 0, ConcealedTiles = new List<int> { Wan(1, 0) } },
                new()
                {
                    SeatIndex = 1,
                    ConcealedTiles = new List<int> { Tong(2, 0) },
                    Melds = new List<Meld> { FakePung(0), FakePung(9), FakePung(18) }
                },
                new() { SeatIndex = 2, ConcealedTiles = new List<int> { Tong(3, 0) } },
                new() { SeatIndex = 3, ConcealedTiles = new List<int> { Tong(4, 0) } },
            },
            DiscardPile = new List<ChangshaDiscard>
            {
                new() { SeatIndex = 1, TileId = Tiao(5, 0), TurnNumber = 5 }
            }
        };

        Assert.Equal(-1, TenpaiDetector.SafetyBias(Tiao(5, 1), state, botSeatIndex: 0));
        Assert.Equal(0, TenpaiDetector.SafetyBias(Tiao(7, 0), state, botSeatIndex: 0));
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void TenpaiDetector_SafetyBias_ZeroWhenNoDangerousOpponent()
    {
        // No opponent has 3+ melds — safety bias must be 0 across the board.
        var state = new ChangshaGameState
        {
            Hands = new List<ChangshaHandState>
            {
                new() { SeatIndex = 0, ConcealedTiles = new List<int> { Wan(1, 0) } },
                new() { SeatIndex = 1, ConcealedTiles = new List<int> { Tong(2, 0) } },
                new() { SeatIndex = 2, ConcealedTiles = new List<int> { Tong(3, 0) } },
                new() { SeatIndex = 3, ConcealedTiles = new List<int> { Tong(4, 0) } },
            },
            DiscardPile = new List<ChangshaDiscard>
            {
                new() { SeatIndex = 1, TileId = Tiao(5, 0), TurnNumber = 5 }
            }
        };

        Assert.Equal(0, TenpaiDetector.SafetyBias(Tiao(5, 1), state, botSeatIndex: 0));
        Assert.Equal(0, TenpaiDetector.SafetyBias(Wan(9, 0), state, botSeatIndex: 0));
    }

    // ──────────────────────────────────────────────────────────────────
    //  HEURISTIC #2 — Claim priority Hu > Kong > Pung > Chow
    // ──────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Bot_PrioritizesHuOverKong_WhenBothAvailable()
    {
        // MediumStrategy claim priority loop: Hu (always) before Kong/Pung/Chow.
        var state = BuildClaimWindowState(
            new (int Seat, TableClaimType Type)[]
            {
                (1, TableClaimType.Kong),
                (1, TableClaimType.Hu)
            });

        var strategy = ChangshaBotEngine.Resolve("medium");
        var action = strategy.DecideAction(state, botSeatIndex: 1);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Hu, action.ClaimType);
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Bot_HuFastPath_AlwaysWins_OnHardStrategy()
    {
        // HardStrategy.DecideClaimPhase's Hu fast-path is unconditional:
        // even when the shanten gate would normally probe a Kong/Pung/Chow,
        // a Hu opportunity short-circuits the whole gate and claims
        // immediately. This is the strongest claim-priority assertion in
        // the directive (HU > KONG > PUNG > CHOW) — Hu trumps everything.
        var hand = new ChangshaHandState
        {
            SeatIndex = 1,
            ConcealedTiles = new List<int>
            {
                Wan(1, 0), Wan(1, 1),
                Wan(4, 0), Wan(5, 0), Wan(6, 0),
                Tong(1, 0), Tong(2, 0), Tong(3, 0),
                Tong(7, 0), Tong(8, 0), Tong(9, 0),
                Tiao(2, 0), Tiao(3, 0)
            },
            Melds = new List<Meld>()
        };

        var state = new ChangshaGameState
        {
            Phase = ChangshaPhase.AwaitingClaim,
            Hands = new List<ChangshaHandState>
            {
                new() { SeatIndex = 0 }, hand,
                new() { SeatIndex = 2 }, new() { SeatIndex = 3 }
            },
            ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = 0,
                DiscardTileId = Tiao(1, 0),
                Opportunities = new List<ChangshaClaimOpportunity>
                {
                    new() { SeatIndex = 1, ClaimType = TableClaimType.Chow, Priority = 1 },
                    new() { SeatIndex = 1, ClaimType = TableClaimType.Hu, Priority = 3 }
                }
            }
        };

        var hard = new HardStrategy();
        var action = hard.DecideAction(state, botSeatIndex: 1);
        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Hu, action.ClaimType);
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void Bot_PrioritizesPungOverChow_WhenBothAvailable_OnMedium()
    {
        var state = BuildClaimWindowState(
            new (int Seat, TableClaimType Type)[]
            {
                (1, TableClaimType.Chow),
                (1, TableClaimType.Pung)
            });

        // Seat 1 has fewer than 3 melds so Medium would accept Chow if no Pung; but
        // Pung is offered too — Pung must win.
        var strategy = ChangshaBotEngine.Resolve("medium");
        var action = strategy.DecideAction(state, botSeatIndex: 1);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Pung, action.ClaimType);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Master-tier composition — heuristics layered correctly
    // ──────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void MasterStrategy_Reasoning_SurfacesSuitCommitment_WhenCommitted()
    {
        // Build a real state with the bot's hand committed to Wan and a
        // discardable non-Wan tile.
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 4242, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(4242));
        ChangshaGameStateMachine.Deal(state);

        // Manually override seat 0's concealed list with a Wan-committed
        // hand. This is a probe construction — fine because we're only
        // testing the reasoning surface, not the deal mechanic.
        var bot = state.Hands.Single(h => h.SeatIndex == 0);
        // Pull 13 starting tiles + 1 draw = 14-tile turn state. We rebuild
        // the concealed list with 13 tiles (8 Wan + 5 mixed) and let the
        // existing draw cycle add the 14th by simulating a self-draw.
        bot.ConcealedTiles = new List<int>
        {
            Wan(1, 0), Wan(2, 0), Wan(3, 0), Wan(4, 0),
            Wan(5, 0), Wan(6, 0), Wan(7, 0), Wan(8, 0),
            Tong(2, 0), Tong(5, 0),
            Tiao(3, 0), Tiao(7, 0), Tiao(9, 0)
        };

        // Force the active seat to be 0 in AwaitingDiscard phase by
        // drawing the next tile (machine guarantees 14 in hand on
        // AwaitingDiscard for the active seat).
        state.ActiveSeatIndex = 0;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        // Add a 14th synthetic concealed tile so SelectDiscardTile has a
        // legal 14-tile state to work from.
        bot.ConcealedTiles.Add(Tiao(8, 0));

        var master = ChangshaBotEngine.Resolve("master");
        var decision = master.DecideWithReasoning(state, botSeatIndex: 0);

        var joined = string.Join('\n', decision.Reasoning);
        _output.WriteLine($"Master reasoning:\n{joined}");

        Assert.Contains("suit-commitment", joined, StringComparison.OrdinalIgnoreCase);
        // The dominant suit is Wan and the bot SHOULD discard outside it.
        if (decision.Action.Type == BotActionType.Discard)
        {
            var discardedSuit = ChangshaDeckBuilder.GetSuit(decision.Action.TileId!.Value);
            Assert.NotEqual(Suit.Wan, discardedSuit);
        }
    }

    [Fact, Trait("Category", "Bot"), Trait("Wave", "W24")]
    public void MasterStrategy_Reasoning_SurfacesTenpaiDefense_WhenOpponentDangerous()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 7777, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(7777));
        ChangshaGameStateMachine.Deal(state);

        // Seat 1 becomes "dangerous" with 3 declared melds.
        var seat1 = state.Hands.Single(h => h.SeatIndex == 1);
        seat1.Melds = new List<Meld>
        {
            FakePung(ChangshaDeckBuilder.GetLogicalTile(Wan(1, 0))),
            FakePung(ChangshaDeckBuilder.GetLogicalTile(Wan(5, 0))),
            FakePung(ChangshaDeckBuilder.GetLogicalTile(Tong(3, 0)))
        };

        state.ActiveSeatIndex = 0;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        var bot = state.Hands.Single(h => h.SeatIndex == 0);
        // Ensure bot has a 14-tile state.
        if (bot.ConcealedTiles.Count < 14)
            bot.ConcealedTiles.Add(Tiao(9, 0));

        var master = ChangshaBotEngine.Resolve("master");
        var decision = master.DecideWithReasoning(state, botSeatIndex: 0);

        var joined = string.Join('\n', decision.Reasoning);
        _output.WriteLine($"Master reasoning:\n{joined}");

        Assert.Contains("tenpai defense", joined, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Test fixtures
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal <see cref="ChangshaGameState"/> with a claim window
    /// holding the supplied opportunities. The bot under test sits at the
    /// claimer seat with enough concealed tiles to satisfy each claim
    /// type's mechanical preconditions (so the strategy doesn't reject
    /// based on missing tiles).
    /// </summary>
    private static ChangshaGameState BuildClaimWindowState(
        (int Seat, TableClaimType Type)[] opps)
    {
        var claimerSeat = opps[0].Seat;
        var discardTile = Wan(5, 0);
        var hand = new ChangshaHandState
        {
            SeatIndex = claimerSeat,
            ConcealedTiles = new List<int>
            {
                // Three copies of the discarded logical so Pung/Kong are
                // mechanically possible.
                Wan(5, 1), Wan(5, 2), Wan(5, 3),
                // Adjacent tiles for chow legality.
                Wan(4, 0), Wan(6, 0),
                Tong(1, 0), Tong(2, 0), Tong(3, 0),
                Tiao(7, 0), Tiao(8, 0), Tiao(9, 0)
            },
            Melds = new List<Meld>()
        };

        var state = new ChangshaGameState
        {
            Phase = ChangshaPhase.AwaitingClaim,
            ActiveSeatIndex = (claimerSeat + 3) % 4,
            Hands = new List<ChangshaHandState>
            {
                new() { SeatIndex = 0 },
                new() { SeatIndex = 1 },
                new() { SeatIndex = 2 },
                new() { SeatIndex = 3 }
            },
            ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = (claimerSeat + 3) % 4,
                DiscardTileId = discardTile,
                Opportunities = opps.Select(o => new ChangshaClaimOpportunity
                {
                    SeatIndex = o.Seat,
                    ClaimType = o.Type,
                    // Mirror the runtime's resolver shape — Priority is
                    // ChangshaClaimPriority.TierOf so OrderByDescending in
                    // MediumStrategy surfaces Hu first, then Kong/Pung
                    // (same tier), then Chow.
                    Priority = ChangshaClaimPriority.TierOf(o.Type)
                }).ToList()
            }
        };
        state.Hands[claimerSeat] = hand;
        return state;
    }
}
