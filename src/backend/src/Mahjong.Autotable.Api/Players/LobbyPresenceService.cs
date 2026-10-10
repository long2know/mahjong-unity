using Mahjong.Autotable.Api.Changsha;
using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Autotable.Api.Players;

public sealed record OnlinePlayerDto(
    string PlayerId, string DisplayName, string? AvatarColor, bool CanReceiveInvites);

public sealed record LobbyPlayersSnapshot(long Revision, IReadOnlyList<OnlinePlayerDto> Players);

/// <summary>Verified, process-local transport membership; never a source of seat authority.</summary>
public sealed class LobbyPresenceService
{
    public const string LobbyGroup = "lobby";

    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectionPresence> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerPresence> _players = new(StringComparer.Ordinal);
    private readonly PlayerProfileService _profiles;
    private readonly IHubContext<ChangshaHub> _hub;
    private long _revision;

    public LobbyPresenceService(PlayerProfileService profiles, IHubContext<ChangshaHub> hub)
    {
        _profiles = profiles;
        _hub = hub;
    }

    public static string WebSocketConnection(string connectionId) => $"ws:{connectionId}";
    public static string HubConnection(string connectionId) => $"hub:{connectionId}";
    public static string PlayerGroup(string verifiedPlayerId) => $"lobby-player:{verifiedPlayerId}";

