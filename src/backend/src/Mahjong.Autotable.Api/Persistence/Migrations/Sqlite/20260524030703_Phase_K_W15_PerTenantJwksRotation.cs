using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Phase_K_W15_PerTenantJwksRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PerTenantJwksRotationPolicies",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RotationStartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RotationCompleteUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActiveKid = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PreviousKid = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerTenantJwksRotationPolicies", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PerTenantJwksRotationPolicies_RotationCompleteUtc",
                table: "PerTenantJwksRotationPolicies",
                column: "RotationCompleteUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PerTenantJwksRotationPolicies_RotationStartUtc",
                table: "PerTenantJwksRotationPolicies",
                column: "RotationStartUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PerTenantJwksRotationPolicies");
        }
    }
}
