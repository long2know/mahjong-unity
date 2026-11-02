using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class WidenChangshaReplayDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Detail",
                table: "ChangshaGameEvents",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "ChangshaGameEvents" IN ACCESS EXCLUSIVE MODE;
                DO $replay_guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ChangshaGameEvents" WHERE length("Detail") > 256) THEN
                        RAISE EXCEPTION 'Cannot narrow replay Detail while payloads exceed 256 characters';
                    END IF;
                END;
                $replay_guard$;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "Detail",
                table: "ChangshaGameEvents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
