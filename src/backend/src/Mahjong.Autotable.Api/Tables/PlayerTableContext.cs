using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tables;

/// <summary>
/// Phase K Wave 8 — Bishop. Centralises the "is this player
/// associated with this table?" gate logic that was previously
/// inlined inside <see cref="Mahjong.Autotable.Api.Voice.VoiceHub"/>.
///
/// <para>Two callers exercise this surface in W8:</para>
/// <list type="bullet">
///   <item><see cref="Mahjong.Autotable.Api.Voice.VoiceLivestreamController"/>
///         — gates the public HLS playlist + segment endpoints so
///         only seated players or active spectators on the table
///         can pull the stream.</item>
///   <item><see cref="Mahjong.Autotable.Api.Voice.VoiceHub"/> — the
///         legacy seated-vs-spectator branching keeps its inline
///         implementation for backwards compat; the W8 service
///         centralises the same logic so future surfaces don't
///         re-implement the gate.</item>
/// </list>
///
/// <para>The implementation reads:
/// <list type="number">
///   <item>The session role: <c>admin</c> bypasses the gate (admins
///         can always pull the playlist).</item>
///   <item>The persistent <c>ChangshaGame.OwnerPlayerId</c> — owners
///         are treated as seated by default for the
///         pre-hydration window.</item>
///   <item>The live <see cref="IChangshaGameRuntime.TryGetSnapshot"/>
///         result — when the runtime has hydrated the game state,
///         we check whether the caller occupies a seat.</item>
///   <item>An optional "in spectator group" check resolved by the
///         caller — Wave 8 lets the controller pass an anonymous id
///         (from the anon-cookie identity service) so unauthenticated
///         spectators still pass when they're actively watching the
///         table via SignalR.</item>
/// </list></para>
/// </summary>
public interface IPlayerTableContext
{
    /// <summary>
    /// Resolves the player's association with the supplied
    /// table/game id. The returned <see cref="PlayerTableAssociation"/>
    /// is the canonical envelope the gate surfaces consume.
    /// </summary>
    Task<PlayerTableAssociation> ResolveAsync(
        Guid gameId,
        PlayerAuthSession? session,
        string? anonymousPlayerId,
        CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 8 — Bishop. Canonical envelope describing a player's
/// relationship to a given table. The gate surfaces dispatch on
/// <see cref="Role"/>; <see cref="Reason"/> carries a free-form
/// classifier for the audit trail.
/// </summary>
public sealed record PlayerTableAssociation(
    PlayerTableRole Role,
    string? PlayerId,
    string? Reason)
{
    public static PlayerTableAssociation Unknown(string reason) =>
        new(PlayerTableRole.Unknown, null, reason);

    public static PlayerTableAssociation Anonymous() =>
        new(PlayerTableRole.Anonymous, null, "no-session");

    public static PlayerTableAssociation Seated(string playerId) =>
        new(PlayerTableRole.Seated, playerId, "seat-occupied");

    public static PlayerTableAssociation Owner(string playerId) =>
        new(PlayerTableRole.Owner, playerId, "table-owner");

    public static PlayerTableAssociation Spectator(string playerId) =>
        new(PlayerTableRole.Spectator, playerId, "spectator-snapshot-present");

    public static PlayerTableAssociation Admin(string playerId) =>
        new(PlayerTableRole.Admin, playerId, "admin-override");
}

/// <summary>
/// Phase K Wave 8 — Bishop. Discriminated player-vs-table role used
/// by the gate surfaces to decide allow/deny. <see cref="Anonymous"/>
/// = no session; <see cref="Unknown"/> = session resolved but no
/// relationship to the table.
/// </summary>
public enum PlayerTableRole
{
    Anonymous,
    Unknown,
    Seated,
    Owner,
    Spectator,
    Admin,
}

/// <summary>
/// Phase K Wave 8 — Bishop. Default <see cref="IPlayerTableContext"/>
/// implementation. Reads
/// <see cref="ChangshaGame.OwnerPlayerId"/> from the DB +
/// <see cref="IChangshaGameRuntime.TryGetSnapshot"/> from the
/// in-memory runtime.
/// </summary>
public sealed class PlayerTableContext : IPlayerTableContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IChangshaGameRuntime _runtime;

    public PlayerTableContext(IServiceScopeFactory scopeFactory, IChangshaGameRuntime runtime)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<PlayerTableAssociation> ResolveAsync(
        Guid gameId,
        PlayerAuthSession? session,
        string? anonymousPlayerId,
        CancellationToken ct = default)
    {
        // Resolve the effective caller id. Authenticated session
        // takes precedence; anonymous cookie id is the fallback for
        // spectator surfaces that are open to non-authed clients.
        var effectivePlayerId = session?.PlayerId ?? anonymousPlayerId;
        if (string.IsNullOrEmpty(effectivePlayerId))
            return PlayerTableAssociation.Anonymous();

        if (session is not null
            && string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return PlayerTableAssociation.Admin(effectivePlayerId);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ChangshaGames
            .AsNoTracking()
            .Where(g => g.Id == gameId)
            .Select(g => new { g.OwnerPlayerId })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return PlayerTableAssociation.Unknown("table-not-found");

        if (string.Equals(row.OwnerPlayerId, effectivePlayerId, StringComparison.Ordinal))
            return PlayerTableAssociation.Owner(effectivePlayerId);

        var snapshotAvailable = _runtime.TryGetSnapshot(
            gameId.ToString(),
            out var state) && state is not null;
        if (snapshotAvailable)
        {
            foreach (var seat in state!.Seats)
            {
                if (!string.IsNullOrEmpty(seat.PlayerId)
                    && string.Equals(seat.PlayerId, effectivePlayerId, StringComparison.Ordinal))
                {
                    return PlayerTableAssociation.Seated(effectivePlayerId);
                }
            }
            // The state is hydrated and the caller is not in any seat
            // — they're a spectator on a live table.
            return PlayerTableAssociation.Spectator(effectivePlayerId);
        }

        return PlayerTableAssociation.Unknown("not-seated-snapshot-missing");
    }
}
