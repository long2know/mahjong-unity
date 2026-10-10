namespace Mahjong.Autotable.Api.Spectator;

/// <summary>
/// Phase K Wave 3 — Bishop (Backend). Canonical spectator-stream
/// envelope. The spectator surface (Phase L HLS pipeline + the Wave 2
/// stub livestream endpoint) emits one of these per observable game
/// event so the client / encoder consumes a single, stable shape
/// regardless of which Wave the producing code lands in.
///
/// <para>Fields:</para>
/// <list type="bullet">
///   <item><see cref="Type"/> — discriminator. Canonical values
///         include <c>tile.flipped</c>, <c>discard</c>, <c>draw</c>,
///         <c>peng</c>, <c>gang</c>, <c>chi</c>, <c>win</c>. Producers
///         that don't fit one of these can mint a new dotted name;
///         consumers fall back to a "unknown event type" branch.</item>
///   <item><see cref="GameId"/> — the
///         <see cref="Mahjong.Autotable.Api.Data.Entities.ChangshaGame.Id"/>
///         the event was produced against, as the canonical
///         <c>N</c>-formatted GUID string.</item>
///   <item><see cref="PlayerId"/> — the actor (or null for
///         server-driven events like wall reshuffles).</item>
///   <item><see cref="Ts"/> — UTC ISO-8601 timestamp the envelope
///         was minted. Encoders use this to align HLS GOP
///         boundaries.</item>
///   <item><see cref="Data"/> — opaque event-specific payload. Each
///         <see cref="Type"/> defines its own shape (e.g.
///         <c>tile.flipped</c> ⇒ <c>{ tileId, suit, rank }</c>).</item>
/// </list>
///
/// <para>The envelope is intentionally a record so the JSON
/// serialisation lands camelCase + immutable without any extra
/// attribute work; Vasquez's Wave-3 contract test pins the field
/// names via reflection.</para>
/// </summary>
public sealed record SpectatorEvent(
    string Type,
    string GameId,
    string? PlayerId,
    DateTime Ts,
    object? Data);
