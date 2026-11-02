using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Phase_K_W8_AuditEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "ReconnectAuditEntries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "ReconnectAuditEntries",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconnectAuditEntries_CorrelationId",
                table: "ReconnectAuditEntries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconnectAuditEntries_IdempotencyKey",
                table: "ReconnectAuditEntries",
                column: "IdempotencyKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReconnectAuditEntries_CorrelationId",
                table: "ReconnectAuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_ReconnectAuditEntries_IdempotencyKey",
                table: "ReconnectAuditEntries");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "ReconnectAuditEntries");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "ReconnectAuditEntries");
        }
    }
}
