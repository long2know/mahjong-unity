namespace Mahjong.Autotable.Api.Changsha.Runtime;

public sealed record PublicRoomCreation(
    string RoomId,
    DealMode DealMode,
    string BotDifficulty,
    string? ReplacesRuntimeGameId = null,
    bool IsExplicitNew = false)
{
    public int? CreatorSeatIndex { get; init; }
}

public sealed class PublicRoomRecoveryException(string reason, Exception? innerException = null)
    : InvalidOperationException(reason, innerException)
{
    public string Reason { get; } = reason;
}
