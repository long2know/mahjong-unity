using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Phase_K_W11_CommentaryRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommentaryRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Speaker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    EmotionIntensity = table.Column<double>(type: "REAL", nullable: false),
                    TileReferencesJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentaryRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommentaryRecords_ExpiresAtUtc",
                table: "CommentaryRecords",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommentaryRecords_GameId_GeneratedAtUtc",
                table: "CommentaryRecords",
                columns: new[] { "GameId", "GeneratedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CommentaryRecords_GeneratedAtUtc",
                table: "CommentaryRecords",
                column: "GeneratedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommentaryRecords");
        }
    }
}
