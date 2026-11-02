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
    public partial class AddChangshaGameReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangshaGameReplays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangshaGameReplays", x => x.Id);
                });

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
                name: "ChangshaGameReplays");
        }
    }
}
