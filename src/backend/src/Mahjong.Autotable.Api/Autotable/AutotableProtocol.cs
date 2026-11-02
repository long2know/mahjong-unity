using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Autotable.Api.Autotable;

/// <summary>
/// Envelope record types for the upstream pwmarcz/autotable WebSocket protocol.
/// Mirrors <c>server/protocol.ts</c> from upstream so the byte-identical
/// <c>autotable.9519e86d.js</c> bundle connects unchanged.
/// </summary>
public enum AutotableMessageKind
{
    NEW,
    JOIN,
    JOINED,
    UPDATE
}

/// <summary>
/// A single collection mutation. Serialized as the three-element JSON array
/// <c>[kind, key, value]</c> per upstream <c>protocol.ts</c>.
/// <para>Examples:</para>
/// <list type="bullet">
/// <item><c>["things", 42, { slotName: "hand.0@0", ... }]</c></item>
/// <item><c>["match", 0, { dealer: 0, honba: 0, conditions: { ... } }]</c></item>
/// <item><c>["dice", 0, { dice: [3, 4], state: "rolled" }]</c></item>
/// </list>
/// </summary>
[JsonConverter(typeof(CollectionEntryJsonConverter))]
public sealed record CollectionEntry(string Kind, object Key, object? Value);

/// <summary>Inbound envelope from the bundle. Discriminated by <see cref="Type"/>.</summary>
public sealed class AutotableInboundMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("gameId")]
    public string? GameId { get; set; }

    [JsonPropertyName("entries")]
    public List<CollectionEntry>? Entries { get; set; }

    [JsonPropertyName("full")]
    public bool? Full { get; set; }
}

/// <summary>
/// Changsha-specific collection kinds layered on top of the upstream autotable protocol.
/// These reuse the same <c>UPDATE { entries: [[kind, key, value], …] }</c> envelope —
/// no new message types — and live alongside upstream collections (<c>things</c>,
/// <c>seats</c>, <c>nicks</c>, <c>match</c>, <c>mouse</c>, <c>sound</c>, <c>dice</c>).
///
/// <list type="bullet">
///   <item><b><c>claim</c></b> (Bishop Phase D-backend §2): keyed by seat index (0..3),
///   value = <c>{ available: ["Pung","Chow","Kong","Hu"], deadline: long, source: int,
///   tile: int }</c>. Server-emitted when a discard opens a claim window for that seat;
///   client-emitted when a player presses a claim button (or "pass"). Cleared with a
///   tombstone (value=<c>null</c>) when the window closes.</item>
///
///   <item><b><c>result</c></b> (Bishop Phase D-backend §2): keyed by the literal string
///   <c>"current"</c>, value = <c>{ winner: int, type: "Hu"|"Draw"|"ZhaHu",
///   score: { seat: int, delta: int }[], hand: int[], nextBanker: int }</c>.
///   Server-emitted on hand end (Hu, washout, or false-Hu penalty). Used by the
///   autotable scene to drive the result panel + banker arrow rotation. Lives until
///   the next deal clears it. Note: <c>score</c> is an ARRAY of seat/delta entries
///   (matches the frontend <c>ScoreDelta</c> interface in <c>types.ts</c>) — emitting
///   it as a JSON object trips <c>TypeError: ... is not iterable</c> in the
///   <c>game-ui.ts:renderResult</c> spread.</item>
///
///   <item><b><c>pickup</c></b> (Bishop Phase F §2): keyed by the literal string
///   <c>"current"</c>, value = <c>{ phase: string, seatIndex: int, count: int,
///   dealMode: "auto"|"manual", breakPoint: {wallIndex, stackIndex, tileIndex},
///   wallIndex: int }</c>. Server-emitted whenever the active pickup phase or cursor
///   advances (BreakPointMarked, PickupRound1..3, SingleTilePickup, DealerExtra).
///   Tombstoned (value=<c>null</c>) when phase transitions to <c>AwaitingDiscard</c>
///   (manual deal complete). Drives the autotable scene's "Take Tiles" affordance and
///   bot pickup animation.</item>
/// </list>
/// </summary>
public static class ChangshaCollectionKinds
{
    public const string Claim = "claim";
    public const string Result = "result";
    public const string HandResultAck = "handResultAck";
    public const string Pickup = "pickup";

