namespace Mahjong.Autotable.Api.Data.Entities;

public sealed class AutotableRoomBinding
{
    // A digest key preserves ordinal room identity on case-insensitive providers.
    public required string RoomKey { get; set; }
    public required string RoomId { get; set; }
    public Guid RuntimeGameId { get; set; }
    public DateTime CreatedUtc { get; set; }
}
