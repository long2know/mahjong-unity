using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangshaGameReplays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangshaGameReplays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChangshaGames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleSet = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Seed = table.Column<int>(type: "INTEGER", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    StateVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    CurrentHandNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentRoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangshaGames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerProfiles",
                columns: table => new
                {
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AvatarColor = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerProfiles", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "ChangshaGameEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SeatIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    TileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    HandNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StateVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PersistedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangshaGameEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangshaGameEvents_ChangshaGames_GameId",
                        column: x => x.GameId,
                        principalTable: "ChangshaGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerStats",
                columns: table => new
                {
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GamesPlayed = table.Column<int>(type: "INTEGER", nullable: false),
                    GamesWon = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalScore = table.Column<long>(type: "INTEGER", nullable: false),
                    HighestSingleGameScore = table.Column<int>(type: "INTEGER", nullable: false),
                    LongestWinStreak = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentWinStreak = table.Column<int>(type: "INTEGER", nullable: false),
                    LastGameAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerStats", x => x.PlayerId);
                    table.ForeignKey(
                        name: "FK_PlayerStats_PlayerProfiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "PlayerProfiles",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangshaGameEvents_GameId_Sequence",
                table: "ChangshaGameEvents",
                columns: new[] { "GameId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangshaGameReplays_GameId",
                table: "ChangshaGameReplays",
                column: "GameId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangshaGameEvents");

            migrationBuilder.DropTable(
                name: "ChangshaGameReplays");

            migrationBuilder.DropTable(
                name: "PlayerStats");

            migrationBuilder.DropTable(
                name: "ChangshaGames");

            migrationBuilder.DropTable(
                name: "PlayerProfiles");
        }
    }
}
