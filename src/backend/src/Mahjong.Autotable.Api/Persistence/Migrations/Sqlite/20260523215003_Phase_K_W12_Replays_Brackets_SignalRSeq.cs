using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Phase_K_W12_Replays_Brackets_SignalRSeq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BracketRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TournamentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchSlot = table.Column<int>(type: "INTEGER", nullable: false),
                    SeedA = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SeedB = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WinnerSeed = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BracketRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Replays",
                columns: table => new
                {
                    ReplayId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Variant = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TurnCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CompressedPayload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IngestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Replays", x => x.ReplayId);
                });

            migrationBuilder.CreateTable(
                name: "SignalRSequenceEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HubName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GroupName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalRSequenceEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BracketRecords_TournamentId",
                table: "BracketRecords",
                column: "TournamentId");

            migrationBuilder.CreateIndex(
                name: "IX_BracketRecords_TournamentId_RoundNumber_MatchSlot",
                table: "BracketRecords",
                columns: new[] { "TournamentId", "RoundNumber", "MatchSlot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Replays_ExpiresAt",
                table: "Replays",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Replays_GameId_CompletedAt",
                table: "Replays",
                columns: new[] { "GameId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalRSequenceEntries_ExpiresAt",
                table: "SignalRSequenceEntries",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SignalRSequenceEntries_HubName_ConnectionId",
                table: "SignalRSequenceEntries",
                columns: new[] { "HubName", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalRSequenceEntries_HubName_ConnectionId_Sequence",
                table: "SignalRSequenceEntries",
                columns: new[] { "HubName", "ConnectionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BracketRecords");

            migrationBuilder.DropTable(
                name: "Replays");

            migrationBuilder.DropTable(
                name: "SignalRSequenceEntries");
        }
    }
}
