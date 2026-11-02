using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase_K_W21_RotationScheduleAndReplayRestoration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReplayRestorationAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplayId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperatorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DetailMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    AttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayRestorationAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RotationSchedules",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CronExpression = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RotationSchedules", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReplayRestorationAttempts_AttemptedAtUtc",
                table: "ReplayRestorationAttempts",
                column: "AttemptedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReplayRestorationAttempts_ReplayId_AttemptedAtUtc",
                table: "ReplayRestorationAttempts",
                columns: new[] { "ReplayId", "AttemptedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RotationSchedules_Enabled",
                table: "RotationSchedules",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_RotationSchedules_UpdatedAtUtc",
                table: "RotationSchedules",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplayRestorationAttempts");

            migrationBuilder.DropTable(
                name: "RotationSchedules");
        }
    }
}
