using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddMatchHistoryAndRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForfeitedByDisconnect",
                table: "TournamentMatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ForfeitedPlayerId",
                table: "TournamentMatches",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlayerGameHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeatIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalScore = table.Column<int>(type: "INTEGER", nullable: false),
                    Won = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OpponentPlayerIdsCsv = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RulePresetId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerGameHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerRatingHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Season = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EloRating = table.Column<int>(type: "INTEGER", nullable: false),
                    GamesPlayed = table.Column<int>(type: "INTEGER", nullable: false),
                    FrozenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRatingHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerRatings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Season = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EloRating = table.Column<int>(type: "INTEGER", nullable: false),
                    GamesPlayed = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRatings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerGameHistory_PlayerId_CompletedAt",
                table: "PlayerGameHistory",
                columns: new[] { "PlayerId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerGameHistory_PlayerId_GameId",
                table: "PlayerGameHistory",
                columns: new[] { "PlayerId", "GameId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRatingHistory_PlayerId_Season",
                table: "PlayerRatingHistory",
                columns: new[] { "PlayerId", "Season" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRatingHistory_Season",
                table: "PlayerRatingHistory",
                column: "Season");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRatings_PlayerId_Season",
                table: "PlayerRatings",
                columns: new[] { "PlayerId", "Season" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRatings_Season",
                table: "PlayerRatings",
                column: "Season");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerGameHistory");

            migrationBuilder.DropTable(
                name: "PlayerRatingHistory");

            migrationBuilder.DropTable(
                name: "PlayerRatings");

            migrationBuilder.DropColumn(
                name: "ForfeitedByDisconnect",
                table: "TournamentMatches");

            migrationBuilder.DropColumn(
                name: "ForfeitedPlayerId",
                table: "TournamentMatches");
        }
    }
}
