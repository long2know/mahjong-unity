using System.Text.Json.Serialization;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Autotable.Api.Changsha.Chat;

public sealed record TableInviteDto(
    string InviteId,
    string SenderPlayerId,
    string SenderDisplayName,
    string? SenderAvatarColor,
    string RecipientPlayerId,
    string GameId,
    string? PublicName,
    string JoinUrl,
    DateTime CreatedUtc,
    DateTime ExpiresUtc);

public sealed record TableInviteResult(
    bool Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TableInviteDto? Invite = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record LobbySnapshotDto(
    long Revision, IReadOnlyList<OnlinePlayerDto> Players, IReadOnlyList<TableInviteDto> Invites);

/// <summary>Recipient-only, short-lived invitations. Invitations never reserve a seat.</summary>
public sealed class ChatInvitationService
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);
    public const int RecipientCapacity = 50;
    public const int GlobalCapacity = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Sender, string Recipient, string Room), TableInviteDto> _pending = new();
    private readonly IChangshaGameRuntime _runtime;
    private readonly LobbyPresenceService _presence;
    private readonly ChatService _chat;
    private readonly PlayerProfileService _profiles;
    private readonly IHubContext<ChangshaHub> _hub;
    private readonly TimeProvider _time;

    public ChatInvitationService(IChangshaGameRuntime runtime, LobbyPresenceService presence,
        ChatService chat, PlayerProfileService profiles, IHubContext<ChangshaHub> hub,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime;
        _presence = presence;
        _chat = chat;
        _profiles = profiles;
        _hub = hub;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<TableInviteResult> SendAsync(
        string? senderPlayerId, string gameId, string recipientPlayerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(senderPlayerId)) return new(false, Reason: "identity-required");
        if (string.Equals(senderPlayerId, recipientPlayerId, StringComparison.Ordinal))
            return new(false, Reason: "self-invite");
        if (!PlayerIdentityService.IsValidPlayerId(recipientPlayerId))
            return new(false, Reason: "recipient-offline");

        await _gate.WaitAsync(ct);
        try
        {
            Prune(_time.GetUtcNow().UtcDateTime);
            RoomReference? room;
            try { room = await _runtime.ResolveExistingRoomAsync(gameId, ct); }
            catch (PublicRoomRecoveryException ex) when (ex.Reason == "invalid-public-room")
            {
                return new(false, Reason: "room-not-found");
            }
            if (room is null) return new(false, Reason: "room-not-found");
            var access = await _runtime.GetRoomAccessAsync(room.RuntimeGameId, senderPlayerId, ct);
            if (access is null) return new(false, Reason: "room-not-found");
            var failure = EligibilityFailure(room, access, senderPlayerId, recipientPlayerId);
            if (failure is not null) return new(false, Reason: failure);

            Prune(_time.GetUtcNow().UtcDateTime);
            var key = (senderPlayerId, recipientPlayerId, room.RoomId);
            if (_pending.TryGetValue(key, out var duplicate)) return new(true, duplicate);
            if (_pending.Count >= GlobalCapacity
                || _pending.Values.Count(invite => string.Equals(invite.RecipientPlayerId, recipientPlayerId,
                    StringComparison.Ordinal)) >= RecipientCapacity)
                return new(false, Reason: "inbox-full");

            var profile = await _profiles.GetOrCreateAsync(senderPlayerId, ct);
            // Profile I/O must not turn an already departed sender/recipient into a successful delivery.
            access = await _runtime.GetRoomAccessAsync(room.RuntimeGameId, senderPlayerId, ct);
            if (access is null) return new(false, Reason: "room-not-found");
            failure = EligibilityFailure(room, access, senderPlayerId, recipientPlayerId);
            if (failure is not null) return new(false, Reason: failure);
            if (!_chat.TryConsumeSendQuota(senderPlayerId)) return new(false, Reason: "rate-limited");

            var now = _time.GetUtcNow().UtcDateTime;
            var invite = new TableInviteDto(
                Guid.NewGuid().ToString(), senderPlayerId, profile.DisplayName, profile.AvatarColor,
                recipientPlayerId, room.RoomId, access.PublicName,
                $"/autotable/?gameId={Uri.EscapeDataString(room.RoomId)}&variant=changsha&join=1",
                now, now.Add(InviteLifetime));
            _pending.Add(key, invite);
            await _hub.Clients.Group(LobbyPresenceService.PlayerGroup(recipientPlayerId))
                .SendAsync("TableInviteReceived", invite, ct);
            return new(true, invite);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TableInviteDto>> GetInboxAsync(string recipientPlayerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recipientPlayerId);
        await _gate.WaitAsync(ct);
        try
        {
            Prune(_time.GetUtcNow().UtcDateTime);
            return _pending.Values.Where(invite =>
                    string.Equals(invite.RecipientPlayerId, recipientPlayerId, StringComparison.Ordinal))
                .OrderBy(invite => invite.CreatedUtc).ThenBy(invite => invite.InviteId, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    private void Prune(DateTime now)
    {
        foreach (var key in _pending.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
            _pending.Remove(key);
    }

    private string? EligibilityFailure(
        RoomReference room, RoomAccessSnapshot access, string senderPlayerId, string recipientPlayerId)
    {
        if (!_presence.IsJoined(senderPlayerId, room.RuntimeGameId) || !access.ViewerHasConnectedHumanSeat
            || (!access.IsPublic && !string.Equals(access.OwnerId, senderPlayerId, StringComparison.Ordinal)))
            return "not-allowed";
        if (access.Phase != ChangshaPhase.Seating) return "room-not-seating";
        if (access.OpenHumanSeats == 0) return "room-full";
        return _presence.CanReceiveInvites(recipientPlayerId) ? null : "recipient-offline";
    }
}
