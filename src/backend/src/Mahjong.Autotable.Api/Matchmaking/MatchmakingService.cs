using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;

namespace Mahjong.Autotable.Api.Matchmaking;

/// <summary>
/// Phase J Wave 5 — wire DTO for the <c>GET /api/matchmaking/lobby</c>
/// endpoint. Mirrors <see cref="LobbyGameSnapshot"/> but resolves
/// <c>CreatorPlayerId</c> to a display-name so the frontend doesn't have to
/// round-trip through the profile endpoint.
/// </summary>
public sealed record LobbyGameDto(
    string GameId,
    string? PublicName,
    string? CreatorDisplayName,
    int SeatedCount,
    int MaxSeats,
    string Variant,
    DateTime CreatedAt,
    int BotCount = 0,
    int OpenHumanSeats = 0);

/// <summary>
/// Phase J Wave 5 — joins the in-memory matchmaking snapshot from
/// <see cref="IChangshaGameRuntime"/> with the persistent profile lookup so
/// the lobby UI sees "Bishop's Game — hosted by &lt;Display Name&gt;" without
/// needing a second round-trip. Also owns the host-only <c>SetGamePublic</c>
/// and <c>JoinRandom</c> entry points used by the SignalR hub.
/// </summary>
public sealed class MatchmakingService
{
    public const int LobbyCap = 50;

    private readonly IChangshaGameRuntime _runtime;
    private readonly PlayerProfileService _profiles;

    public MatchmakingService(IChangshaGameRuntime runtime, PlayerProfileService profiles)
    {
        _runtime = runtime;
        _profiles = profiles;
    }

    /// <summary>
    /// Returns the current public-listing snapshot (capped, newest-first).
    /// Each entry's <c>CreatorDisplayName</c> is resolved via the profile
    /// service; missing profiles fall back to the default-name helper.
    /// </summary>
    public async Task<IReadOnlyList<LobbyGameDto>> GetLobbyAsync(CancellationToken ct = default)
    {
        var snapshots = _runtime.SnapshotLobbyGames(LobbyCap);
        var result = new List<LobbyGameDto>(snapshots.Count);
        foreach (var snap in snapshots)
        {
            var room = await _runtime.ResolveExistingRoomAsync(snap.GameId, ct);
            if (room is null) continue;
            string? displayName = null;
            if (!string.IsNullOrEmpty(snap.CreatorPlayerId))
            {
                var profile = await _profiles.GetOrCreateAsync(snap.CreatorPlayerId, ct);
                displayName = profile.DisplayName;
            }
            result.Add(new LobbyGameDto(
                GameId: room.RoomId,
                PublicName: snap.PublicName,
                CreatorDisplayName: displayName,
                SeatedCount: snap.SeatedCount,
                MaxSeats: snap.MaxSeats,
                Variant: snap.Variant,
                CreatedAt: snap.CreatedAt,
                BotCount: snap.BotCount,
                OpenHumanSeats: snap.OpenHumanSeats));
        }
        return result;
    }

    public async Task SetGamePublicAsync(string gameId, string callerPlayerId, bool isPublic, string? publicName, CancellationToken ct = default)
    {
        var room = await _runtime.ResolveExistingRoomAsync(gameId, ct)
            ?? throw new RoomAdmissionException("room-not-found");
        await _runtime.SetGamePublicAsync(room.RuntimeGameId, callerPlayerId, isPublic, publicName, ct);
    }

    public async Task<RoomReference?> FindJoinableGameAsync(string? variant, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(variant)
            && !string.Equals(variant, "changsha", StringComparison.OrdinalIgnoreCase))
            return null;
        var candidates = _runtime.SnapshotLobbyGames(LobbyCap);
        if (candidates.Count == 0) return null;
        var candidate = candidates[Random.Shared.Next(candidates.Count)];
        return await _runtime.ResolveExistingRoomAsync(candidate.GameId, ct);
    }

    /// <summary>
    /// Phase J Wave 6 — picks a public lobby-phase game with a free human seat
    /// and seats the caller into it. Wave-6 split the conflated identity arg
    /// into <paramref name="playerId"/> (persistent cookie-derived identifier
    /// used for stats and seat ownership) and <paramref name="connectionId"/>
    /// (transport-level SignalR id used for per-connection routing).
    /// </summary>
    public async Task<(string GameId, int SeatIndex)?> JoinRandomAsync(string playerId, string connectionId, string? variant, CancellationToken ct = default)
    {
        var result = await _runtime.JoinRandomAsync(playerId, connectionId, variant, ct);
        if (result is null) return null;
        var room = await _runtime.ResolveExistingRoomAsync(result.Value.GameId, ct)
            ?? throw new RoomAdmissionException("room-not-found");
        return (room.RoomId, result.Value.SeatIndex);
    }
}
