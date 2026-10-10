namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 6 — Bishop. DI seam for the eventual LLM-driven
/// play-by-play commentary surface. Wave 6 ships the contract +
/// the <see cref="StubCommentaryGenerator"/> default implementation
/// so the controller is testable end-to-end without an LLM
/// dependency. The real generator lands in Phase L behind the same
/// interface; the controller does not change.
///
/// <para>The generator returns a <see cref="CommentaryReplay"/>
/// envelope keyed by <see cref="CommentaryReplay.GameId"/>. The
/// stub always returns a single deterministic
/// <see cref="CommentaryItem"/> bearing the canonical "not yet
/// available" message so downstream UIs can render without
/// branching on null.</para>
/// </summary>
public interface ICommentaryGenerator
{
    /// <summary>Triggers commentary generation for
    /// <paramref name="gameId"/>. The Wave-6 stub returns
    /// synchronously; Phase L's implementation will likely enqueue a
    /// background job and return immediately with a "pending"
    /// status (the envelope shape is forward-compatible — the stub
    /// reports <c>status: "stub"</c>).</summary>
    Task<CommentaryReplay> GenerateAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>Returns the previously-generated commentary for
    /// <paramref name="gameId"/>, or a fresh stub envelope when no
    /// prior generation exists. The stub does not persist between
    /// requests — Phase L will store generated commentary in the
    /// audit table; today the surface is purely the contract
    /// pin.</summary>
    Task<CommentaryReplay> GetAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>
    /// Phase K Wave 7 — Bishop. Returns the per-turn
    /// <see cref="CommentaryRecord"/> list backing the
    /// <c>GET /api/games/{gameId}/commentary/replay</c> endpoint.
    /// This is the canonical Phase-L LLM contract: each record is a
    /// single speaker utterance tied to a specific game turn +
    /// phase, with structured emotion intensity + tile references
    /// for downstream UI rendering.
    /// <para>Distinct from <see cref="GetAsync"/> which returns the
    /// W6 envelope (one summary item per call) — the records path
    /// is the streaming-friendly shape Phase L emits one-record-per-
    /// notable-event. The stub returns a single placeholder record
    /// so the wire contract is verifiable today.</para>
    /// </summary>
    Task<IReadOnlyList<CommentaryRecord>> GetRecordsAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>Identifier of the active generator implementation.
    /// The stub returns <c>"stub"</c>; production implementations
    /// return their model identifier (e.g. <c>"gpt-4-mahjong-v1"</c>).
    /// Surfaced in the <see cref="CommentaryReplay.Generator"/> field.</summary>
    string GeneratorId { get; }
}

/// <summary>
/// Phase K Wave 6 — Bishop. Result envelope returned from
/// <see cref="ICommentaryGenerator.GenerateAsync"/> /
/// <see cref="ICommentaryGenerator.GetAsync"/>. The shape is
/// pinned by the Wave-6 contract test — the controller passes the
/// envelope through unchanged.
/// </summary>
public sealed record CommentaryReplay(
    Guid GameId,
    string Generator,
    string Status,
    IReadOnlyList<CommentaryItem> Items);

/// <summary>
/// Phase K Wave 6 — Bishop. Single commentary entry. The Wave-6
/// stub emits one of these per call; Phase L will emit one per
/// notable game event (riichi, win, dora kan, etc.).
/// </summary>
public sealed record CommentaryItem(
    int Sequence,
    string Text,
    int? RoundOrdinal,
    string Tone);

