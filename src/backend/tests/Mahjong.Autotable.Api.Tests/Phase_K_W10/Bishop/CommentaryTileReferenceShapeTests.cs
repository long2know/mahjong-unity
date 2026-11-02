using Mahjong.Autotable.Api.Commentary;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Typed tile-reference contract.
/// W9 carried <see cref="CommentaryRecord.TileReferences"/> as a
/// bare <c>string[]</c>; W10 promotes it to a typed
/// <see cref="TileReference"/> shape so the wire carries the
/// parsed suit / rank alongside the wire id.
///
/// <list type="number">
///   <item>The <see cref="TileReference"/> record exists with
///         <c>TileId</c>, <c>Suit</c>, and <c>Rank</c>
///         properties.</item>
///   <item><see cref="TileReference.Parse(string)"/> decodes
///         <c>"man5"</c> → suit=<c>"man"</c>, rank=<c>5</c>.</item>
///   <item><see cref="TileReference.Parse(string)"/> decodes
///         every numeric suit (man/pin/sou) over the 1..9
///         range.</item>
///   <item>Honor tiles (winds + dragons) parse to suit
///         <c>"wind"</c> / <c>"dragon"</c> with rank 0 and the
///         honor name as <see cref="TileReference.TileId"/>.</item>
///   <item>Malformed ids (<c>"foo"</c>, <c>"man0"</c>, empty)
///         collapse to <see cref="TileReference.Unknown"/>
///         instead of throwing.</item>
///   <item><see cref="CommentaryRecord.TileReferences"/> has the
///         new typed shape — the property type is
///         <c>IReadOnlyList&lt;TileReference&gt;</c>.</item>
///   <item><see cref="TileReference.Unknown"/> sentinel is
///         publicly accessible so callers can compare via
///         reference equality.</item>
/// </list>
/// </summary>
public sealed class CommentaryTileReferenceShapeTests
{
    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void TileReference_HasTileId_Suit_Rank()
    {
        var t = new TileReference("man5", "man", 5);
        Assert.Equal("man5", t.TileId);
        Assert.Equal("man", t.Suit);
        Assert.Equal(5, t.Rank);
    }

    [Theory, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    [InlineData("man5", "man", 5)]
    [InlineData("pin3", "pin", 3)]
    [InlineData("sou9", "sou", 9)]
    [InlineData("MAN1", "man", 1)]
    public void Parse_DecodesSuitTiles(string wire, string expectedSuit, int expectedRank)
    {
        var t = TileReference.Parse(wire);
        Assert.Equal(expectedSuit, t.Suit);
        Assert.Equal(expectedRank, t.Rank);
        Assert.Equal(expectedSuit + expectedRank, t.TileId);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void Parse_DecodesAllSuitTiles_OneThroughNine()
    {
        foreach (var suit in new[] { "man", "pin", "sou" })
        {
            for (var rank = 1; rank <= 9; rank++)
            {
                var t = TileReference.Parse($"{suit}{rank}");
                Assert.Equal(suit, t.Suit);
                Assert.Equal(rank, t.Rank);
            }
        }
    }

    [Theory, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    [InlineData("east", "wind")]
    [InlineData("south", "wind")]
    [InlineData("west", "wind")]
    [InlineData("north", "wind")]
    [InlineData("haku", "dragon")]
    [InlineData("hatsu", "dragon")]
    [InlineData("chun", "dragon")]
    public void Parse_DecodesHonorTiles(string wire, string expectedSuit)
    {
        var t = TileReference.Parse(wire);
        Assert.Equal(expectedSuit, t.Suit);
        Assert.Equal(0, t.Rank);
        Assert.Equal(wire.ToLowerInvariant(), t.TileId);
    }

    [Theory, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("foo")]
    [InlineData("man0")]
    [InlineData("man10")]
    [InlineData("xyz5")]
    [InlineData(null)]
    public void Parse_MalformedIds_ReturnUnknown(string? wire)
    {
        var t = TileReference.Parse(wire!);
        Assert.Same(TileReference.Unknown, t);
        Assert.Equal("unknown", t.Suit);
        Assert.Equal(0, t.Rank);
        Assert.Equal(string.Empty, t.TileId);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryRecord_TileReferences_HasTypedShape()
    {
        var prop = typeof(CommentaryRecord).GetProperty("TileReferences");
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyList<TileReference>), prop!.PropertyType);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void Unknown_Sentinel_IsReferenceStable()
    {
        Assert.Same(TileReference.Unknown, TileReference.Parse(null));
        Assert.Same(TileReference.Unknown, TileReference.Parse(""));
        Assert.Same(TileReference.Unknown, TileReference.Parse("nonsense"));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryRecord_CanBeConstructedWithTypedReferences()
    {
        var record = new CommentaryRecord(
            GameId: Guid.NewGuid().ToString("N"),
            TurnNumber: 1,
            Phase: CommentaryPhases.Discard,
            Speaker: CommentarySpeakers.PlayByPlay,
            Text: "Discards man5.",
            EmotionIntensity: 0.5,
            TileReferences: new[] { TileReference.Parse("man5") },
            GeneratedAt: DateTimeOffset.UtcNow);
        Assert.Single(record.TileReferences);
        Assert.Equal("man", record.TileReferences[0].Suit);
    }
}
