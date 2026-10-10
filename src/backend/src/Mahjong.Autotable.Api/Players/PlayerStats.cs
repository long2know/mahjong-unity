namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase J Wave 5 — career stats keyed by <see cref="PlayerProfile.PlayerId"/>.
/// One-to-one with <see cref="PlayerProfile"/>; cascade-deletes via the EF
/// model. All counters are monotonic non-negative; <c>LastGameAt</c> is null
/// until the player completes their first game.
/// </summary>
public sealed class PlayerStats
{
    public string PlayerId { get; set; } = string.Empty;
    public int GamesPlayed { get; set; }
    public int GamesWon { get; set; }
    public long TotalScore { get; set; }
    public int HighestSingleGameScore { get; set; }
    public int LongestWinStreak { get; set; }
    public int CurrentWinStreak { get; set; }
    public DateTime? LastGameAt { get; set; }
}
