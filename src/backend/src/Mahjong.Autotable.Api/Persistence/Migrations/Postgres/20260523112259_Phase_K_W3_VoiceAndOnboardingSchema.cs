using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase_K_W3_VoiceAndOnboardingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ToSeason",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "ToSeasonId");

            migrationBuilder.RenameColumn(
                name: "FromSeason",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "FromSeasonId");

            migrationBuilder.RenameColumn(
                name: "DrainedAtUtc",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "ResolvedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_PlayerSeasonRolloverDeferrals_TournamentId_DrainedAtUtc",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "IX_PlayerSeasonRolloverDeferrals_TournamentId_ResolvedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_PlayerSeasonRolloverDeferrals_PlayerId_FromSeason_Tournamen~",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "IX_PlayerSeasonRolloverDeferrals_PlayerId_FromSeasonId_Tournam~");

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "ReconnectAuditEntries",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerPlayerId",
                table: "ChangshaGames",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VoiceEnabled",
                table: "ChangshaGames",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PlayerOnboardingStatuses",
                columns: table => new
                {
                    PlayerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    StepsCompleted = table.Column<int>(type: "integer", nullable: false),
                    LastStepCompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerOnboardingStatuses", x => x.PlayerId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerOnboardingStatuses");

            migrationBuilder.DropColumn(
                name: "Detail",
                table: "ReconnectAuditEntries");

            migrationBuilder.DropColumn(
                name: "OwnerPlayerId",
                table: "ChangshaGames");

            migrationBuilder.DropColumn(
                name: "VoiceEnabled",
                table: "ChangshaGames");

            migrationBuilder.RenameColumn(
                name: "ToSeasonId",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "ToSeason");

            migrationBuilder.RenameColumn(
                name: "ResolvedAtUtc",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "DrainedAtUtc");

            migrationBuilder.RenameColumn(
                name: "FromSeasonId",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "FromSeason");

            migrationBuilder.RenameIndex(
                name: "IX_PlayerSeasonRolloverDeferrals_TournamentId_ResolvedAtUtc",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "IX_PlayerSeasonRolloverDeferrals_TournamentId_DrainedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_PlayerSeasonRolloverDeferrals_PlayerId_FromSeasonId_Tournam~",
                table: "PlayerSeasonRolloverDeferrals",
                newName: "IX_PlayerSeasonRolloverDeferrals_PlayerId_FromSeason_Tournamen~");
        }
    }
}