/// <summary>
/// Phase K Wave 7 — Bishop. Finalised JSON contract for the
/// Phase-L LLM-driven commentary records. One record per speaker
/// utterance tied to a specific game turn + phase. The fields are
/// pinned by <c>Phase_K_W7/Bishop/CommentaryRecordContractTests</c>;
/// downstream LLM generators MUST emit records that round-trip
/// through this exact shape.
///
/// <list type="bullet">
///   <item><b>GameId</b> — opaque string id (matches the persistence
///         column shape; Phase L emitter may use the
///         <c>Guid.ToString("N")</c> form or an alternative if the
///         backing store changes).</item>
///   <item><b>TurnNumber</b> — 1-based turn number inside the game.
///         Zero for pre-deal commentary; negative values are
///         reserved (and rejected by the contract test).</item>
///   <item><b>Phase</b> — one of <c>"draw"</c>, <c>"discard"</c>,
///         <c>"claim"</c>, or <c>"win"</c>. Other values fail the
///         contract — downstream UI dispatch keys on this field.</item>
///   <item><b>Speaker</b> — one of <c>"play-by-play"</c>,
///         <c>"color"</c>, or <c>"analyst"</c>. Three personas keep
///         the broadcast tone varied without an unbounded namespace.</item>
///   <item><b>Text</b> — the utterance text. Plain UTF-8; no
///         HTML/Markdown.</item>
///   <item><b>EmotionIntensity</b> — 0.0..1.0 inclusive. Downstream
///         renderers map this to font weight, voice tempo, or color
///         saturation. Values outside [0,1] fail the contract.</item>
///   <item><b>TileReferences</b> — typed tile references for the
///         tiles the <see cref="Text"/> mentions (e.g.
///         <c>{ TileId="man5", Suit="man", Rank=5 }</c>). Empty
///         list when the utterance carries no tile reference;
///         never null. Phase K Wave 10 promoted the field from
///         a bare <c>string[]</c> to the typed
///         <see cref="TileReference"/> shape so the wire carries
///         the parsed suit / rank alongside the wire id — this
///         removes a parser round-trip in the renderer and
///         catches malformed ids at generation time rather than
///         at render time.</item>
///   <item><b>GeneratedAt</b> — UTC timestamp the record was minted.
///         <see cref="DateTimeOffset"/> so the offset is wire-visible
///         and replay tooling can sort across generator runs.</item>
/// </list>
/// </summary>
public sealed record CommentaryRecord(
    string GameId,
    int TurnNumber,
    string Phase,
    string Speaker,
    string Text,
    double EmotionIntensity,
    IReadOnlyList<TileReference> TileReferences,
    DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// Phase K Wave 11 — Bishop. Bit-packed binary projection of
    /// <see cref="TileReferences"/>. Each entry is the 3-byte
    /// encoding produced by <see cref="TileReference.ToBinary"/>;
    /// on the wire each byte array is base64-encoded so the field
    /// survives the JSON pipeline. The projection is lazy — the
    /// list is materialised on first access and re-derived on
    /// every read (the record is immutable so caching adds no
    /// value; the codec is cheap).
    ///
    /// <para>Bandwidth-sensitive consumers (mobile spectator
    /// stream, the commentary replay endpoint when paginated) can
    /// read the binary field instead of the typed
    /// <see cref="TileReferences"/> list — a 3-byte payload per
    /// tile vs ~30 bytes per JSON object.</para>
    /// </summary>
    public IReadOnlyList<byte[]> TileReferencesBinary =>
        TileReferences is null
            ? Array.Empty<byte[]>()
            : TileReferences.Select(t => t.ToBinary()).ToArray();
}

/// <summary>
/// Phase K Wave 10 — Bishop. Typed tile reference attached to a
/// <see cref="CommentaryRecord"/>. The wire id matches the
/// canonical mahjong tile vocabulary (<c>man1..man9</c>,
/// <c>pin1..pin9</c>, <c>sou1..sou9</c>, plus the honor tiles
/// <c>east|south|west|north|haku|hatsu|chun</c>).
///
/// <list type="bullet">
///   <item><b>TileId</b> — the canonical wire id (<c>"man5"</c>,
///         <c>"chun"</c>). Empty string when the parser couldn't
///         identify a tile.</item>
///   <item><b>Suit</b> — one of <c>"man"</c>, <c>"pin"</c>,
///         <c>"sou"</c>, <c>"wind"</c>, <c>"dragon"</c>, or
///         <c>"unknown"</c>.</item>
///   <item><b>Rank</b> — 1..9 for suit tiles; 0 for honor tiles
///         (callers key on <see cref="TileId"/> for honors).</item>
/// </list>
/// </summary>
public sealed record TileReference(string TileId, string Suit, int Rank)
{
    /// <summary>The "unknown" sentinel returned by
    /// <see cref="Parse"/> when the wire id cannot be classified.
    /// The sentinel is never null so callers can safely render the
    /// tileId without a separate null check.</summary>
    public static readonly TileReference Unknown =
        new(TileId: string.Empty, Suit: "unknown", Rank: 0);

