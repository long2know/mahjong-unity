using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCspViolations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CspViolations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DocumentUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Referrer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ViolatedDirective = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EffectiveDirective = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OriginalPolicy = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Disposition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    BlockedUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SourceFile = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LineNumber = table.Column<int>(type: "integer", nullable: true),
                    ColumnNumber = table.Column<int>(type: "integer", nullable: true),
                    ScriptSample = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RawJson = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CspViolations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CspViolations_EffectiveDirective",
                table: "CspViolations",
                column: "EffectiveDirective");

            migrationBuilder.CreateIndex(
                name: "IX_CspViolations_ReceivedAt",
                table: "CspViolations",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CspViolations");
        }
    }
}
