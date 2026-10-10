using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.SqlServer
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
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [ChangshaGameEvents] WITH (TABLOCKX, HOLDLOCK)
                    WHERE DATALENGTH([Detail]) > 512
                )
                    THROW 51017, 'Cannot narrow replay Detail while payloads exceed 256 UTF-16 units', 1;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "Detail",
                table: "ChangshaGameEvents",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }
    }
}
