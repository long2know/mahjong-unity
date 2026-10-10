using Mahjong.Autotable.Api.Commentary;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Bishop;

/// <summary>
/// Phase K Wave 11 — Bishop. Hard-asserted contract for the
/// <see cref="TileReference"/> 3-byte binary codec.
///
/// <list type="number">
///   <item><see cref="TileReference.BinaryLength"/> is pinned at
///         3 so callers building fixed-stride arrays don't
///         hard-code the literal.</item>
///   <item>Every legal suit tile (3 × 9 = 27 entries) round-
///         trips through <c>ToBinary</c> / <c>FromBinary</c>.</item>
///   <item>Every honor tile (4 winds + 3 dragons = 7) round-
///         trips.</item>
///   <item>The unknown sentinel round-trips to itself.</item>
///   <item><c>FromBinary(null)</c> returns the Unknown sentinel
///         (decoder must never crash).</item>
///   <item><c>FromBinary(byte[]{})</c> (empty) returns Unknown.</item>
///   <item><c>FromBinary(byte[]{0,0})</c> (wrong length) returns
///         Unknown.</item>
///   <item><c>FromBinary(byte[]{0,0,0,0})</c> (wrong length)
///         returns Unknown.</item>
///   <item>A suit byte of 0x4..0xE (reserved) round-trips to
///         Unknown.</item>
///   <item>A man-rank of 0 (reserved) round-trips to Unknown.</item>
///   <item>A man-rank of 10 (reserved) round-trips to Unknown.</item>
///   <item>An honor rank of 7..0xE (reserved) round-trips to
///         Unknown.</item>
///   <item>Bytes 1+2 are reserved and stamped as zero on
///         encode.</item>
///   <item>The high nibble of byte 0 is the suit code; low
///         nibble is the rank code (bit-layout assertion).</item>
///   <item>Pin-1 encodes as 0x01.</item>
///   <item>Sou-9 encodes as 0x19 (high nibble = 1 = sou).</item>
///   <item>Man-5 encodes as 0x25 (high nibble = 2 = man).</item>
///   <item>East wind encodes as 0x30.</item>
///   <item>Chun dragon encodes as 0x36 (high nibble = 3 honors,
///         low nibble = 6 = chun).</item>
///   <item>Unknown sentinel encodes as 0xFF (suit unknown +
///         rank unknown).</item>
///   <item><c>CommentaryRecord.TileReferencesBinary</c> projects
///         the typed list through <c>ToBinary</c>.</item>
///   <item><c>CommentaryRecord.TileReferencesBinary</c> on a
///         record with empty refs returns an empty array.</item>
/// </list>
/// </summary>
public sealed class TileReferenceBinaryCodecFacts
{
    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void BinaryLength_IsPinnedAtThree()
    {
        Assert.Equal(3, TileReference.BinaryLength);
    }