    private static readonly HashSet<string> Winds =
        new(StringComparer.OrdinalIgnoreCase) { "east", "south", "west", "north" };
    private static readonly HashSet<string> Dragons =
        new(StringComparer.OrdinalIgnoreCase) { "haku", "hatsu", "chun" };

    /// <summary>
    /// Phase K Wave 10 — Bishop. Parse a wire-name tile id (e.g.
    /// <c>"man5"</c>, <c>"chun"</c>) into its typed
    /// <see cref="TileReference"/> shape. Unknown / malformed
    /// ids return <see cref="Unknown"/> instead of throwing —
    /// the generator must never crash on a stray reference.
    /// </summary>
    public static TileReference Parse(string? wireName)
    {
        if (string.IsNullOrWhiteSpace(wireName)) return Unknown;
        var trimmed = wireName.Trim();
        // Suit tiles: "man5", "pin3", "sou9".
        if (trimmed.Length >= 4)
        {
            var suitPrefix = trimmed.Substring(0, 3).ToLowerInvariant();
            if (suitPrefix is "man" or "pin" or "sou")
            {
                if (int.TryParse(trimmed.AsSpan(3), out var rank) && rank is >= 1 and <= 9)
                {
                    return new TileReference(
                        TileId: suitPrefix + rank,
                        Suit: suitPrefix,
                        Rank: rank);
                }
            }
        }
        if (Winds.Contains(trimmed))
        {
            return new TileReference(
                TileId: trimmed.ToLowerInvariant(),
                Suit: "wind",
                Rank: 0);
        }
        if (Dragons.Contains(trimmed))
        {
            return new TileReference(
                TileId: trimmed.ToLowerInvariant(),
                Suit: "dragon",
                Rank: 0);
        }
        return Unknown;
    }

    // ── Phase K Wave 11 — Bishop. Binary codec.
    //
    // The wire shape is 3 bytes:
    //   byte 0 = (suit << 4) | (rank & 0x0F)
    //   byte 1 = flags (red-five, etc. — reserved, currently 0)
    //   byte 2 = reserved (future-proof — generator version, etc.;
    //            currently 0).
    //
    // Suit codes (4 bits, 0–3 only used; 4–15 reserved):
    //   0 = dots / pin
    //   1 = bamboo / sou
    //   2 = characters / man
    //   3 = honors (winds + dragons)
    //
    // Rank codes (4 bits): for the three suits, 1–9 maps directly
    // to the tile rank (zero is invalid). For honors:
    //   0 = east, 1 = south, 2 = west, 3 = north,
    //   4 = haku  (white dragon),
    //   5 = hatsu (green dragon),
    //   6 = chun  (red   dragon),
    //   7..15 reserved.
    //
    // The Unknown sentinel encodes as {0xFF, 0x00, 0x00} — a value
    // no legal tile can produce (suit 0xF + rank 0xF) so the
    // decoder can detect a passthrough without ambiguity.

    /// <summary>Phase K Wave 11 — Bishop. Length of the binary
    /// encoding in bytes. Pinned as a constant so callers building
    /// fixed-stride arrays of <see cref="TileReference"/> values
    /// don't hard-code the literal.</summary>
    public const int BinaryLength = 3;

    private const byte SuitCodeDots = 0;
    private const byte SuitCodeBamboo = 1;
    private const byte SuitCodeCharacters = 2;
    private const byte SuitCodeHonors = 3;
    private const byte SuitCodeUnknown = 0xF;

    private const byte HonorEast = 0;
    private const byte HonorSouth = 1;
    private const byte HonorWest = 2;
    private const byte HonorNorth = 3;
    private const byte HonorHaku = 4;
    private const byte HonorHatsu = 5;
    private const byte HonorChun = 6;

