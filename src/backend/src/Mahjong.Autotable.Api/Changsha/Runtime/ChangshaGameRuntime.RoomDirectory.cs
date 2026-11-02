using System.Data.Common;
using Mahjong.Autotable.Api.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Changsha.Runtime;

public sealed record RoomReference(string RoomId, string RuntimeGameId);

public sealed record RoomAccessSnapshot(
    string? OwnerId,
    ChangshaPhase Phase,
    bool IsPublic,
    string? PublicName,
    int BotCount,
    int SeatedCount,
    int OpenHumanSeats,
    bool ViewerHasConnectedHumanSeat,
    bool BotQuotaLocked = false)
{
    public bool CanInvite(string playerId) =>
        ViewerHasConnectedHumanSeat && Phase == ChangshaPhase.Seating && OpenHumanSeats > 0
        && (IsPublic || string.Equals(OwnerId, playerId, StringComparison.Ordinal));
}

public sealed class RoomAdmissionException(string reason) : HubException(reason)
{
    public string Reason { get; } = reason;
}

public sealed partial class ChangshaGameRuntime
{
    public async Task<RoomReference?> ResolveExistingRoomAsync(string roomId, CancellationToken ct = default)
    {
        // An exact alias takes precedence even when its spelling is also a GUID.
        var aliasedGameId = await RestorePublicRoomAsync(roomId, ct);
        if (aliasedGameId is not null)
            return new RoomReference(roomId, aliasedGameId);
        if (!Guid.TryParse(roomId, out var runtimeId)) return null;

        var normalized = runtimeId.ToString();
        var cachedAliases = _publicRoomBindings
            .Where(binding => string.Equals(binding.Value, normalized, StringComparison.Ordinal))
            .Select(binding => binding.Key).Take(2).ToArray();
        if (cachedAliases.Length > 1)
            throw new PublicRoomRecoveryException("room-binding-conflict");

        string? alias = cachedAliases.SingleOrDefault();
        if (alias is null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var aliases = await db.AutotableRoomBindings.AsNoTracking()
                    .Where(binding => binding.RuntimeGameId == runtimeId)
                    .Select(binding => binding.RoomId).Take(2).ToArrayAsync(ct);
                if (aliases.Length > 1)
                    throw new PublicRoomRecoveryException("room-binding-conflict");
                alias = aliases.SingleOrDefault();
            }
            catch (DbException ex)
            {
                throw new PublicRoomRecoveryException("room-store-unavailable", ex);
            }
        }

        if (alias is not null)
        {
            var restored = await RestorePublicRoomAsync(alias, ct);
            if (!string.Equals(restored, normalized, StringComparison.Ordinal))
                throw new PublicRoomRecoveryException("room-binding-conflict");
            return new RoomReference(alias, normalized);
        }

        return _games.ContainsKey(normalized) ? new RoomReference(normalized, normalized) : null;
    }

    public async Task<RoomAccessSnapshot?> GetRoomAccessAsync(
        string runtimeGameId, string? viewerPlayerId, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(runtimeGameId, out var instance)) return null;
        using var lifetime = instance.TryEnterOperation();
        if (lifetime is null) return null;
        var isAliasedRoom = await IsAliasedRoomAsync(instance, ct);
        await instance.Lock.WaitAsync(ct);
        try
        {
            var state = instance.State;
            return new RoomAccessSnapshot(
                state.CreatorPlayerId,
                state.Phase,
                state.IsPublic,
                state.PublicName,
                state.Seats.Count(seat => seat.IsBot),
                CountConnectedHumans(instance),
                CountOpenHumanSeats(instance),
                !string.IsNullOrEmpty(viewerPlayerId) && state.Seats.Any(seat =>
                    !seat.IsBot && string.Equals(seat.PlayerId, viewerPlayerId, StringComparison.Ordinal)
                    && instance.SeatConnections.ContainsKey(seat.SeatIndex)),
                isAliasedRoom);
        }
        finally { instance.Lock.Release(); }
    }

    public Task LeaveTableAsync(string gameId, string playerId, string connectionId, CancellationToken ct = default)
    {
        lock (_disposeGate)
        {
            if (_stopping || !_games.TryGetValue(gameId, out var instance)
                || instance.TryEnterOperation() is not { } lifetime)
                return Task.CompletedTask;
            return TrackCleanup(HandleDisconnectCoreAsync(playerId, connectionId,
                [(gameId, instance, lifetime)], ct));
        }
    }

    private bool IsAliasedRoom(string runtimeGameId) =>
        _publicRoomBindings.Any(binding =>
            string.Equals(binding.Value, runtimeGameId, StringComparison.Ordinal));

    private async Task<bool> IsAliasedRoomAsync(ChangshaGameInstance instance, CancellationToken ct)
    {
        if (IsAliasedRoom(instance.GameId)) return true;
        if (!instance.WasRecovered) return false;
        // Hydration restores instances before the first alias lookup populates the directory cache.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var runtimeId = Guid.Parse(instance.GameId);
            return await db.AutotableRoomBindings.AsNoTracking()
                .AnyAsync(binding => binding.RuntimeGameId == runtimeId, ct);
        }
        catch (DbException ex)
        {
            throw new PublicRoomRecoveryException("room-store-unavailable", ex);
        }
    }

    private static bool IsOpenHumanSeat(ChangshaGameInstance instance, int seatIndex) =>
        !instance.State.Seats[seatIndex].IsBot
        && !instance.SeatConnections.ContainsKey(seatIndex)
        && !instance.RecoveredSeatOwners.ContainsKey(seatIndex);

    private static int CountOpenHumanSeats(ChangshaGameInstance instance) =>
        instance.State.Phase == ChangshaPhase.Seating
            ? Enumerable.Range(0, instance.State.Seats.Count).Count(index => IsOpenHumanSeat(instance, index))
            : 0;

    private static int CountConnectedHumans(ChangshaGameInstance instance) =>
        instance.State.Seats.Count(seat => !seat.IsBot
            && instance.SeatConnections.ContainsKey(seat.SeatIndex));

    private static bool AreAllSeatsOccupied(ChangshaGameInstance instance) =>
        instance.State.Seats.All(seat => seat.IsBot || instance.SeatConnections.ContainsKey(seat.SeatIndex));
}
