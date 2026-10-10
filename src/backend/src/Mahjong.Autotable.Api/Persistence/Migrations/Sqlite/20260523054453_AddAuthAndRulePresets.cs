using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Autotable.Api.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddAuthAndRulePresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RulePresetId",
                table: "ChangshaGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChangshaRulePresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    HandLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxScorePerHand = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowWashout = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowKongRobbing = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowConcealedKongPromotion = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowSevenPairs = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowChow = table.Column<bool>(type: "INTEGER", nullable: false),
                    BotDecisionTimeoutMs = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatorPlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangshaRulePresets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailMagicLinkTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestedPlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMagicLinkTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerAuthIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerAuthIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerAuthIdentities_PlayerProfiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "PlayerProfiles",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerAuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerAuthSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangshaRulePresets_Name",
                table: "ChangshaRulePresets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailMagicLinkTokens_Token",
                table: "EmailMagicLinkTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerAuthIdentities_PlayerId",
                table: "PlayerAuthIdentities",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerAuthIdentities_Provider_ProviderSubject",
                table: "PlayerAuthIdentities",
                columns: new[] { "Provider", "ProviderSubject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerAuthSessions_PlayerId",
                table: "PlayerAuthSessions",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerAuthSessions_Token",
                table: "PlayerAuthSessions",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangshaRulePresets");

            migrationBuilder.DropTable(
                name: "EmailMagicLinkTokens");

            migrationBuilder.DropTable(
                name: "PlayerAuthIdentities");

            migrationBuilder.DropTable(
                name: "PlayerAuthSessions");

            migrationBuilder.DropColumn(
                name: "RulePresetId",
                table: "ChangshaGames");
        }
    }
}
