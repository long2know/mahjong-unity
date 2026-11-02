using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase_K_W19_SwissPairingAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwissPairingAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TournamentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Round = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<int>(type: "integer", nullable: false),
                    White = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Black = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Tiebreaker = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwissPairingAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwissPairingAuditEntries_CreatedAtUtc",
                table: "SwissPairingAuditEntries",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SwissPairingAuditEntries_TournamentId",
                table: "SwissPairingAuditEntries",
                column: "TournamentId");

            migrationBuilder.CreateIndex(
                name: "IX_SwissPairingAuditEntries_TournamentId_Round_Board",
                table: "SwissPairingAuditEntries",
                columns: new[] { "TournamentId", "Round", "Board" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwissPairingAuditEntries");
        }
    }
}