    public async Task RegisterVerifiedAsync(
        string connectionId, string verifiedPlayerId, CancellationToken ct = default)
    {
        if ((!connectionId.StartsWith("ws:", StringComparison.Ordinal)
                && !connectionId.StartsWith("hub:", StringComparison.Ordinal))
            || !PlayerIdentityService.IsValidPlayerId(verifiedPlayerId)
            || IsPlaceholder(verifiedPlayerId))
            throw new ArgumentException("Presence requires a namespaced transport and a verified human identity.");

        var profile = await _profiles.GetOrCreateAsync(verifiedPlayerId, ct);
        LobbyPlayersSnapshot? changed = null;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_connections.TryGetValue(connectionId, out var existing))
            {
                if (!string.Equals(existing.PlayerId, verifiedPlayerId, StringComparison.Ordinal))
                    throw new InvalidOperationException("A presence connection cannot change identity.");
            }
            else
            {
                var connection = new ConnectionPresence(verifiedPlayerId);
                _connections.Add(connectionId, connection);
                if (!_players.TryGetValue(verifiedPlayerId, out var player))
                {
                    player = new PlayerPresence(profile.DisplayName, profile.AvatarColor);
                    _players.Add(verifiedPlayerId, player);
                    changed = AdvanceSnapshot();
                }
                player.Connections.Add(connectionId);
            }
            var current = _players[verifiedPlayerId];
            if (current.DisplayName != profile.DisplayName || current.AvatarColor != profile.AvatarColor)
            {
                current.DisplayName = profile.DisplayName;
                current.AvatarColor = profile.AvatarColor;
                changed = AdvanceSnapshot();
            }
            // Include the newly registered transport in the first-identity snapshot.
            if (changed is not null) changed = SnapshotCore();
        }
        if (changed is not null) await PublishAsync(changed, ct);
    }

    public async Task<LobbyPlayersSnapshot> SubscribeLobbyAsync(string connectionId, CancellationToken ct = default)
    {
        LobbyPlayersSnapshot snapshot;
        var changed = false;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!connectionId.StartsWith("hub:", StringComparison.Ordinal)
                || !_connections.TryGetValue(connectionId, out var connection))
                throw new HubException("identity-required");
            var wasAvailable = CanReceiveInvitesCore(connection.PlayerId);
            connection.LobbySubscribed = true;
            changed = !wasAvailable;
            snapshot = changed ? AdvanceSnapshot() : SnapshotCore();
        }
        if (changed) await PublishAsync(snapshot, ct);
        return snapshot;
    }

    public async Task UnregisterAsync(string connectionId, CancellationToken ct = default)
    {
        LobbyPlayersSnapshot? changed = null;
        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var connection)) return;
            var wasAvailable = CanReceiveInvitesCore(connection.PlayerId);
            _connections.Remove(connectionId);
            var player = _players[connection.PlayerId];
            player.Connections.Remove(connectionId);
            if (player.Connections.Count == 0)
            {
                _players.Remove(connection.PlayerId);
                changed = AdvanceSnapshot();
            }
            else if (wasAvailable != CanReceiveInvitesCore(connection.PlayerId))
            {
                changed = AdvanceSnapshot();
            }
        }
        if (changed is not null) await PublishAsync(changed, ct);
    }

    public async Task UpdateProfileAsync(PlayerProfile profile, CancellationToken ct = default)
    {
        LobbyPlayersSnapshot? changed = null;
        lock (_gate)
        {
            if (_players.TryGetValue(profile.PlayerId, out var player)
                && (player.DisplayName != profile.DisplayName || player.AvatarColor != profile.AvatarColor))
            {
                player.DisplayName = profile.DisplayName;
                player.AvatarColor = profile.AvatarColor;
                changed = AdvanceSnapshot();
            }
        }
        if (changed is not null) await PublishAsync(changed, ct);
    }

    public void JoinRoom(string connectionId, string runtimeGameId, int? seatIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeGameId);
        if (seatIndex is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(seatIndex));
        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var connection))
                throw new InvalidOperationException("Only a registered connection may join a room.");
            connection.RuntimeGameId = runtimeGameId;
            connection.SeatIndex = seatIndex;
        }
    }

    public void LeaveRoom(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var connection)) return;
            connection.RuntimeGameId = null;
            connection.SeatIndex = null;
        }
    }

    public bool IsJoined(string playerId, string runtimeGameId)
    {
        lock (_gate)
        {
            return _players.TryGetValue(playerId, out var player)
                && player.Connections.Any(id =>
                    string.Equals(_connections[id].RuntimeGameId, runtimeGameId, StringComparison.Ordinal));
        }
    }

    public bool CanReceiveInvites(string playerId)
    {
        lock (_gate) return CanReceiveInvitesCore(playerId);
    }

    public LobbyPlayersSnapshot Snapshot()
    {
        lock (_gate) return SnapshotCore();
    }

    private bool CanReceiveInvitesCore(string playerId) =>
        _players.TryGetValue(playerId, out var player)
        && player.Connections.Any(id => _connections[id].LobbySubscribed);

    private static bool IsPlaceholder(string playerId) =>
        string.Equals(playerId, "offline", StringComparison.OrdinalIgnoreCase)
        || Enumerable.Range(0, 4).Any(seat =>
            string.Equals(playerId, $"bot-{seat}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(playerId, $"human-{seat}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(playerId, $"seat-{seat}", StringComparison.OrdinalIgnoreCase));

    private LobbyPlayersSnapshot AdvanceSnapshot()
    {
        _revision++;
        return SnapshotCore();
    }

    private LobbyPlayersSnapshot SnapshotCore() => new(_revision, _players
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new OnlinePlayerDto(pair.Key, pair.Value.DisplayName,
            pair.Value.AvatarColor, CanReceiveInvitesCore(pair.Key))).ToArray());

    private Task PublishAsync(LobbyPlayersSnapshot snapshot, CancellationToken ct) =>
        _hub.Clients.Group(LobbyGroup).SendAsync("LobbyPlayersChanged", snapshot, ct);

    private sealed class ConnectionPresence(string playerId)
    {
        public string PlayerId { get; } = playerId;
        public bool LobbySubscribed { get; set; }
        public string? RuntimeGameId { get; set; }
        public int? SeatIndex { get; set; }
    }

    private sealed class PlayerPresence(string displayName, string? avatarColor)
    {
        public string DisplayName { get; set; } = displayName;
        public string? AvatarColor { get; set; } = avatarColor;
        public HashSet<string> Connections { get; } = new(StringComparer.Ordinal);
    }
}
