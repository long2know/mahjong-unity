using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// ─────────────────────────────────────────────────────────────────────────
// DORMANT — DO NOT APPLY, EDIT, OR REGENERATE AGAINST THIS SET. (WP-C / #118)
//
// This root-namespace migration targets the *base* `AppDbContext` and predates
// the Phase J Wave 7 provider split. The canonical, runtime-applied migration
// sets now live under Persistence/Migrations/{Sqlite,Postgres,SqlServer}/ and
// target the concrete `SqliteAppDbContext` / `PostgresAppDbContext` /
// `SqlServerAppDbContext` subclasses. The runtime never applies this file:
// SQLite bootstraps via EnsureCreated, and Postgres/SqlServer run MigrateAsync
// against their provider-specific context.
//
// It is retained only for historical continuity. New migrations MUST be added
// with an explicit provider context, e.g.
//   dotnet ef migrations add <Name> --context SqliteAppDbContext \
//     --output-dir Persistence/Migrations/Sqlite
// See Persistence/Migrations/README.md.
// ─────────────────────────────────────────────────────────────────────────
namespace Mahjong.Autotable.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerProfileAndStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangshaGameEvents");

            migrationBuilder.DropTable(
                name: "PlayerStats");

            migrationBuilder.DropTable(
                name: "ChangshaGames");

            migrationBuilder.DropTable(
                name: "PlayerProfiles");
        }
    }
}
