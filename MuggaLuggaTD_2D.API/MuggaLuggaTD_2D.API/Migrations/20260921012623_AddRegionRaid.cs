using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddRegionRaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegionRaids",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RegionId = table.Column<string>(type: "text", nullable: false),
                    DefenderUserId = table.Column<string>(type: "text", nullable: true),
                    RaidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttackerWon = table.Column<bool>(type: "boolean", nullable: false),
                    ResolveDamage = table.Column<int>(type: "integer", nullable: false),
                    ResolveAfter = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionRaids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegionRaids_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RegionRaids_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegionRaids_GameInstanceId_RegionId_RaidedAt",
                table: "RegionRaids",
                columns: new[] { "GameInstanceId", "RegionId", "RaidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionRaids_GameInstanceId_UserId_RegionId_RaidedAt",
                table: "RegionRaids",
                columns: new[] { "GameInstanceId", "UserId", "RegionId", "RaidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionRaids_UserId",
                table: "RegionRaids",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegionRaids");
        }
    }
}
