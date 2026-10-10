using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase_K_W2_AuditKind_And_RolloverDeferral : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ReconnectAuditEntries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "reconnect.token.rotated");

            migrationBuilder.CreateTable(
                name: "PlayerSeasonRolloverDeferrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FromSeason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ToSeason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DeferredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TournamentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DrainedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSeasonRolloverDeferrals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconnectAuditEntries_Kind_At",
                table: "ReconnectAuditEntries",
                columns: new[] { "Kind", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSeasonRolloverDeferrals_PlayerId_FromSeason_Tournamen~",
                table: "PlayerSeasonRolloverDeferrals",
                columns: new[] { "PlayerId", "FromSeason", "TournamentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSeasonRolloverDeferrals_TournamentId_DrainedAtUtc",
                table: "PlayerSeasonRolloverDeferrals",
                columns: new[] { "TournamentId", "DrainedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerSeasonRolloverDeferrals");

            migrationBuilder.DropIndex(
                name: "IX_ReconnectAuditEntries_Kind_At",
                table: "ReconnectAuditEntries");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ReconnectAuditEntries");
        }
    }
}
