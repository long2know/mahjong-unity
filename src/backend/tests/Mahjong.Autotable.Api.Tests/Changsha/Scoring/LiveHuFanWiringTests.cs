using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Tables;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Scoring;

/// <summary>
/// Frost — Live-Hu wire-proof acceptance tests. Stephen wanted PROOF that
/// <see cref="FanCalculator"/> actually fires during a real game and that the
/// detected fans land on the autotable bundle WS so the frontend
/// <c>renderResult</c> path can read them — not just that
/// <see cref="FanCalculator.EvaluateHand"/> behaves correctly in isolation.
///
/// <para>The wiring stack under test (verified to be present at audit time —
/// memory claiming "FanCalculator is NOT wired" was OUTDATED):</para>
/// <list type="number">
///   <item><c>ChangshaGameStateMachine.DeclareSelfDrawWin / ResolveClaim(Hu)</c>
///         → sets <see cref="ChangshaGameState.CurrentWin"/> and transitions
///         to <see cref="ChangshaPhase.Scoring"/>.</item>
///   <item><c>ChangshaGameStateMachine.Score</c> → composes a
///         <see cref="FanContext"/> from win flags + state, calls
///         <see cref="FanCalculator.EvaluateHand"/>, and writes
///         <see cref="ScoreResult.Fans"/> + <see cref="ScoreResult.FanPoints"/>
///         (production wiring; see <c>ChangshaStateMachine.cs:944</c>).</item>
///   <item><c>ChangshaToAutotableTranslator.BuildHandResult</c> → projects
///         <c>score.Fans</c> into <see cref="ScoreResultEntry.Fans"/> with the
///         camelCase wire identifier + Chinese / Pinyin / English labels
///         pulled from <see cref="FanCatalog"/>
///         (<c>ChangshaToAutotableTranslator.cs:274</c>).</item>
///   <item><c>ChangshaCollectionEncoder.EncodeHandResult</c> →
///         <see cref="CollectionEntry"/> tuple
///         <c>["result", "current", {...}]</c> serialized through
///         <see cref="AutotableJson.Options"/> — the exact same byte stream
///         that hits the WebSocket.</item>
/// </list>
///
/// <para>Each test drives the real state machine through a deterministic Hu
/// (no synthetic events), then serializes the wire envelope and asserts the
/// JSON contains the expected <c>scoreResult.fans</c> array with non-empty
/// localised labels. The "fans = empty array when no Hu" contract from
/// Frost's W23 thoroughness audit is also pinned here on the Draw path.</para>
///
/// <para><b>Lane discipline:</b> reads-only of <c>Changsha/Scoring/**</c>,
/// <c>Changsha/StateMachine/**</c>, <c>Autotable/**</c>. No production code
/// modified.</para>
/// </summary>
public class LiveHuFanWiringTests
{
    // ─────────────────────────────────────────────────────────────────
    //  Wire-shape helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Round-trips a <see cref="HandResultEntry"/> through the same
    /// <see cref="AutotableJson.Options"/> the WS endpoint uses, returning
    /// the parsed root element. Pins the on-wire shape, not the in-memory
    /// .NET shape — case differences (PascalCase property names → camelCase
    /// JSON) and serializer config drift surface here, not in
    /// production-only paths.
    /// </summary>
    private static JsonElement SerializeHandResultToWire(HandResultEntry entry)
    {
        var envelope = ChangshaCollectionEncoder.EncodeHandResult(entry);
        var json = JsonSerializer.Serialize(envelope, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        // CollectionEntry wire shape is [kind, key, value]; the bundle
        // collection consumer indexes `[2]` for the inner value object.
        return doc.RootElement[2].Clone();
    }

    /// <summary>
    /// Drives the real <see cref="ChangshaGameStateMachine"/> from a
    /// post-deal state through self-draw win + scoring, returning the final
    /// <see cref="ChangshaGameState"/> ready for translator projection.
    /// </summary>
    private static ChangshaGameState DriveSelfDrawSevenPairs(
        int dealerSeat, ChangshaScoringOptions? options = null)
    {
        var state = BuildPostDealState(dealerSeat);
        SuppressFirstDiscardContext(state);

        // 7 distinct pairs that cannot reorganise into a Standard 4-sets+pair
        // shape (no three-of-a-kind overlap, no chow run). The detector locks
        // onto SevenPairs unambiguously.
        OverrideConcealedWith14(state, dealerSeat,
            (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Tong, 2), (Suit.Tong, 2),
            (Suit.Tong, 6), (Suit.Tong, 6),
            (Suit.Tiao, 3), (Suit.Tiao, 3),
            (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 9), (Suit.Tiao, 9));
        ClearOtherHands(state, keepSeat: dealerSeat);

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, dealerSeat);
        ChangshaGameStateMachine.Score(state, options);
        return state;
    }

