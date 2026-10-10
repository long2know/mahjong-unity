namespace Mahjong.Autotable.Api.Changsha.Chat;

public sealed record ChatMessageDto(
    string Id,
    string GameId,
    string Channel,
    string SenderPlayerId,
    string SenderDisplayName,
    string? SenderAvatarColor,
    string? RecipientPlayerId,
    string Body,
    DateTime SentUtc)
{
    public string PlayerId => SenderPlayerId;
    public DateTime At => SentUtc;
}

public sealed class ChatAccessException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}
