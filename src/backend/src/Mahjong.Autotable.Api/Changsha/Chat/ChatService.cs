using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Changsha.Chat;

/// <summary>
/// Phase J Wave 9 — server-side table chat. Validates inbound messages,
/// rate-limits a sender's burst, persists each accepted message, and
/// returns the persisted row so the caller (hub or REST controller) can
/// broadcast it.
///
/// <para><b>Rate limit.</b> A sliding 30-second window with a 6-message
/// cap per <c>playerId</c>. Implemented in-memory (single-process
/// authoritative server is the autotable deployment model). The 7th
/// send within the window is rejected with
/// <see cref="ChatSendOutcome.RateLimited"/>.</para>
///
/// <para><b>Profanity.</b> Delegated to <see cref="ChatContentFilter"/>,
/// which substitutes banned tokens with asterisk runs of the same
/// length. The message is still persisted (so chat history stays
/// continuous), but the persisted body never contains the original
/// profanity. Operators extend the catalog at runtime via
/// <see cref="AddProfanity"/>; the canonical seed is intentionally
/// minimal so the wave-9 surface ships a contract without committing
/// to a real moderation policy.</para>
/// </summary>
public sealed class ChatService
{
    public const int RateLimitWindowSeconds = 30;
    public const int RateLimitMaxMessages = 6;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChatContentFilter _filter;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _windows = new();
    private readonly IChangshaGameRuntime? _runtime;
    private readonly LobbyPresenceService? _presence;
    private readonly PlayerProfileService? _profiles;
    private readonly TimeProvider _time;

    public ChatService(IServiceScopeFactory scopeFactory, ChatContentFilter filter,
        IChangshaGameRuntime? runtime = null, LobbyPresenceService? presence = null,
        PlayerProfileService? profiles = null, TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _filter = filter;
        _runtime = runtime;
        _presence = presence;
        _profiles = profiles;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validate + persist a chat message. Returns the persisted row +
    /// outcome. Outcomes other than <see cref="ChatSendOutcome.Ok"/>
    /// return a null message.
    /// </summary>
    public async Task<(ChatSendOutcome outcome, ChatMessage? message)> SendAsync(string gameId, string playerId, string body, string channel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(playerId))
            return (ChatSendOutcome.Invalid, null);
        if (string.IsNullOrWhiteSpace(body))
            return (ChatSendOutcome.Invalid, null);
        var trimmed = body.Trim();
        if (trimmed.Length > ChatMessage.MaxBodyLength)
            return (ChatSendOutcome.TooLong, null);
        if (!TryResolveChannel(channel, null, out var resolvedChannel))
            return (ChatSendOutcome.Invalid, null);

        // Sanitize first — banned tokens are masked rather than rejected,
        // so the conversation continues but the persisted body / audit
        // log never contains the original profanity.
        var sanitized = _filter.Sanitize(trimmed);

        if (!TryConsumeSendQuota(playerId))
            return (ChatSendOutcome.RateLimited, null);

        var row = new ChatMessage
        {
            GameId = gameId,
            PlayerId = playerId,
            Body = sanitized,
            Channel = resolvedChannel!.Stored,
            At = _time.GetUtcNow().UtcDateTime,
        };
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChatMessages.Add(row);
        await db.SaveChangesAsync(ct);
        return (ChatSendOutcome.Ok, row);
    }

