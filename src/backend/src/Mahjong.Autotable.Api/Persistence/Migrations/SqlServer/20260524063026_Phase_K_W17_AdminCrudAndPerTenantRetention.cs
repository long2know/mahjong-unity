using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase_K_W17_AdminCrudAndPerTenantRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "SignalRSequenceEntries",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Replays",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverlapWindowDays",
                table: "PerTenantJwksRotationPolicies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ReplayRetentionPolicies",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RetentionDays = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayRetentionPolicies", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "SignalRRetentionPolicies",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RetentionMinutes = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalRRetentionPolicies", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignalRSequenceEntries_TenantId",
                table: "SignalRSequenceEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Replays_TenantId",
                table: "Replays",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplayRetentionPolicies");

            migrationBuilder.DropTable(
                name: "SignalRRetentionPolicies");

            migrationBuilder.DropIndex(
                name: "IX_SignalRSequenceEntries_TenantId",
                table: "SignalRSequenceEntries");

            migrationBuilder.DropIndex(
                name: "IX_Replays_TenantId",
                table: "Replays");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SignalRSequenceEntries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Replays");

            migrationBuilder.DropColumn(
                name: "OverlapWindowDays",
                table: "PerTenantJwksRotationPolicies");
        }
    }
}
