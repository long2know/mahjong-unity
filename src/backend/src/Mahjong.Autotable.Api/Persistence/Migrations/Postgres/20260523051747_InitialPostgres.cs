using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangshaGameReplays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangshaGameReplays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChangshaGames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleSet = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    StateJson = table.Column<string>(type: "text", nullable: false),
                    StateVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CurrentHandNumber = table.Column<int>(type: "integer", nullable: false),
                    CurrentRoundNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangshaGames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerProfiles",
                columns: table => new
                {
                    PlayerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AvatarColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerProfiles", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "ChangshaGameEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SeatIndex = table.Column<int>(type: "integer", nullable: false),
                    TurnNumber = table.Column<int>(type: "integer", nullable: false),
                    TileId = table.Column<int>(type: "integer", nullable: true),
                    Detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    HandNumber = table.Column<int>(type: "integer", nullable: false),
                    StateVersion = table.Column<int>(type: "integer", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PersistedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    PlayerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GamesPlayed = table.Column<int>(type: "integer", nullable: false),
                    GamesWon = table.Column<int>(type: "integer", nullable: false),
                    TotalScore = table.Column<long>(type: "bigint", nullable: false),
                    HighestSingleGameScore = table.Column<int>(type: "integer", nullable: false),
                    LongestWinStreak = table.Column<int>(type: "integer", nullable: false),
                    CurrentWinStreak = table.Column<int>(type: "integer", nullable: false),
                    LastGameAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