    /// <summary>
    /// Server-emitted only (inbound to clients): the authoritative "whose turn / must a seat
    /// discard" signal. Singleton keyed <c>"current"</c>, value = <see cref="TurnEntry"/>
    /// (<c>{ activeSeat: 0..3 | null, phase: string, awaitingDiscard: bool }</c>). Emitted by
    /// <see cref="ChangshaToAutotableTranslator"/> on every snapshot:
    /// <list type="bullet">
    ///   <item>While <see cref="Mahjong.Autotable.Api.Changsha.ChangshaPhase.AwaitingDiscard"/>
    ///   holds → <c>activeSeat</c> = the authoritative
    ///   <see cref="Mahjong.Autotable.Api.Changsha.ChangshaGameState.ActiveSeatIndex"/> and
    ///   <c>awaitingDiscard</c> = <c>true</c>. That seat owes the discard whatever produced the
    ///   turn — the dealer's initial 14, an ordinary auto-draw, a Chow/Pung claim, or a Kong
    ///   replacement draw — for human and bot seats alike (C-2).</item>
    ///   <item>On every other phase → <c>activeSeat</c> = <c>null</c> and
    ///   <c>awaitingDiscard</c> = <c>false</c>: the discard cue is explicitly RETRACTED so it can
    ///   never linger past phase exit (C-1's tombstone discipline). An explicit
    ///   <c>activeSeat: null</c> (not an omitted field or a JS-null tombstone) is deliberate —
    ///   the frontend's <c>resolveActiveSeat</c> trusts an explicit null over stale
    ///   <c>things</c> geometry, so this keeps the turn cue authoritative rather than
    ///   geometry-derived.</item>
    /// </list>
    /// This is the authoritative turn cue: the frontend must not infer the discard turn from tile
    /// geometry alone (geometry stays as defence-in-depth only). Clients never push this kind (the
    /// endpoint drops any inbound <c>turn</c>). Wire shape locked with Hicks (see frontend
    /// <c>types.ts:TurnEntry</c> / <c>world.ts:normalizeTurnEntry</c>).
    /// </summary>
    public const string Turn = "turn";

    /// <summary>
    /// Client-emitted only: an active-seat human player clicked a hand tile to
    /// discard it. Keyed by seat index (0..3), value =
    /// <c>{ tileId: int }</c>. The backend routes it to
    /// <see cref="IChangshaGameRuntime.DiscardAsync"/>. Phase/seat are validated
    /// by the state machine — invalid clicks are silently dropped. No
    /// server-emitted form (the resulting tile move is already broadcast via
    /// the <c>things</c> collection).
    /// </summary>
    public const string Discard = "discard";

    /// <summary>Private own-turn availability under a numeric seat key. Client commands carry
    /// gameId, expectedVersion, action (hu/concealedKong/addedKong), and exact Kong tileIds.</summary>
    public const string OwnTurn = "ownTurn";

    /// <summary>
    /// Server-emitted only (inbound to clients): the authoritative end-of-match signal.
    /// Singleton keyed <c>"current"</c>, value = <see cref="GameCompleteEntry"/>. Emitted by
    /// <see cref="ChangshaToAutotableTranslator"/> once the game reaches its terminal phase
    /// (<see cref="Mahjong.Autotable.Api.Changsha.ChangshaGameState.IsGameComplete"/>), which is
    /// what makes the bundle's <c>#game-complete-modal</c> reachable through real play. Clients
    /// never push this kind (the endpoint drops any inbound <c>gameComplete</c>); locked C-1.
    /// </summary>
    public const string GameComplete = "gameComplete";
}

