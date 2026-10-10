using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase_K_W22_StandingsAndJwtRevoked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAtUtc",
                table: "TournamentMatches",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeLimitMinutes",
                table: "TournamentMatches",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "JwtEmergencyRevokedKids",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Kid = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JwtEmergencyRevokedKids", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TournamentStandings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TournamentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    GamesPlayed = table.Column<int>(type: "int", nullable: false),
                    FinalizedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TournamentStandings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TournamentStandings_Tournaments_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "Tournaments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JwtEmergencyRevokedKids_RevokedAtUtc",
                table: "JwtEmergencyRevokedKids",
                column: "RevokedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JwtEmergencyRevokedKids_TenantId",
                table: "JwtEmergencyRevokedKids",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_JwtEmergencyRevokedKids_TenantId_Kid",
                table: "JwtEmergencyRevokedKids",
                columns: new[] { "TenantId", "Kid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TournamentStandings_TournamentId",
                table: "TournamentStandings",
                column: "TournamentId");

            migrationBuilder.CreateIndex(
                name: "IX_TournamentStandings_TournamentId_PlayerId",
                table: "TournamentStandings",
                columns: new[] { "TournamentId", "PlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TournamentStandings_TournamentId_Rank",
                table: "TournamentStandings",
                columns: new[] { "TournamentId", "Rank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JwtEmergencyRevokedKids");

            migrationBuilder.DropTable(
                name: "TournamentStandings");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "TournamentMatches");

            migrationBuilder.DropColumn(
                name: "TimeLimitMinutes",
                table: "TournamentMatches");
        }
    }
}
