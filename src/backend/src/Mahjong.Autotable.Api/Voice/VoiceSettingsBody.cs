namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 3 — Bishop (Backend). Body shape for the
/// <c>POST /api/games/{id}/settings/voice</c> endpoint. The endpoint
/// is an admin/creator-only toggle that flips the per-table
/// <c>ChangshaGame.VoiceEnabled</c> column; <see cref="Enabled"/>
/// carries the desired boolean value.
/// </summary>
public sealed class VoiceSettingsBody
{
    public bool Enabled { get; set; }
}