    /// <summary>
    /// Reads the last <paramref name="limit"/> messages for
    /// <paramref name="gameId"/>, oldest first, optionally filtered to
    /// rows newer than <paramref name="since"/>.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> BackfillAsync(string gameId, DateTime? since, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return Array.Empty<ChatMessage>();
        limit = Math.Clamp(limit, 1, 200);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.ChatMessages.AsNoTracking().Where(m => m.GameId == gameId);
        if (since.HasValue)
            query = query.Where(m => m.At > since.Value);
        // Take most recent N, but return chronological — matches the
        // ascending order a client expects when appending to a buffer.
        var rows = await query.OrderByDescending(m => m.At).Take(limit).ToListAsync(ct);
        rows.Reverse();
        return rows;
    }

    public void AddProfanity(string word) => _filter.Add(word);

    public async Task<(ChatSendOutcome Outcome, ChatMessageDto? Message)> SendForMemberAsync(
        RoomReference room, string playerId, string body, string? channel,
        string? recipientPlayerId, CancellationToken ct = default)
    {
        if (!TryResolveChannel(channel, recipientPlayerId, out var resolved))
            return (ChatSendOutcome.Invalid, null);
        var access = await RequireMembershipAsync(room, playerId, ct);
        if (resolved!.Channel == "spectators" && access.ViewerHasConnectedHumanSeat)
            return (ChatSendOutcome.NotAllowed, null);
        if (resolved.RecipientPlayerId is { } recipient && !Presence.IsJoined(recipient, room.RuntimeGameId))
            return (ChatSendOutcome.RecipientNotJoined, null);

        var (outcome, row) = await SendAsync(room.RuntimeGameId, playerId, body, resolved.Stored, ct);
        if (row is null) return (outcome, null);
        var profile = await Profiles.GetOrCreateAsync(playerId, ct);
        return (outcome, ToDto(row, room.RoomId, profile));
    }

    public async Task<IReadOnlyList<ChatMessageDto>> BackfillForMemberAsync(
        RoomReference room, string playerId, DateTime? since, int limit, CancellationToken ct = default)
    {
        var access = await RequireMembershipAsync(room, playerId, ct);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Force exact public-alias and identity comparisons on all supported stores.
        var collation = db.Database.IsSqlServer() ? "Latin1_General_100_BIN2"
            : db.Database.IsNpgsql() ? "C"
            : db.Database.IsSqlite() ? "BINARY"
            : throw new NotSupportedException("Chat history requires a supported relational provider.");
        var spectator = !access.ViewerHasConnectedHumanSeat;
        var query = db.ChatMessages.AsNoTracking().Where(message =>
            EF.Functions.Collate(message.GameId, collation) == room.RuntimeGameId
            || EF.Functions.Collate(message.GameId, collation) == room.RoomId);
        query = query.Where(message =>
            message.Channel.ToLower() == "table"
            || (spectator && (message.Channel.ToLower() == "spectator" || message.Channel.ToLower() == "spectators"))
            || (message.Channel.Length > 8 && message.Channel.ToLower().StartsWith("private:")
                && (EF.Functions.Collate(message.PlayerId, collation) == playerId
                    || EF.Functions.Collate(message.Channel.Substring(8), collation) == playerId)));
        if (since.HasValue) query = query.Where(message => message.At > since.Value);
        var rows = await query.OrderByDescending(message => message.At).ThenByDescending(message => message.Id)
            .Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        rows.Reverse();

        var profiles = new Dictionary<string, PlayerProfile>(StringComparer.Ordinal);
        foreach (var sender in rows.Select(row => row.PlayerId).Distinct(StringComparer.Ordinal))
            profiles.Add(sender, await Profiles.GetOrCreateAsync(sender, ct));
        return rows.Select(row => ToDto(row, room.RoomId, profiles[row.PlayerId])).ToArray();
    }

    private async Task<RoomAccessSnapshot> RequireMembershipAsync(RoomReference room, string playerId, CancellationToken ct)
    {
        if (!Presence.IsJoined(playerId, room.RuntimeGameId))
            throw new ChatAccessException("not-joined");
        var runtime = _runtime ?? throw new InvalidOperationException("Room chat runtime is not configured.");
        return await runtime.GetRoomAccessAsync(room.RuntimeGameId, playerId, ct)
            ?? throw new ChatAccessException("room-not-found");
    }

    private LobbyPresenceService Presence =>
        _presence ?? throw new InvalidOperationException("Room chat presence is not configured.");
    private PlayerProfileService Profiles =>
        _profiles ?? throw new InvalidOperationException("Room chat profiles are not configured.");

    private static ChatMessageDto ToDto(ChatMessage row, string roomId, PlayerProfile profile)
    {
        if (!TryResolveChannel(row.Channel, null, out var channel))
            throw new InvalidOperationException("Stored chat message has an invalid channel.");
        return new ChatMessageDto(row.Id.ToString(), roomId, channel!.Channel,
            row.PlayerId, profile.DisplayName, profile.AvatarColor, channel.RecipientPlayerId,
            row.Body, DateTime.SpecifyKind(row.At, DateTimeKind.Utc));
    }

    public bool TryConsumeSendQuota(string playerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        var window = _windows.GetOrAdd(playerId, _ => new Queue<DateTime>());
        lock (window)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var cutoff = now.AddSeconds(-RateLimitWindowSeconds);
            while (window.Count > 0 && window.Peek() <= cutoff)
            {
                window.Dequeue();
            }
            if (window.Count >= RateLimitMaxMessages)
                return false;
            window.Enqueue(now);
            return true;
        }
    }

    private sealed record ResolvedChannel(string Channel, string Stored, string? RecipientPlayerId);

    private static bool TryResolveChannel(string? channel, string? recipientPlayerId, out ResolvedChannel? result)
    {
        result = null;
        var value = string.IsNullOrWhiteSpace(channel) ? "table" : channel.Trim();
        if (value.StartsWith("private:", StringComparison.OrdinalIgnoreCase))
        {
            var encodedRecipient = value[8..];
            if (recipientPlayerId is not null
                && !string.Equals(recipientPlayerId, encodedRecipient, StringComparison.Ordinal))
                return false;
            recipientPlayerId = encodedRecipient;
            value = "private";
        }
        if (value.Equals("private", StringComparison.OrdinalIgnoreCase))
        {
            if (!PlayerIdentityService.IsValidPlayerId(recipientPlayerId)) return false;
            result = new("private", $"private:{recipientPlayerId}", recipientPlayerId);
            return true;
        }
        if (recipientPlayerId is not null) return false;
        if (value.Equals("table", StringComparison.OrdinalIgnoreCase))
            result = new("table", "table", null);
        else if (value.Equals("spectator", StringComparison.OrdinalIgnoreCase)
            || value.Equals("spectators", StringComparison.OrdinalIgnoreCase))
            result = new("spectators", "spectator", null);
        return result is not null;
    }
}

public enum ChatSendOutcome
{
    Ok,
    Invalid,
    TooLong,
    Filtered,
    RateLimited,
    NotAllowed,
    RecipientNotJoined,
}
