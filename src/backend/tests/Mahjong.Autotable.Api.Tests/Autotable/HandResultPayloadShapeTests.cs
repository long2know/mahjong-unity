using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Regression suite pinning the wire shape of <see cref="HandResultEntry"/>.
///
/// The frontend (<c>game-ui.ts:renderResult</c>) does:
/// <code>
///   const ordered = [...(result.score ?? [])].sort((a, b) =&gt; a.seat - b.seat);
///   for (const tile of result.hand) { ... }
/// </code>
/// The <c>?? []</c> only catches <c>null</c> / <c>undefined</c>. If the backend
/// emits <c>score</c> as a JSON object (e.g. from <c>Dictionary&lt;int,int&gt;</c>)
/// or <c>hand</c> as a scalar, the spread / for-of throws
/// <c>TypeError: ... is not iterable</c>. Vasquez's 2026-05-29 integration audit
/// captured 6 such exceptions in 35 s of bot autoplay (scenario B).
///
/// These tests assert via <see cref="JsonSerializer"/> round-trip with
/// <see cref="AutotableJson.Options"/> (the same options used on the wire) that
/// <c>score</c> and <c>hand</c> are ALWAYS emitted as JSON arrays — empty in
/// the default / draw cases, populated in the Hu case.
/// </summary>
public class HandResultPayloadShapeTests
{
    private static JsonElement SerializeAndParse(HandResultEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── default-constructed instance is wire-safe ────────────────────────

    [Fact, Trait("Category", "PayloadShape")]
    public void DefaultHandResultEntry_Score_SerializesAsEmptyJsonArray()
    {
        var entry = new HandResultEntry();

        var root = SerializeAndParse(entry);

        Assert.Equal(JsonValueKind.Array, root.GetProperty("score").ValueKind);
        Assert.Equal(0, root.GetProperty("score").GetArrayLength());
    }

    [Fact, Trait("Category", "PayloadShape")]
    public void DefaultHandResultEntry_Hand_SerializesAsEmptyJsonArray()
    {
        var entry = new HandResultEntry();

        var root = SerializeAndParse(entry);

        Assert.Equal(JsonValueKind.Array, root.GetProperty("hand").ValueKind);
        Assert.Equal(0, root.GetProperty("hand").GetArrayLength());
    }

    // ── populated instance emits { seat, delta } entries ────────────────

    [Fact, Trait("Category", "PayloadShape")]
    public void PopulatedScore_SerializesAsArrayOfSeatDeltaObjects()
    {
        var entry = new HandResultEntry
        {
            Score =
            [
                new ScoreDeltaEntry { Seat = 0, Delta = 10 },
                new ScoreDeltaEntry { Seat = 1, Delta = -5 },
                new ScoreDeltaEntry { Seat = 2, Delta = -3 },
                new ScoreDeltaEntry { Seat = 3, Delta = -2 },
            ]
        };

        var root = SerializeAndParse(entry);
        var score = root.GetProperty("score");

        Assert.Equal(JsonValueKind.Array, score.ValueKind);
        Assert.Equal(4, score.GetArrayLength());

        foreach (var item in score.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, item.ValueKind);
            Assert.Equal(JsonValueKind.Number, item.GetProperty("seat").ValueKind);
            Assert.Equal(JsonValueKind.Number, item.GetProperty("delta").ValueKind);
        }

        Assert.Equal(10, score[0].GetProperty("delta").GetInt32());
        Assert.Equal(0, score[0].GetProperty("seat").GetInt32());
        Assert.Equal(-2, score[3].GetProperty("delta").GetInt32());
        Assert.Equal(3, score[3].GetProperty("seat").GetInt32());
    }

    [Fact, Trait("Category", "PayloadShape")]
    public void PopulatedHand_SerializesAsArrayOfNumbers()
    {
        var entry = new HandResultEntry
        {
            Hand = [0, 4, 8, 12, 16]
        };

        var root = SerializeAndParse(entry);
        var hand = root.GetProperty("hand");

        Assert.Equal(JsonValueKind.Array, hand.ValueKind);
        Assert.Equal(5, hand.GetArrayLength());
        foreach (var item in hand.EnumerateArray())
            Assert.Equal(JsonValueKind.Number, item.ValueKind);
    }

    // ── frontend spread / for-of semantic check (deserialize then iterate)

