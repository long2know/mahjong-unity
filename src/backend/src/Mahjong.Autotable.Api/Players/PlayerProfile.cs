namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase J Wave 5 — persistent per-player identity. The PK <see cref="PlayerId"/>
/// is the SignalR connection id used by the player at registration time (v1
/// uses ConnectionId as session identity — see Bishop's Wave 5 memo for the
/// reconnect limitation). DisplayName and AvatarColor are user-editable via
/// the <c>UpdateProfile</c> hub RPC.
/// </summary>
public sealed class PlayerProfile
{
    /// <summary>
    /// Phase J Wave 7 — class-initializer fallback for <see cref="AvatarColor"/>.
    /// Equals the FIRST entry of Hicks's documented 8-colour preset palette
    /// (<c>AVATAR_COLOR_PRESETS[0]</c> in <c>src/frontend/autotable-src/src/profile.ts</c>):
    /// a saturated red. Wave 5 had this initialised to <c>#808080</c> (grey),
    /// which is NOT a member of the user-facing palette — it rendered as a
    /// "ghost" 9th colour on the frontend chip and caused Vasquez's
    /// <c>UpdateAvatarColor_RejectsInvalid_HexFormat</c> happy-path checks to
    /// silently rely on the override path. The runtime almost always overrides
    /// this through <see cref="PlayerProfileService.DefaultAvatarColor"/>, but
    /// the property initialiser is the fallback the EF Core column metadata
    /// resolves to for newly-attached entities that skip the service helper.
    /// </summary>
    public const string DefaultPaletteAvatarColor = "#c0392b";

    public string PlayerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AvatarColor { get; set; } = DefaultPaletteAvatarColor;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
