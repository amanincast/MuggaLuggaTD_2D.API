using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AutoFight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AutoFightCount",
                table: "PlayerParties",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "AutoMode",
                table: "PlayerParties",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoOrder",
                table: "PlayerParties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AutoRecentJson",
                table: "PlayerParties",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AutoRegionId",
                table: "PlayerParties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoSettledAt",
                table: "PlayerParties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoStatus",
                table: "PlayerParties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoStepEndsAt",
                table: "PlayerParties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoTargetSiteId",
                table: "PlayerParties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutoFightReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Skirmish = table.Column<bool>(type: "boolean", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    FighterIdsJson = table.Column<string>(type: "text", nullable: false),
                    Experience = table.Column<long>(type: "bigint", nullable: false),
                    Gold = table.Column<long>(type: "bigint", nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    MaterialsJson = table.Column<string>(type: "text", nullable: false),
                    ProvisionsJson = table.Column<string>(type: "text", nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoFightReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoFightReports_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutoFightReports_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BloodiedCharacters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CharacterId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RecoversAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BloodiedCharacters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BloodiedCharacters_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BloodiedCharacters_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutoFightReports_GameInstanceId_UserId_CollectedAt",
                table: "AutoFightReports",
                columns: new[] { "GameInstanceId", "UserId", "CollectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutoFightReports_UserId",
                table: "AutoFightReports",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BloodiedCharacters_GameInstanceId_UserId_CharacterId",
                table: "BloodiedCharacters",
                columns: new[] { "GameInstanceId", "UserId", "CharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BloodiedCharacters_UserId",
                table: "BloodiedCharacters",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoFightReports");

            migrationBuilder.DropTable(
                name: "BloodiedCharacters");

            migrationBuilder.DropColumn(
                name: "AutoFightCount",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoMode",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoOrder",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoRecentJson",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoRegionId",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoSettledAt",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoStatus",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoStepEndsAt",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AutoTargetSiteId",
                table: "PlayerParties");
        }
    }
}