/// <summary>
/// Server-emitted authoritative turn cue. Maps to the
/// <see cref="ChangshaCollectionKinds.Turn"/> collection value under key <c>"current"</c>.
/// Wire shape locked with Hicks's consumer (frontend <c>types.ts:TurnEntry</c> /
/// <c>world.ts:normalizeTurnEntry</c>): <c>{ activeSeat, phase, awaitingDiscard }</c>.
/// <para>The frontend treats <c>activeSeat === mySeat &amp;&amp; awaitingDiscard</c> as the single
/// source of truth for enabling the local discard affordance, and an explicit
/// <c>activeSeat: null</c> as "no seat is on the clock" (overriding stale tile geometry) — so
/// <see cref="ActiveSeat"/> is serialized even when null (see the
/// <see cref="JsonIgnoreCondition.Never"/> below), otherwise the shared
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> serializer would drop it and the reader would
/// fall back to geometry.</para>
/// </summary>
public sealed class TurnEntry
{
    /// <summary>Seat (0..3) that owes the discard while AwaitingDiscard, or <c>null</c> when no seat
    /// is on the clock (any other phase). Always emitted — an explicit null is meaningful.</summary>
    [JsonPropertyName("activeSeat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ActiveSeat { get; set; }

    /// <summary>The current <see cref="Mahjong.Autotable.Api.Changsha.ChangshaPhase"/> name
    /// (e.g. <c>"AwaitingDiscard"</c>); lets the client derive/verify the cue and aids debugging.</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    /// <summary>True exactly when <see cref="ActiveSeat"/> must discard now
    /// (phase == AwaitingDiscard); false on every other phase (cue retracted).</summary>
    [JsonPropertyName("awaitingDiscard")]
    public bool AwaitingDiscard { get; set; }
}

/// <summary>
/// Server-emitted snapshot of an open claim window for one seat. Maps to the
/// <see cref="ChangshaCollectionKinds.Claim"/> collection value.
/// </summary>
public sealed class ClaimWindowEntry
{
    /// <summary>Runtime identity paired with StateVersion for version-aware claim commands.</summary>
    [JsonPropertyName("gameId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GameId { get; set; }

    /// <summary>Copy into expectedVersion; deadline/source/tile alone do not identify a window.</summary>
    [JsonPropertyName("stateVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StateVersion { get; set; }

    [JsonPropertyName("available")]
    public List<string> Available { get; set; } = [];

    /// <summary>UTC unix millis at which auto-pass kicks in.</summary>
    [JsonPropertyName("deadline")]
    public long DeadlineUnixMs { get; set; }

    /// <summary>Seat that discarded the tile triggering the window.</summary>
    [JsonPropertyName("source")]
    public int SourceSeat { get; set; }

    /// <summary>Changsha tile id of the discarded tile.</summary>
    [JsonPropertyName("tile")]
    public int TileId { get; set; }

    /// <summary>Exact concealed partner pairs, supplied only to the owning claimant.</summary>
    [JsonPropertyName("chowOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int[]>? ChowOptions { get; set; }
}

public sealed class OwnTurnEntry
{
    /// <summary>Runtime identity, copied into commands to reject cross-room replay.</summary>
    [JsonPropertyName("gameId")]
    public required string GameId { get; init; }

    [JsonPropertyName("stateVersion")]
    public int StateVersion { get; init; }

    [JsonPropertyName("hu")]
    public bool Hu { get; init; }

    [JsonPropertyName("concealedKongs")]
    public List<int[]> ConcealedKongs { get; init; } = [];

    [JsonPropertyName("addedKongs")]
    public List<int> AddedKongs { get; init; } = [];
}

/// <summary>
/// Server-emitted hand result. Maps to the <see cref="ChangshaCollectionKinds.Result"/>
/// collection value under key <c>"current"</c>.
/// </summary>
public sealed class HandResultEntry
{
    [JsonPropertyName("winner")]
    public int Winner { get; set; }

    /// <summary>One of <c>"Hu"</c>, <c>"Draw"</c>, <c>"ZhaHu"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Cumulative net payments per seat for this hand (positive = gained). Wire shape is
    /// an array of <c>{ seat, delta }</c> objects ordered by seat — see
    /// <see cref="ScoreDeltaEntry"/>. The frontend result modal spreads this field
    /// (<c>[...result.score]</c>) so it MUST always be a JSON array (possibly empty),
    /// never a JSON object or null. Initialized to an empty list so even a partial
    /// <see cref="HandResultEntry"/> emitted before scoring completes serializes as
    /// <c>"score": []</c>.
    /// </summary>
    [JsonPropertyName("score")]
    public List<ScoreDeltaEntry> Score { get; set; } = [];

    /// <summary>
    /// Tile ids in the winning hand (concealed + meld) for the result panel. Wire shape
    /// is an array of tile-id ints — the frontend iterates this field with
    /// <c>for (const tile of result.hand)</c> so it MUST always be a JSON array
    /// (possibly empty), never null or a scalar.
    /// </summary>
    [JsonPropertyName("hand")]
    public List<int> Hand { get; set; } = [];

    /// <summary>Banker seat for the next hand per Changsha v1.2 rotation.</summary>
    [JsonPropertyName("nextBanker")]
    public int NextBanker { get; set; }

    /// <summary>
    /// Phase I Wave 1 — nested win metadata mirroring the SignalR <c>WinDeclared</c>
    /// shape so the frontend result modal (chips, RobbingKong badge) can render
    /// without a second WS subscription. Null when the hand ended on draw/false-Hu.
    /// </summary>
    [JsonPropertyName("winResult")]
    public WinResultEntry? WinResult { get; set; }

    /// <summary>
    /// Phase I Wave 1 — nested scoring metadata (category, base points, multiplier,
    /// payments) so the result modal can render the multiplier breakdown
    /// (<c>base × patterns = total</c>) without an additional round-trip.
    /// Null when the hand ended on draw/false-Hu.
    /// </summary>
    [JsonPropertyName("scoreResult")]
    public ScoreResultEntry? ScoreResult { get; set; }

    /// <summary>Explicitly null unless this settlement is waiting for human Continue actions.</summary>
    [JsonPropertyName("continuation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public HandResultContinuationEntry? Continuation { get; set; }
}

public sealed class HandResultContinuationEntry
{
    [JsonPropertyName("gameId")]
    public required string GameId { get; init; }

    [JsonPropertyName("handNumber")]
    public int HandNumber { get; init; }

    [JsonPropertyName("resultToken")]
    public required string ResultToken { get; init; }

    [JsonPropertyName("requiredSeats")]
    public int[] RequiredSeats { get; init; } = [];

    [JsonPropertyName("acknowledgedSeats")]
    public int[] AcknowledgedSeats { get; init; } = [];

    [JsonPropertyName("waitingSeats")]
    public int[] WaitingSeats { get; init; } = [];
}

/// <summary>
/// Server-emitted end-of-match payload. Maps to the
/// <see cref="ChangshaCollectionKinds.GameComplete"/> collection value under key
/// <c>"current"</c>. The frontend (<c>game-ui.ts</c> / <c>client.ts GameCompleteEntry</c>)
/// shows <c>#game-complete-modal</c> when <see cref="IsComplete"/> is truthy and renders the
/// per-seat totals from <see cref="TotalScores"/> (keyed by seat index string) with the
/// subtitle derived from <see cref="MaxHands"/>. Distinct from
/// <see cref="HandResultEntry.Score"/> (which is a per-hand <c>[{seat,delta}]</c> <b>array</b>):
/// <see cref="TotalScores"/> is a cumulative seat→score <b>object</b>, so the two contracts never
/// collide.
/// </summary>
public sealed class GameCompleteEntry
{
    /// <summary>Authoritative "match is over" flag — true whenever emitted.</summary>
    [JsonPropertyName("isComplete")]
    public bool IsComplete { get; set; }

    /// <summary>Cumulative net score per seat, keyed by seat index as a string ("0".."3").</summary>
    [JsonPropertyName("totalScores")]
    public Dictionary<string, int> TotalScores { get; set; } = new();

    /// <summary>Configured hand cap for this match (drives the "N-hand match complete" subtitle).</summary>
    [JsonPropertyName("maxHands")]
    public int MaxHands { get; set; }
}

/// <summary>
/// Per-seat cumulative score delta entry carried inside <see cref="HandResultEntry.Score"/>.
/// Wire shape mirrors the frontend <c>ScoreDelta</c> interface (<c>types.ts</c>) so the
/// result-modal spread + sort
/// (<c>[...result.score ?? []].sort((a, b) =&gt; a.seat - b.seat)</c>) works directly
/// on the deserialized payload without any client-side coercion. Backend MUST emit
/// <see cref="HandResultEntry.Score"/> as an ARRAY of these — emitting a JSON object
/// (e.g. from <c>Dictionary&lt;int,int&gt;</c>) trips a <c>TypeError: ... is not iterable</c>
/// in <c>game-ui.ts:renderResult</c> because the <c>??</c> guard only catches
/// <c>null</c> / <c>undefined</c>.
/// </summary>
public sealed class ScoreDeltaEntry
{
    /// <summary>Seat index 0..3.</summary>
    [JsonPropertyName("seat")]
    public int Seat { get; set; }

    /// <summary>Cumulative net point delta for this seat (positive = gained, negative = paid).</summary>
    [JsonPropertyName("delta")]
    public int Delta { get; set; }
}

/// <summary>
/// Phase I Wave 1 — nested win metadata carried inside <see cref="HandResultEntry"/>.
/// Mirrors the SignalR <c>WinDeclared</c> message shape so the frontend result-modal
/// chip strip + RobbingKong badge work uniformly across both transports.
/// </summary>
public sealed class WinResultEntry
{
    [JsonPropertyName("winningSeatIndex")]
    public int WinningSeatIndex { get; set; }

    /// <summary>One of <c>"selfDraw"</c>, <c>"discard"</c>, <c>"robbingKong"</c>.</summary>
    [JsonPropertyName("winType")]
    public string WinType { get; set; } = string.Empty;

    /// <summary>Highest-precedence pattern (camelCase, e.g. <c>"sevenPairs"</c>).</summary>
    [JsonPropertyName("winPattern")]
    public string WinPattern { get; set; } = string.Empty;

    [JsonPropertyName("winningTileId")]
    public int WinningTileId { get; set; }

    [JsonPropertyName("sourceSeatIndex")]
    public int SourceSeatIndex { get; set; }

    /// <summary>Every Big Win pattern that fired (deterministic enum order).</summary>
    [JsonPropertyName("allPatterns")]
    public List<string> AllPatterns { get; set; } = [];

    /// <summary>True when this hand won by 抢杠胡 (robbing the added kong).</summary>
    [JsonPropertyName("isRobbedKong")]
    public bool IsRobbedKong { get; set; }

    /// <summary>
    /// Phase J Wave 3 — true when the winning tile arrived via a draw from the wall
    /// (regular OR kong-replacement) rather than by claiming another seat's discard.
    /// Mirrors <see cref="WinResult.IsSelfDraw"/>. Provided so the frontend banner /
    /// result-modal copy can distinguish self-draw from ron without parsing
    /// <see cref="WinType"/>.
    /// </summary>
    [JsonPropertyName("isSelfDraw")]
    public bool IsSelfDraw { get; set; }

    /// <summary>
    /// Phase J Wave 3 — true when the winning tile was drawn as a kong replacement
    /// (杠上开花). Mirrors <see cref="WinResult.IsKongReplacement"/>. Backward-compat:
    /// the corresponding <c>kongReplacementWin</c> entry is still emitted inside
    /// <see cref="AllPatterns"/>, so legacy clients that scan the pattern list keep
    /// working unchanged.
    /// </summary>
    [JsonPropertyName("isKongReplacement")]
    public bool IsKongReplacement { get; set; }
}

/// <summary>
/// Phase I Wave 1 — nested scoring metadata carried inside <see cref="HandResultEntry"/>.
/// Mirrors the SignalR <c>HandFinished</c> message scoreResult shape.
/// </summary>
public sealed class ScoreResultEntry
{
    /// <summary>One of <c>"smallWin"</c>, <c>"bigWin"</c>.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("basePoints")]
    public int BasePoints { get; set; }

    [JsonPropertyName("payments")]
    public List<ScorePaymentEntry> Payments { get; set; } = [];

    /// <summary>
    /// Fan-catalog breakdown layered onto the base score (Frost's
    /// <c>FanCalculator.EvaluateHand</c>, wired in
    /// <c>ChangshaGameStateMachine.Score</c>). Each entry carries the canonical
    /// <see cref="Fan"/> enum name (e.g. <c>"selfDraw"</c>, <c>"fullFlush"</c>) and
    /// the per-payment point value. Empty when no fan applied. Backward-compatible:
    /// legacy clients that ignore this field continue working unchanged.
    /// </summary>
    [JsonPropertyName("fans")]
    public List<FanEntry> Fans { get; set; } = [];

    /// <summary>
    /// Sum of every <see cref="FanEntry.Points"/> — mirrors
    /// <see cref="ScoreResult.FanPoints"/>. Useful for the result modal's
    /// "fan total" subtotal line without re-aggregating <see cref="Fans"/>.
    /// </summary>
    [JsonPropertyName("fanPoints")]
    public int FanPoints { get; set; }
}

/// <summary>
/// Phase L (post-W23) — single detected fan in <see cref="ScoreResultEntry.Fans"/>.
/// Wire shape carries the catalog identifier (<c>fan</c>, camelCase of the
/// <c>Fan</c> enum), Chinese/Pinyin/English labels for direct frontend rendering,
/// and per-payment points. The catalog is the source of truth — frontend renderers
/// can also look up <c>fan</c> in their own i18n catalog if they need a different
/// language path (see <c>FanCatalog.Get(fan)</c>).
/// </summary>
public sealed class FanEntry
{
    /// <summary>Canonical fan identifier (camelCase of the <c>Fan</c> enum, e.g.
    /// <c>"selfDraw"</c>, <c>"kongReplacement"</c>, <c>"fullFlush"</c>).</summary>
    [JsonPropertyName("fan")]
    public string Fan { get; set; } = string.Empty;

    /// <summary>Per-payment point value contributed by this fan
    /// (e.g. <c>1</c> for SelfDraw, <c>6</c> for FullFlush). Multiplied across each
    /// existing base payment by <c>ChangshaGameStateMachine.Score</c>.</summary>
    [JsonPropertyName("points")]
    public int Points { get; set; }

    [JsonPropertyName("chinese")]
    public string Chinese { get; set; } = string.Empty;

    [JsonPropertyName("pinyin")]
    public string Pinyin { get; set; } = string.Empty;

    [JsonPropertyName("english")]
    public string English { get; set; } = string.Empty;
}

public sealed class ScorePaymentEntry
{
    [JsonPropertyName("fromSeatIndex")]
    public int FromSeatIndex { get; set; }

    [JsonPropertyName("toSeatIndex")]
    public int ToSeatIndex { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Helpers for shaping outbound Changsha-collection entries.</summary>
public static class ChangshaCollectionEncoder
{
    /// <summary>
    /// Encodes a claim window for <paramref name="seat"/> as the <c>claim</c> collection
    /// entry the bundle expects. Caller decides timeout policy.
    /// </summary>
    /// <remarks>
    /// Frost 2026-05-29 — the bundle-side <c>Collection&lt;K,V&gt;</c> stores entries in a
    /// JS <c>Map</c>, where <c>Map.get(0)</c> and <c>Map.get("0")</c> are distinct lookups.
    /// <c>game-ui.ts.sendClaim()</c> writes its outbound action via
    /// <c>client.claim.set(String(selfSeat), …)</c>, so the entry the frontend hands back
    /// to itself is keyed by string. If we emit the seat as a number here, every server
    /// snapshot replaces the string entry with a numeric one, causing the overlay's
    /// <c>key !== String(selfSeat)</c> filter to silently drop the entry (the overlay
    /// stays hidden even though a real claim window is open). Emitting the seat as a
    /// stringified number keeps both write paths keyed consistently.
    /// </remarks>
    public static CollectionEntry EncodeClaimWindow(
        int seat,
        IEnumerable<string> available,
        int sourceSeat,
        int tileId,
        long deadlineUnixMs,
        IEnumerable<int[]>? chowOptions = null,
        string? gameId = null,
        int? stateVersion = null)
        => new(ChangshaCollectionKinds.Claim, seat.ToString(System.Globalization.CultureInfo.InvariantCulture), new ClaimWindowEntry
        {
            Available = available.ToList(),
            DeadlineUnixMs = deadlineUnixMs,
            SourceSeat = sourceSeat,
            TileId = tileId,
            ChowOptions = chowOptions?.ToList(),
            GameId = gameId,
            StateVersion = stateVersion
        });

    /// <summary>Encodes a tombstone for <paramref name="seat"/>'s claim window (window closed).</summary>
    public static CollectionEntry EncodeClaimWindowClosed(int seat)
        => new(ChangshaCollectionKinds.Claim, seat.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

    public static CollectionEntry EncodeOwnTurn(int seat, OwnTurnEntry? actions)
        => new(ChangshaCollectionKinds.OwnTurn, seat, actions);

    /// <summary>Encodes the current hand's result as the <c>result["current"]</c> entry.</summary>
    public static CollectionEntry EncodeHandResult(HandResultEntry result)
        => new(ChangshaCollectionKinds.Result, "current", result);

    /// <summary>Encodes a tombstone for the current hand result (cleared on next deal).</summary>
    public static CollectionEntry EncodeHandResultCleared()
        => new(ChangshaCollectionKinds.Result, "current", null);

    /// <summary>Encodes the authoritative end-of-match signal as the <c>gameComplete["current"]</c> entry.</summary>
    public static CollectionEntry EncodeGameComplete(GameCompleteEntry entry)
        => new(ChangshaCollectionKinds.GameComplete, "current", entry);

    /// <summary>Encodes a tombstone for the end-of-match signal (cleared when a fresh game starts).</summary>
    public static CollectionEntry EncodeGameCompleteCleared()
        => new(ChangshaCollectionKinds.GameComplete, "current", null);

    // ── Phase F: pickup-state collection ──

    /// <summary>
    /// Encodes the current pickup cursor as the <c>pickup["current"]</c> entry. Emitted
    /// whenever the pickup phase or active seat changes during a Manual deal. Drives the
    /// autotable scene's "Take Tiles" button visibility per seat. Caller is expected to
    /// derive <paramref name="count"/> from <c>ChangshaGameStateMachine.ExpectedPickupCount(phase)</c>.
    /// </summary>
    public static CollectionEntry EncodePickup(PickupEntry pickup)
        => new(ChangshaCollectionKinds.Pickup, "current", pickup);

    /// <summary>Encodes a tombstone for the pickup cursor (manual deal complete).</summary>
    public static CollectionEntry EncodePickupCleared()
        => new(ChangshaCollectionKinds.Pickup, "current", null);

    // ── C-1/C-2: authoritative discard-turn cue ──

    /// <summary>
    /// Encodes the authoritative turn cue as the <c>turn["current"]</c> entry (wire shape locked
    /// with Hicks: <c>{ activeSeat, phase, awaitingDiscard }</c>). Emitted on every snapshot:
    /// while <c>AwaitingDiscard</c> holds, <paramref name="activeSeat"/> is the seat that owes the
    /// discard and <paramref name="awaitingDiscard"/> is true; on every other phase
    /// <paramref name="activeSeat"/> is <c>null</c> and <paramref name="awaitingDiscard"/> is false
    /// (the cue is explicitly retracted — C-1 tombstone discipline).
    /// </summary>
    public static CollectionEntry EncodeTurn(int? activeSeat, string phase, bool awaitingDiscard)
        => new(ChangshaCollectionKinds.Turn, "current", new TurnEntry
        {
            ActiveSeat = activeSeat,
            Phase = phase,
            AwaitingDiscard = awaitingDiscard
        });
}

/// <summary>
/// Server-emitted <c>things</c> collection value (one per tile). Maps 1:1 to the frontend C-1
/// <c>ThingInfo</c> wire contract (<c>autotable-src/src/types.ts</c>).
///
/// <para><b>Why a typed DTO instead of an anonymous object:</b> <see cref="ClaimedBy"/>
/// (<c>number|null</c>) and <see cref="ShiftSlotName"/> (<c>string|null</c>) are <b>required and
/// present</b> in the C-1 type, and the relay path already emits them as explicit <c>null</c> (the
/// upstream client's <c>describeThing</c> serializes <c>claimedBy: null</c>, and the relay stores
/// the client JsonElement verbatim). The changsha translator previously emitted an anonymous object
/// whose null <c>claimedBy</c>/<c>shiftSlotName</c> were dropped by the shared serializer
/// (<see cref="AutotableJson"/> uses <see cref="JsonIgnoreCondition.WhenWritingNull"/>), so ~108
/// unclaimed tiles omitted the field — inconsistent with both the type and the relay path (Hicks's
/// P0). The <see cref="JsonIgnoreCondition.Never"/> on those two properties overrides the global
/// null-drop so the wire always carries an explicit <c>null</c>, restoring byte-consistency.</para>
/// </summary>
public sealed class ThingInfo
{
    [JsonPropertyName("slotName")]
    public string SlotName { get; set; } = string.Empty;

    [JsonPropertyName("rotationIndex")]
    public int RotationIndex { get; set; }

    /// <summary>Owning seat while a client is dragging the tile; server play is authoritative, so
    /// the translator always emits <c>null</c> — but explicitly (C-1 <c>claimedBy: number|null</c>).</summary>
    [JsonPropertyName("claimedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ClaimedBy { get; set; }

    [JsonPropertyName("heldRotation")]
    public object HeldRotation { get; set; } = default!;

    /// <summary>Shift-drag target slot; the translator emits <c>null</c> explicitly (C-1
    /// <c>shiftSlotName: string|null</c>), matching the relay path.</summary>
    [JsonPropertyName("shiftSlotName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ShiftSlotName { get; set; }
}

/// <summary>
/// Server-emitted snapshot of the pickup cursor while a Manual deal is in progress.
/// Maps to the <see cref="ChangshaCollectionKinds.Pickup"/> collection value under
/// key <c>"current"</c>.
///
/// <para><b>Pickup MATCH KEY = <c>targetSlots</c> (Ripley CANONICAL KEY LOCK 2026-08-07T11:23, "no more
/// pivots").</b> The opaque <c>targetHandles</c> refinement is DEAD/superseded; SC-2 opaque handles are the
/// hidden-<c>things</c> render key only (orthogonal). <c>targetSlots</c> is a SINGLE trigger slot (top-first
/// <c>Wall[0]</c>) — Hicks's matcher fails closed on <c>length != 1</c>; the full batch is display-only
/// <see cref="BatchPreviewSlots"/>. <b>No raw <c>targetTileIds</c></b> (parent 11:14) — intentionally absent
/// (reveal-only when present, per-viewer PROJECTED, never raw ids). Populated in one seam:
/// <c>ChangshaToAutotableTranslator.ApplyPickupTargetSlots</c>.</para>
/// </summary>
public sealed class PickupEntry
{
    /// <summary>One of <c>BreakPointMarked</c>, <c>PickupRound1</c>, <c>PickupRound2</c>,
    /// <c>PickupRound3</c>, <c>SingleTilePickup</c>, or <c>DealerExtra</c>.</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    /// <summary>Seat whose turn it is to take tiles from the wall (0..3).</summary>
    [JsonPropertyName("seatIndex")]
    public int SeatIndex { get; set; }

    /// <summary>Number of tiles the active seat should take from the wall front.
    /// 4 during PickupRound1..3, 1 during SingleTilePickup and DealerExtra.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>One of <c>"auto"</c>, <c>"manual"</c>. Echoed from
    /// <c>ChangshaGameState.DealMode</c> so the bundle can render the same cursor
    /// in both modes (auto-deal mode tombstones the entry immediately).</summary>
    [JsonPropertyName("dealMode")]
    public string DealMode { get; set; } = "manual";

    /// <summary>Break-point address (wall index / stack index / tile index within stack).
    /// Computed by <see cref="BreakPointService.ComputeBreakPoint"/> from the dice roll.
    /// Sent so the bundle can render the dealer's break point marker between rounds.</summary>
    [JsonPropertyName("breakPoint")]
    public BreakPointWire? BreakPoint { get; set; }

    /// <summary>Current zero-based offset into <c>ChangshaGameState.Wall</c> where the
    /// next pickup will draw from. Lets the bundle preview which slots will be removed
    /// next without re-deriving from <c>BreakPoint</c>.</summary>
    [JsonPropertyName("wallIndex")]
    public int WallIndex { get; set; }

    /// <summary>
    /// SC-4 v4 FROZEN (parent-locked, IMMUTABLE — `decisions.md` 09:06, re-confirmed through 10:21):
    /// the manual-pickup match key. <b>EXACTLY ONE co-derived trigger slot</b> = the reachable
    /// top-first <c>state.Wall[0]</c>, captured in the SAME wall-`things` projection pass (never a
    /// second ordinal computation). The client is interactive ⟺
    /// <c>hovered.slot.name === targetSlots[0]</c> (single trigger; all other batch tiles inert;
    /// missing/empty/<b>multiple ⇒ FAIL CLOSED</b>), and sends <c>pickup.take{seatIndex,count}</c>
    /// with ZERO tile authority. Opacity-independent (a hovered wall tile carries its
    /// <c>slotName</c> even under an opaque SC-2 handle). NO <c>targetTileIds</c>/<c>targetHandles</c>
    /// live here — SC-2 opaque handles are a SEPARATE `things`-key field (privacy/G19 only).
    /// </summary>
    [JsonPropertyName("targetSlots")]
    public IReadOnlyList<string> TargetSlots { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// SC-4 v4 — OPTIONAL, DISPLAY-ONLY highlight of the full designated batch
    /// (<c>state.Wall[0..count-1]</c> render slots, co-derived from the same pass as
    /// <see cref="TargetSlots"/>). NEVER actionable: the only interactive slot is
    /// <c>TargetSlots[0]</c>. The server takes <c>Wall[0..count-1]</c> by count regardless.
    /// </summary>
    [JsonPropertyName("batchPreviewSlots")]
    public IReadOnlyList<string> BatchPreviewSlots { get; set; } = System.Array.Empty<string>();
}

/// <summary>JSON-friendly mirror of <c>Changsha.BreakPointResult</c> (record struct).
/// Lives in this protocol-layer file so the autotable WS contract is fully described
/// alongside the rest of the wire format.</summary>
public sealed class BreakPointWire
{
    [JsonPropertyName("wallIndex")]
    public int WallIndex { get; set; }

    [JsonPropertyName("stackIndex")]
    public int StackIndex { get; set; }

    [JsonPropertyName("tileIndex")]
    public int TileIndex { get; set; }
}

public sealed record AutotableViewerAuthority(
    [property: JsonPropertyName("roomId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RoomId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("seat"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Seat);

/// <summary>Outbound <c>JOINED</c> envelope.</summary>
public sealed class JoinedMessage
{
    [JsonPropertyName("type")]
    public string Type { get; } = "JOINED";

    [JsonPropertyName("gameId")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("playerId")]
    public string PlayerId { get; set; } = string.Empty;

    [JsonPropertyName("isFirst")]
    public bool IsFirst { get; set; }

    [JsonPropertyName("viewer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutotableViewerAuthority? Viewer { get; init; }
}

/// <summary>Outbound <c>UPDATE</c> envelope (full or incremental).</summary>
public sealed class UpdateMessage
{
    [JsonPropertyName("type")]
    public string Type { get; } = "UPDATE";

    [JsonPropertyName("entries")]
    public List<CollectionEntry> Entries { get; set; } = [];

    [JsonPropertyName("full")]
    public bool Full { get; set; }

    [JsonPropertyName("viewer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutotableViewerAuthority? Viewer { get; init; }
}

/// <summary>Shared JSON options for the autotable protocol. CamelCase matches the rest of the API.</summary>
public static class AutotableJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Serializes <see cref="CollectionEntry"/> as the three-element array tuple
/// upstream expects (<c>[kind, key, value]</c>) and parses incoming arrays of the same shape.
/// </summary>
internal sealed class CollectionEntryJsonConverter : JsonConverter<CollectionEntry>
{
    public override CollectionEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected JSON array for CollectionEntry.");

        reader.Read();
        var kind = reader.GetString() ?? throw new JsonException("CollectionEntry.kind must be a string.");

        reader.Read();
        // NB: explicit `(object)` casts on both branches prevent C# from unifying
        // `long` and `double` to `double` via implicit conversion. Without them the
        // conditional expression's static type is `double`, so even when
        // `TryGetInt64` succeeds the boxed `entry.Key` is a `Double` — silently
        // breaking every downstream `entry.Key is long` / `is int` pattern match
        // (notably `TryHandleDiscardActionAsync` / `TryHandleClaimActionAsync`,
        // which would then read `Double 0.0`, fall through every case, and reject
        // the action as a bad seat).
        object key = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? (object)l : (object)reader.GetDouble(),
            _ => throw new JsonException("CollectionEntry.key must be string or number.")
        };

        reader.Read();
        object? value;
        if (reader.TokenType == JsonTokenType.Null)
        {
            value = null;
        }
        else
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            value = doc.RootElement.Clone();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("Expected end of CollectionEntry array.");

        return new CollectionEntry(kind, key, value);
    }

    public override void Write(Utf8JsonWriter writer, CollectionEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.Kind);

        switch (value.Key)
        {
            case string s: writer.WriteStringValue(s); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d: writer.WriteNumberValue(d); break;
            default: JsonSerializer.Serialize(writer, value.Key, value.Key.GetType(), options); break;
        }

        if (value.Value is null)
            writer.WriteNullValue();
        else
            JsonSerializer.Serialize(writer, value.Value, value.Value.GetType(), options);

        writer.WriteEndArray();
    }
}