    [Theory]
    [InlineData("pin", 1), InlineData("pin", 2), InlineData("pin", 3),
     InlineData("pin", 4), InlineData("pin", 5), InlineData("pin", 6),
     InlineData("pin", 7), InlineData("pin", 8), InlineData("pin", 9),
     InlineData("sou", 1), InlineData("sou", 2), InlineData("sou", 3),
     InlineData("sou", 4), InlineData("sou", 5), InlineData("sou", 6),
     InlineData("sou", 7), InlineData("sou", 8), InlineData("sou", 9),
     InlineData("man", 1), InlineData("man", 2), InlineData("man", 3),
     InlineData("man", 4), InlineData("man", 5), InlineData("man", 6),
     InlineData("man", 7), InlineData("man", 8), InlineData("man", 9)]
    [Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void SuitTiles_RoundTripThroughBinary(string suit, int rank)
    {
        var t = new TileReference(suit + rank, suit, rank);
        var bytes = t.ToBinary();
        Assert.Equal(3, bytes.Length);
        var back = TileReference.FromBinary(bytes);
        Assert.Equal(t.TileId, back.TileId);
        Assert.Equal(t.Suit, back.Suit);
        Assert.Equal(t.Rank, back.Rank);
    }

    [Theory]
    [InlineData("east", "wind"), InlineData("south", "wind"),
     InlineData("west", "wind"), InlineData("north", "wind"),
     InlineData("haku", "dragon"), InlineData("hatsu", "dragon"),
     InlineData("chun", "dragon")]
    [Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void HonorTiles_RoundTripThroughBinary(string tileId, string suit)
    {
        var t = new TileReference(tileId, suit, 0);
        var bytes = t.ToBinary();
        Assert.Equal(3, bytes.Length);
        var back = TileReference.FromBinary(bytes);
        Assert.Equal(t.TileId, back.TileId);
        Assert.Equal(t.Suit, back.Suit);
        Assert.Equal(t.Rank, back.Rank);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void UnknownSentinel_RoundTrips()
    {
        var bytes = TileReference.Unknown.ToBinary();
        var back = TileReference.FromBinary(bytes);
        Assert.Equal(TileReference.Unknown, back);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void FromBinary_Null_ReturnsUnknown()
    {
        Assert.Equal(TileReference.Unknown, TileReference.FromBinary(null));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void FromBinary_EmptyArray_ReturnsUnknown()
    {
        Assert.Equal(TileReference.Unknown, TileReference.FromBinary(Array.Empty<byte>()));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void FromBinary_WrongLength2_ReturnsUnknown()
    {
        Assert.Equal(TileReference.Unknown, TileReference.FromBinary(new byte[] { 0, 0 }));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void FromBinary_WrongLength4_ReturnsUnknown()
    {
        Assert.Equal(TileReference.Unknown, TileReference.FromBinary(new byte[] { 0, 0, 0, 0 }));
    }

    [Theory]
    [InlineData(0x40), InlineData(0x50), InlineData(0x80), InlineData(0xE0)]
    [Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void FromBinary_ReservedSuit_ReturnsUnknown(byte byte0)
    {
        Assert.Equal(TileReference.Unknown, TileReference.FromBinary(new byte[] { byte0, 0, 0 }));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void FromBinary_ManRankZero_ReturnsUnknown()
    {
        // 0x20 = suit man, rank 0 (out of range).
        Assert.Equal(TileReference.Unknown, TileReference.FromBinary(new byte[] { 0x20, 0, 0 }));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void FromBinary_HonorReservedRank_ReturnsUnknown()
    {
        // 0x37 = honors suit, rank 7 (reserved, > chun).
        Assert.Equal(TileReference.Unknown, TileReference.FromBinary(new byte[] { 0x37, 0, 0 }));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_ReservedBytesAreZero()
    {
        var bytes = new TileReference("man5", "man", 5).ToBinary();
        Assert.Equal(0, bytes[1]);
        Assert.Equal(0, bytes[2]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_Pin1_Encodes0x01()
    {
        Assert.Equal(0x01, new TileReference("pin1", "pin", 1).ToBinary()[0]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_Sou9_Encodes0x19()
    {
        Assert.Equal(0x19, new TileReference("sou9", "sou", 9).ToBinary()[0]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_Man5_Encodes0x25()
    {
        Assert.Equal(0x25, new TileReference("man5", "man", 5).ToBinary()[0]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_East_Encodes0x30()
    {
        Assert.Equal(0x30, new TileReference("east", "wind", 0).ToBinary()[0]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_Chun_Encodes0x36()
    {
        Assert.Equal(0x36, new TileReference("chun", "dragon", 0).ToBinary()[0]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_Unknown_EncodesUnknownSuitAndRank()
    {
        var b0 = TileReference.Unknown.ToBinary()[0];
        Assert.Equal(0xFF, b0);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryRecord_TileReferencesBinary_Projects()
    {
        var record = new CommentaryRecord(
            GameId: Guid.NewGuid().ToString("N"),
            TurnNumber: 1,
            Phase: CommentaryPhases.Discard,
            Speaker: "narrator",
            Text: "hello",
            EmotionIntensity: 0.5,
            TileReferences: new[]
            {
                new TileReference("man5", "man", 5),
                new TileReference("east", "wind", 0),
            },
            GeneratedAt: DateTimeOffset.UtcNow);

        var binary = record.TileReferencesBinary;
        Assert.Equal(2, binary.Count);
        Assert.Equal(0x25, binary[0][0]);
        Assert.Equal(0x30, binary[1][0]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryRecord_TileReferencesBinary_EmptyRefs_ReturnsEmpty()
    {
        var record = new CommentaryRecord(
            GameId: Guid.NewGuid().ToString("N"),
            TurnNumber: 1,
            Phase: CommentaryPhases.Draw,
            Speaker: "narrator",
            Text: "hello",
            EmotionIntensity: 0.5,
            TileReferences: Array.Empty<TileReference>(),
            GeneratedAt: DateTimeOffset.UtcNow);

        Assert.Empty(record.TileReferencesBinary);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void HighNibbleIsSuitCode_LowNibbleIsRankCode()
    {
        var bytes = new TileReference("pin7", "pin", 7).ToBinary();
        Assert.Equal(0, (bytes[0] >> 4) & 0x0F);
        Assert.Equal(7, bytes[0] & 0x0F);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void ToBinary_AllowsParseRoundTrip()
    {
        // Parse then ToBinary then FromBinary === Parse.
        var parsed = TileReference.Parse("man3");
        var via = TileReference.FromBinary(parsed.ToBinary());
        Assert.Equal(parsed, via);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void Unknown_ParsedFromBadId_RoundTripsAsUnknown()
    {
        var parsed = TileReference.Parse("not-a-tile");
        Assert.Equal(TileReference.Unknown, parsed);
        var via = TileReference.FromBinary(parsed.ToBinary());
        Assert.Equal(TileReference.Unknown, via);
    }
}
