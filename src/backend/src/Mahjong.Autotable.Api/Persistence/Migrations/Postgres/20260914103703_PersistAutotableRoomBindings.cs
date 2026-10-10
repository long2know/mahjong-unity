using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class PersistAutotableRoomBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutotableRoomBindings",
                columns: table => new
                {
                    RoomKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoomId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuntimeGameId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutotableRoomBindings", x => x.RoomKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutotableRoomBindings_RuntimeGameId",
                table: "AutotableRoomBindings",
                column: "RuntimeGameId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutotableRoomBindings");
        }
    }
}