    /// <summary>
    /// Drives the real state machine through a discard-Hu where seat 1
    /// completes a 7-pair shape on dealer seat 0's Wan-1 discard.
    /// </summary>
    private static ChangshaGameState DriveDiscardSevenPairsByNonDealer()
    {
        var state = BuildPostDealState(dealerSeat: 0);
        SuppressFirstDiscardContext(state);

        // Seat 1: 6 distinct pairs + a lone Wan-1 (13 tiles total). The
        // discard of Wan-1 by the dealer completes the 7th pair.
        var seat1Tiles = Tiles(
            (Suit.Wan, 1),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Tong, 2), (Suit.Tong, 2),
            (Suit.Tong, 6), (Suit.Tong, 6),
            (Suit.Tiao, 3), (Suit.Tiao, 3),
            (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 9), (Suit.Tiao, 9));
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].ConcealedTiles.AddRange(seat1Tiles);
        state.Hands[1].Melds.Clear();

        // Remove every other Wan-1 in the deck so the only path to a Wan-1
        // in the claim window is the dealer's discard we plant below.
        for (var seatIdx = 0; seatIdx < 4; seatIdx++)
        {
            if (seatIdx == 1) continue;
            state.Hands[seatIdx].ConcealedTiles.RemoveAll(t => t / 4 == Logical(Suit.Wan, 1));
        }
        ClearOtherHands(state, keepSeat: 1);
        // ClearOtherHands also drops seat 1's tiles if keepSeat != 1, so
        // re-set them defensively. Here keepSeat IS 1 so this is a no-op,
        // but we leave the re-set in to make refactors safe.
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].ConcealedTiles.AddRange(seat1Tiles);

        // Dealer seat 0 needs 13 tiles to discard from. Plant a Wan-1 (copy 1
        // so it doesn't collide with seat 1's copy 0) + 12 benign tiles.
        var seat0Tiles = new List<int>
        {
            Tid(Suit.Wan, 1, 1),
            Tid(Suit.Wan, 9, 0), Tid(Suit.Tong, 9, 0), Tid(Suit.Tiao, 1, 0),
            Tid(Suit.Wan, 8, 0), Tid(Suit.Tong, 8, 0), Tid(Suit.Tiao, 1, 1),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Tong, 7, 0), Tid(Suit.Tiao, 1, 2),
            Tid(Suit.Wan, 6, 0), Tid(Suit.Tong, 5, 0), Tid(Suit.Tiao, 4, 0),
        };
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].ConcealedTiles.AddRange(seat0Tiles);
        state.Hands[0].Melds.Clear();

        state.ActiveSeatIndex = 0;
        state.Phase = ChangshaPhase.AwaitingDiscard;

        var wan1Id = Tid(Suit.Wan, 1, 1);
        ChangshaGameStateMachine.Discard(state, 0, wan1Id);
        ChangshaGameStateMachine.ResolveClaim(state, 1, TableClaimType.Hu);
        ChangshaGameStateMachine.Score(state);
        return state;
    }

    // ─────────────────────────────────────────────────────────────────
    //  1. Self-draw Hu → wire JSON contains expected fans
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringLiveWiring"), Trait("Wave", "LiveWiring-Frost")]
    public void SelfDrawSevenPairsHu_FlowsThroughTranslator_EmitsFansOnWire()
    {
        var state = DriveSelfDrawSevenPairs(dealerSeat: 0);

        // State-machine layer (pre-translator) — sanity gates so failures
        // upstream of the translator are obvious from the assertion text.
        Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        Assert.NotNull(state.CurrentWin);
        Assert.True(state.CurrentWin!.IsSelfDraw);
        Assert.NotNull(state.CurrentScore);
        Assert.Equal(ScoreCategory.BigWin, state.CurrentScore!.Category);
        Assert.Contains(state.CurrentScore.Fans, f => f.Fan == Fan.SelfDraw);
        Assert.Contains(state.CurrentScore.Fans, f => f.Fan == Fan.SevenPairs);
        Assert.Contains(state.CurrentScore.Fans, f => f.Fan == Fan.ConcealedHand);

        // Drive the translator + JSON envelope serialisation.
        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var wireValue = SerializeHandResultToWire(entry);

        // ── Top-level result envelope ────────────────────────────────
        Assert.Equal("Hu", wireValue.GetProperty("type").GetString());
        Assert.Equal(0, wireValue.GetProperty("winner").GetInt32());

        // ── scoreResult must be a JSON object (not null) ────────────
        var scoreResult = wireValue.GetProperty("scoreResult");
        Assert.Equal(JsonValueKind.Object, scoreResult.ValueKind);
        Assert.Equal("bigWin", scoreResult.GetProperty("category").GetString());
        Assert.True(scoreResult.GetProperty("basePoints").GetInt32() > 0,
            "Wire scoreResult.basePoints must be positive after a real Hu.");

        // ── scoreResult.fans must be a populated JSON array ──────────
        var fans = scoreResult.GetProperty("fans");
        Assert.Equal(JsonValueKind.Array, fans.ValueKind);
        Assert.True(fans.GetArrayLength() >= 3,
            $"Expected >= 3 fans (selfDraw + sevenPairs + concealedHand) on wire. Got {fans.GetArrayLength()}.");

        // Each fan entry MUST carry the camelCase identifier AND the
        // localised labels — the frontend renders Chinese-first with
        // Pinyin / English as hover-tooltip and a11y backstop. An empty
        // label silently produces an empty chip in the result modal.
        var fanIds = new List<string>();
        var totalPointsObserved = 0;
        foreach (var f in fans.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, f.ValueKind);
            var id = f.GetProperty("fan").GetString();
            Assert.False(string.IsNullOrEmpty(id), "fan id must be camelCase string.");
            Assert.False(string.IsNullOrEmpty(f.GetProperty("chinese").GetString()),
                $"fan '{id}' missing Chinese label on wire.");
            Assert.False(string.IsNullOrEmpty(f.GetProperty("pinyin").GetString()),
                $"fan '{id}' missing Pinyin label on wire.");
            Assert.False(string.IsNullOrEmpty(f.GetProperty("english").GetString()),
                $"fan '{id}' missing English label on wire.");
            var pts = f.GetProperty("points").GetInt32();
            Assert.True(pts > 0, $"fan '{id}' must have positive points; got {pts}.");
            fanIds.Add(id!);
            totalPointsObserved += pts;
        }
        Assert.Contains("selfDraw", fanIds);
        Assert.Contains("sevenPairs", fanIds);
        Assert.Contains("concealedHand", fanIds);

        // ── fanPoints aggregate matches the per-fan sum ──────────────
        var fanPointsField = scoreResult.GetProperty("fanPoints").GetInt32();
        Assert.Equal(totalPointsObserved, fanPointsField);
        Assert.Equal(1 + 4 + 1, fanPointsField); // SelfDraw(1) + SevenPairs(4) + ConcealedHand(1)

        // ── score array (per-seat deltas) must be a non-empty array ──
        var scoreArr = wireValue.GetProperty("score");
        Assert.Equal(JsonValueKind.Array, scoreArr.ValueKind);
        Assert.Equal(4, scoreArr.GetArrayLength());
        // Winner's delta is positive.
        var winnerDelta = scoreArr[0].GetProperty("delta").GetInt32();
        Assert.True(winnerDelta > 0, $"Winner delta on wire must be > 0. Got {winnerDelta}.");

        // ── hand array (winning tiles) must be populated ─────────────
        var handArr = wireValue.GetProperty("hand");
        Assert.Equal(JsonValueKind.Array, handArr.ValueKind);
        Assert.Equal(14, handArr.GetArrayLength()); // 7 pairs × 2 tiles.

        // ── winResult mirror for the chip strip / banner ─────────────
        var winResult = wireValue.GetProperty("winResult");
        Assert.Equal(JsonValueKind.Object, winResult.ValueKind);
        Assert.True(winResult.GetProperty("isSelfDraw").GetBoolean());
        Assert.False(winResult.GetProperty("isRobbedKong").GetBoolean());
    }

    // ─────────────────────────────────────────────────────────────────
    //  2. Discard Hu → wire JSON omits the SelfDraw fan
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringLiveWiring"), Trait("Wave", "LiveWiring-Frost")]
    public void DiscardSevenPairsHu_FlowsThroughTranslator_EmitsFansWithoutSelfDraw()
    {
        var state = DriveDiscardSevenPairsByNonDealer();

        Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        Assert.NotNull(state.CurrentWin);
        Assert.False(state.CurrentWin!.IsSelfDraw);
        Assert.Equal(0, state.CurrentWin.SourceSeatIndex);

        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var wireValue = SerializeHandResultToWire(entry);

        Assert.Equal("Hu", wireValue.GetProperty("type").GetString());
        Assert.Equal(1, wireValue.GetProperty("winner").GetInt32());

        var scoreResult = wireValue.GetProperty("scoreResult");
        Assert.Equal(JsonValueKind.Object, scoreResult.ValueKind);

        var fans = scoreResult.GetProperty("fans");
        Assert.Equal(JsonValueKind.Array, fans.ValueKind);

        var fanIds = new HashSet<string>(StringComparer.Ordinal);
        var totalPointsObserved = 0;
        foreach (var f in fans.EnumerateArray())
        {
            var id = f.GetProperty("fan").GetString();
            Assert.False(string.IsNullOrEmpty(id));
            fanIds.Add(id!);
            totalPointsObserved += f.GetProperty("points").GetInt32();
        }

        // Discard wins MUST NOT carry the SelfDraw fan — the calculator
        // gates it on the IsSelfDraw flag, and the state machine has
        // already cleared that flag for a claim-resolution path.
        Assert.DoesNotContain("selfDraw", fanIds);
        Assert.Contains("sevenPairs", fanIds);
        Assert.Contains("concealedHand", fanIds);

        // fanPoints aggregate matches the per-fan sum.
        var fanPointsField = scoreResult.GetProperty("fanPoints").GetInt32();
        Assert.Equal(totalPointsObserved, fanPointsField);
        Assert.Equal(4 + 1, fanPointsField); // SevenPairs(4) + ConcealedHand(1)

        // WinResult side (chip strip) confirms the discard route.
        var winResult = wireValue.GetProperty("winResult");
        Assert.False(winResult.GetProperty("isSelfDraw").GetBoolean());
        Assert.Equal("discard", winResult.GetProperty("winType").GetString());
        Assert.Equal(0, winResult.GetProperty("sourceSeatIndex").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────
    //  3. Draw (no Hu) → wire JSON: scoreResult null, no fans surface
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringLiveWiring"), Trait("Wave", "LiveWiring-Frost")]
    public void DrawHand_NoWin_EmitsNullScoreResultAndAbsentFans()
    {
        // Build a fresh dealt state and DON'T drive any Hu. The translator's
        // contract for "no winner" is type=Draw + winResult=null +
        // scoreResult=null — which guarantees the frontend modal's
        // `result.scoreResult?.fans` chain short-circuits to undefined and
        // no spurious fan chips render.
        var state = BuildPostDealState(dealerSeat: 0);
        // CurrentWin / CurrentScore both null on a fresh post-deal state.
        Assert.Null(state.CurrentWin);
        Assert.Null(state.CurrentScore);

        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var wireValue = SerializeHandResultToWire(entry);

        // Draw contract: type=Draw, winner=-1, winResult/scoreResult
        // ABSENT from the wire (AutotableJson.Options sets
        // DefaultIgnoreCondition=WhenWritingNull, so null nested objects
        // serialise as missing properties — the frontend reads them via
        // optional-chaining (`result.scoreResult?.fans`) which is undefined
        // on missing, so no spurious fan chips render).
        Assert.Equal("Draw", wireValue.GetProperty("type").GetString());
        Assert.Equal(-1, wireValue.GetProperty("winner").GetInt32());
        Assert.False(wireValue.TryGetProperty("winResult", out _),
            "winResult should be omitted from the wire when there is no winner.");
        Assert.False(wireValue.TryGetProperty("scoreResult", out _),
            "scoreResult should be omitted from the wire when there is no winner — " +
            "guarantees `result.scoreResult?.fans` short-circuits to undefined.");

        // Score per-seat MUST still be an empty/zero array (frontend spread
        // semantic — see HandResultPayloadShapeTests). Hand array empty.
        var scoreArr = wireValue.GetProperty("score");
        Assert.Equal(JsonValueKind.Array, scoreArr.ValueKind);
        // All four seats are seeded with 0 in CumulativeScores by StartGame.
        Assert.Equal(4, scoreArr.GetArrayLength());
        foreach (var s in scoreArr.EnumerateArray())
            Assert.Equal(0, s.GetProperty("delta").GetInt32());

        var handArr = wireValue.GetProperty("hand");
        Assert.Equal(JsonValueKind.Array, handArr.ValueKind);
        Assert.Equal(0, handArr.GetArrayLength());
    }

    // ─────────────────────────────────────────────────────────────────
    //  4. Stacked-fan Hu (FullFlush + SelfDraw + ConcealedHand)
    //     → wire JSON carries every detected fan with correct points
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringLiveWiring"), Trait("Wave", "LiveWiring-Frost")]
    public void FullFlushSelfDrawHu_StackedFans_AllSurfaceOnWire()
    {
        // Dealer self-draws a 14-tile all-Wan hand: 4 chows + a pair, which
        // satisfies Standard (258-pair: Tong-2/5/8 → here Wan-2/5/8 — Wan-5
        // pair is 258-compliant since the pair is in suit Wan rank 5).
        // Concealed + self-draw + single-suit → FullFlush + SelfDraw + ConcealedHand.
        var state = BuildPostDealState(dealerSeat: 0);
        SuppressFirstDiscardContext(state);

        // Wan-1-2-3 / Wan-2-3-4 / Wan-5-6-7 / Wan-6-7-8 + Wan-5 pair.
        // 258 pair rule: pair must be rank 2/5/8 in non-honor suits.
        OverrideConcealedWith14(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 5), (Suit.Wan, 5));
        ClearOtherHands(state, keepSeat: 0);

        ChangshaGameStateMachine.DeclareSelfDrawWin(state, 0);
        ChangshaGameStateMachine.Score(state);

        Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        Assert.NotNull(state.CurrentScore);
        Assert.Contains(state.CurrentScore!.Fans, f => f.Fan == Fan.FullFlush);

        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var wireValue = SerializeHandResultToWire(entry);

        var scoreResult = wireValue.GetProperty("scoreResult");
        Assert.Equal(JsonValueKind.Object, scoreResult.ValueKind);
        var fans = scoreResult.GetProperty("fans");
        Assert.Equal(JsonValueKind.Array, fans.ValueKind);

        var fanMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in fans.EnumerateArray())
        {
            var id = f.GetProperty("fan").GetString()!;
            fanMap[id] = f.GetProperty("points").GetInt32();
        }

        // FullFlush(6) MUST surface on the wire — this is the big-ticket
        // fan that justifies the modal carrying fans at all.
        Assert.Contains("fullFlush", fanMap.Keys);
        Assert.Contains("selfDraw", fanMap.Keys);
        Assert.Contains("concealedHand", fanMap.Keys);
        Assert.Equal(6, fanMap["fullFlush"]);
        Assert.Equal(1, fanMap["selfDraw"]);
        Assert.Equal(1, fanMap["concealedHand"]);

        // fanPoints aggregate covers every detected fan.
        Assert.Equal(fanMap.Values.Sum(),
            scoreResult.GetProperty("fanPoints").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────
    //  5. Wire JSON `payments` carry per-fan rows the breakdown can read
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringLiveWiring"), Trait("Wave", "LiveWiring-Frost")]
    public void SelfDrawHu_PaymentsCarryFanReasonRowsOnWire()
    {
        // Same self-draw 7-pair shape — but this test asserts the
        // `payments` projection on the wire carries the per-fan rows
        // with `reason: "fan:<id>"`. Per #117 the fan bonus is folded into
        // payments ONLY under the opt-in house-rules mode (spec-pure keeps the
        // payments at the §5.1 magnitude and surfaces fans query-only), so this
        // wire-audit test drives ChangshaScoringOptions.HouseRules.
        var state = DriveSelfDrawSevenPairs(dealerSeat: 0, ChangshaScoringOptions.HouseRules);

        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var wireValue = SerializeHandResultToWire(entry);

        var payments = wireValue.GetProperty("scoreResult").GetProperty("payments");
        Assert.Equal(JsonValueKind.Array, payments.ValueKind);
        Assert.True(payments.GetArrayLength() > 0);

        // Tally the wire payments by reason prefix. There should be:
        //   - 3 base rows (one per opponent → winner), reason = "BigWin*"
        //   - 3 SelfDraw fan rows (1 pt × 3 opponents)
        //   - 3 SevenPairs fan rows (4 pt × 3 opponents)
        //   - 3 ConcealedHand fan rows (1 pt × 3 opponents)
        var fanRows = new List<(string reason, int amount)>();
        var baseRows = new List<(string reason, int amount)>();
        foreach (var p in payments.EnumerateArray())
        {
            var reason = p.GetProperty("reason").GetString() ?? string.Empty;
            var amount = p.GetProperty("amount").GetInt32();
            Assert.True(amount > 0,
                $"Wire payment amount must be > 0 on a real Hu; got {amount} ({reason}).");
            if (reason.StartsWith("fan:", StringComparison.Ordinal))
                fanRows.Add((reason, amount));
            else
                baseRows.Add((reason, amount));
        }

        Assert.Equal(3, baseRows.Count); // 3 opponents → winner.
        Assert.Equal(9, fanRows.Count);  // 3 fans × 3 opponents.

        Assert.Contains(fanRows, r => r.reason == "fan:selfDraw" && r.amount == 1);
        Assert.Contains(fanRows, r => r.reason == "fan:sevenPairs" && r.amount == 4);
        Assert.Contains(fanRows, r => r.reason == "fan:concealedHand" && r.amount == 1);

        // BasePoints = Σ amount across ALL rows (base + fan).
        var allRowsSum = baseRows.Sum(r => r.amount) + fanRows.Sum(r => r.amount);
        Assert.Equal(allRowsSum, wireValue.GetProperty("scoreResult")
            .GetProperty("basePoints").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────
    //  6. Wire identifier matches the catalog snake-canonical mapping
    // ─────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ScoringLiveWiring"), Trait("Wave", "LiveWiring-Frost")]
    public void LiveHu_WireFanIdentifiers_RoundTripThroughFanCatalog()
    {
        // Defends against silent renames of the camelCase wire id (e.g. if
        // a refactor changes `FanWireName` to PascalCase). The frontend
        // looks up the id in its own i18n catalog — a drift here breaks
        // every fan chip silently.
        var state = DriveSelfDrawSevenPairs(dealerSeat: 0);
        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var wireValue = SerializeHandResultToWire(entry);

        var fans = wireValue.GetProperty("scoreResult").GetProperty("fans");
        Assert.True(fans.GetArrayLength() > 0);

        // Reverse map: every wire id MUST decode to a known Fan enum value
        // AND its localised labels MUST match the catalog source of truth.
        foreach (var f in fans.EnumerateArray())
        {
            var wireId = f.GetProperty("fan").GetString()!;
            var matchingFan = Enum
                .GetValues<Fan>()
                .Cast<Fan?>()
                .FirstOrDefault(v => v.HasValue &&
                    ChangshaGameStateMachine.FanWireName(v.Value) == wireId);

            Assert.True(matchingFan.HasValue,
                $"Wire fan id '{wireId}' does not round-trip through FanWireName. " +
                "A refactor likely changed the enum-to-wire mapping — every fan " +
                "chip on the frontend will silently disappear if this drifts.");

            var info = FanCatalog.Get(matchingFan!.Value);
            Assert.Equal(info.Chinese, f.GetProperty("chinese").GetString());
            Assert.Equal(info.Pinyin, f.GetProperty("pinyin").GetString());
            Assert.Equal(info.English, f.GetProperty("english").GetString());
            Assert.Equal(info.Points, f.GetProperty("points").GetInt32());
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  State-machine harness — replicates HuToScoreToPersistenceTests
    //  helpers (kept private here so this file stands alone on review).
    // ─────────────────────────────────────────────────────────────────

    private static ChangshaGameState BuildPostDealState(int dealerSeat)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 42, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        state.ActiveSeatIndex = dealerSeat;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.MissedWinSeats.Clear();
        state.DiscardPile.Clear();
        state.TurnNumber = 1;
        return state;
    }

    private static void SuppressFirstDiscardContext(ChangshaGameState state)
    {
        // Plant a benign prior discard so HeavenlyHand (dealer's
        // first-action gate) doesn't fire and inflate the fan tally.
        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = (state.DealerSeatIndex + 1) % 4,
            TileId = Tid(Suit.Tiao, 8, 0),
            TurnNumber = 1,
        });
    }

    private static void OverrideConcealedWith14(ChangshaGameState state, int seatIndex,
        params (Suit suit, int rank)[] tiles)
    {
        var copies = new Dictionary<int, int>();
        var tileIds = new List<int>(tiles.Length);
        foreach (var (s, r) in tiles)
        {
            var logical = Logical(s, r);
            copies.TryGetValue(logical, out var copy);
            tileIds.Add(Tid(s, r, copy));
            copies[logical] = copy + 1;
        }
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    private static void ClearOtherHands(ChangshaGameState state, int keepSeat)
    {
        for (var i = 0; i < 4; i++)
        {
            if (i == keepSeat) continue;
            state.Hands[i].ConcealedTiles.Clear();
            state.Hands[i].Melds.Clear();
        }
    }
}