    /// <summary>
    /// Phase K Wave 11 — Bishop. Bit-packed binary representation
    /// of this tile reference. Three bytes; see the codec comment
    /// at the top of the binary block for the wire layout.
    ///
    /// <para>Used by <see cref="CommentaryRecord.TileReferencesBinary"/>
    /// (the W11 binary-side of the existing
    /// <see cref="CommentaryRecord.TileReferences"/> list) so the
    /// LLM-generated tile lists ride on a compact byte payload
    /// when commentary records are streamed through bandwidth-
    /// sensitive channels (e.g., the mobile-spectator path).</para>
    /// </summary>
    public byte[] ToBinary()
    {
        var bytes = new byte[BinaryLength];
        var suitCode = Suit switch
        {
            "pin" => SuitCodeDots,
            "sou" => SuitCodeBamboo,
            "man" => SuitCodeCharacters,
            "wind" => SuitCodeHonors,
            "dragon" => SuitCodeHonors,
            _ => SuitCodeUnknown,
        };

        byte rankCode;
        if (suitCode is SuitCodeDots or SuitCodeBamboo or SuitCodeCharacters)
        {
            // Rank 1..9 — clamp to keep the codec total: a malformed
            // out-of-range rank encodes as 0xF (reserved) so the
            // decoder round-trips to Unknown.
            rankCode = Rank is >= 1 and <= 9 ? (byte)Rank : (byte)0xF;
        }
        else if (suitCode == SuitCodeHonors)
        {
            rankCode = TileId switch
            {
                "east" => HonorEast,
                "south" => HonorSouth,
                "west" => HonorWest,
                "north" => HonorNorth,
                "haku" => HonorHaku,
                "hatsu" => HonorHatsu,
                "chun" => HonorChun,
                _ => 0xF,
            };
        }
        else
        {
            rankCode = 0xF;
        }

        bytes[0] = (byte)(((suitCode & 0x0F) << 4) | (rankCode & 0x0F));
        bytes[1] = 0; // reserved — red-five flag etc.
        bytes[2] = 0; // reserved — generator-version etc.
        return bytes;
    }

    /// <summary>
    /// Phase K Wave 11 — Bishop. Parse a 3-byte binary tile
    /// reference. Returns <see cref="Unknown"/> on null input,
    /// wrong length, or any decoded value that doesn't match a
    /// canonical tile (the reserved code-points round-trip to
    /// Unknown so an unknown sender doesn't crash the receiver).
    /// </summary>
    public static TileReference FromBinary(byte[]? bytes)
    {
        if (bytes is null || bytes.Length != BinaryLength) return Unknown;
        var suitCode = (byte)((bytes[0] >> 4) & 0x0F);
        var rankCode = (byte)(bytes[0] & 0x0F);

        return suitCode switch
        {
            SuitCodeDots when rankCode is >= 1 and <= 9 =>
                new TileReference("pin" + rankCode, "pin", rankCode),
            SuitCodeBamboo when rankCode is >= 1 and <= 9 =>
                new TileReference("sou" + rankCode, "sou", rankCode),
            SuitCodeCharacters when rankCode is >= 1 and <= 9 =>
                new TileReference("man" + rankCode, "man", rankCode),
            SuitCodeHonors => DecodeHonor(rankCode),
            _ => Unknown,
        };
    }

    private static TileReference DecodeHonor(byte rankCode) => rankCode switch
    {
        HonorEast => new TileReference("east", "wind", 0),
        HonorSouth => new TileReference("south", "wind", 0),
        HonorWest => new TileReference("west", "wind", 0),
        HonorNorth => new TileReference("north", "wind", 0),
        HonorHaku => new TileReference("haku", "dragon", 0),
        HonorHatsu => new TileReference("hatsu", "dragon", 0),
        HonorChun => new TileReference("chun", "dragon", 0),
        _ => Unknown,
    };
}

/// <summary>
/// Phase K Wave 7 — Bishop. Canonical wire-string vocabulary for
/// <see cref="CommentaryRecord.Phase"/>. Contract tests pin the set
/// so a typo in a downstream emitter is caught at validation time
/// rather than silently producing unrenderable records.
/// </summary>
public static class CommentaryPhases
{
    public const string Draw = "draw";
    public const string Discard = "discard";
    public const string Claim = "claim";
    public const string Win = "win";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Draw, Discard, Claim, Win,
    };
}

/// <summary>
/// Phase K Wave 7 — Bishop. Canonical wire-string vocabulary for
/// <see cref="CommentaryRecord.Speaker"/>.
/// </summary>
public static class CommentarySpeakers
{
    public const string PlayByPlay = "play-by-play";
    public const string Color = "color";
    public const string Analyst = "analyst";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        PlayByPlay, Color, Analyst,
    };
}