    [Fact, Trait("Category", "PayloadShape")]
    public void RoundTrip_ScoreAndHand_AreIterableLikeFrontendExpects()
    {
        // Mirrors `const ordered = [...(result.score ?? [])].sort(...)` and
        // `for (const tile of result.hand)` from game-ui.ts:renderResult.
        var entry = new HandResultEntry
        {
            Score =
            [
                new ScoreDeltaEntry { Seat = 2, Delta = -3 },
                new ScoreDeltaEntry { Seat = 0, Delta = 10 },
            ],
            Hand = [0, 4, 8]
        };

        var json = JsonSerializer.Serialize(entry, AutotableJson.Options);
        var roundTripped = JsonSerializer.Deserialize<HandResultEntry>(json, AutotableJson.Options);

        Assert.NotNull(roundTripped);

        // Spreading the deserialized List<ScoreDeltaEntry> must work (no TypeError analogue).
        var ordered = new List<ScoreDeltaEntry>(roundTripped!.Score);
        ordered.Sort((a, b) => a.Seat.CompareTo(b.Seat));
        Assert.Equal(2, ordered.Count);
        Assert.Equal(0, ordered[0].Seat);
        Assert.Equal(10, ordered[0].Delta);
        Assert.Equal(2, ordered[1].Seat);
        Assert.Equal(-3, ordered[1].Delta);

        var tiles = new List<int>();
        foreach (var t in roundTripped.Hand) tiles.Add(t);
        Assert.Equal(new[] { 0, 4, 8 }, tiles);
    }

    // ── translator path emits arrays end-to-end ─────────────────────────

    [Fact, Trait("Category", "PayloadShape")]
    public void BuildHandResult_FromFreshState_EmitsScoreArrayAndEmptyHand()
    {
        // Fresh post-deal state with no win declared — type should be "Draw" or "ZhaHu";
        // score MUST still be an array (the CumulativeScores dict is non-empty
        // because StartGame seeds 0 per seat). Hand should be empty (no winner).
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);

        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var root = SerializeAndParse(entry);

        var score = root.GetProperty("score");
        Assert.Equal(JsonValueKind.Array, score.ValueKind);
        Assert.Equal(4, score.GetArrayLength());

        // Ordered by seat ascending so consumers don't need to sort defensively.
        for (var i = 0; i < 4; i++)
            Assert.Equal(i, score[i].GetProperty("seat").GetInt32());

        Assert.Equal(JsonValueKind.Array, root.GetProperty("hand").ValueKind);
        Assert.Equal(0, root.GetProperty("hand").GetArrayLength());
    }

    [Fact, Trait("Category", "PayloadShape")]
    public void BuildHandResult_WithEmptyCumulativeScores_EmitsEmptyScoreArray()
    {
        // Constructed state with no seats populated in CumulativeScores — the
        // translator must still emit `score: []` (never `null`, never `{}`).
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        state.CumulativeScores.Clear();

        var entry = ChangshaToAutotableTranslator.BuildHandResult(state);
        var root = SerializeAndParse(entry);

        var score = root.GetProperty("score");
        Assert.Equal(JsonValueKind.Array, score.ValueKind);
        Assert.Equal(0, score.GetArrayLength());
    }

    // ── CollectionEntry envelope ────────────────────────────────────────

    [Fact, Trait("Category", "PayloadShape")]
    public void EncodeHandResult_AsCollectionEntry_PreservesArrayShape()
    {
        // The wire envelope is `["result", "current", {...}]`. We serialize the
        // full envelope to confirm the array shape survives the
        // CollectionEntryJsonConverter path (which calls
        // JsonSerializer.Serialize(value, value.GetType(), options) on the inner value).
        var entry = new HandResultEntry
        {
            Winner = 1,
            Type = "Hu",
            Score =
            [
                new ScoreDeltaEntry { Seat = 0, Delta = -2 },
                new ScoreDeltaEntry { Seat = 1, Delta = 6 },
                new ScoreDeltaEntry { Seat = 2, Delta = -2 },
                new ScoreDeltaEntry { Seat = 3, Delta = -2 },
            ],
            Hand = [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52],
            NextBanker = 1
        };
        var envelope = ChangshaCollectionEncoder.EncodeHandResult(entry);

        var json = JsonSerializer.Serialize(envelope, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;

        // [kind, key, value] triple.
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(3, arr.GetArrayLength());
        Assert.Equal("result", arr[0].GetString());
        Assert.Equal("current", arr[1].GetString());

        var value = arr[2];
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(JsonValueKind.Array, value.GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Array, value.GetProperty("hand").ValueKind);
        Assert.Equal(4, value.GetProperty("score").GetArrayLength());
        Assert.Equal(14, value.GetProperty("hand").GetArrayLength());
    }
}
