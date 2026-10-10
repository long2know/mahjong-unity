using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase_K_W13_SpectatorHandoffAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpectatorHandoffAuditRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenJti = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ClientIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpectatorHandoffAuditRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpectatorHandoffAuditRecords_GameId_IssuedAt",
                table: "SpectatorHandoffAuditRecords",
                columns: new[] { "GameId", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SpectatorHandoffAuditRecords_IssuedAt",
                table: "SpectatorHandoffAuditRecords",
                column: "IssuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SpectatorHandoffAuditRecords_TokenJti",
                table: "SpectatorHandoffAuditRecords",
                column: "TokenJti",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpectatorHandoffAuditRecords_UserId",
                table: "SpectatorHandoffAuditRecords",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpectatorHandoffAuditRecords");
        }
    }
}
