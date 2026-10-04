using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class SiteRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerSiteClears",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SiteId = table.Column<string>(type: "text", nullable: false),
                    LastClearedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WorldRewardsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSiteClears", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerSiteClears_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlayerSiteClears_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSiteClears_GameInstanceId_UserId_SiteId",
                table: "PlayerSiteClears",
                columns: new[] { "GameInstanceId", "UserId", "SiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSiteClears_UserId",
                table: "PlayerSiteClears",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerSiteClears");
        }
    }
}
