using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class FactionRaids : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FactionRaids",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Faction = table.Column<int>(type: "integer", nullable: false),
                    RegionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DefenderUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    DefenderFaction = table.Column<int>(type: "integer", nullable: false),
                    RaidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttackerWon = table.Column<bool>(type: "boolean", nullable: false),
                    March = table.Column<double>(type: "double precision", nullable: false),
                    Hold = table.Column<long>(type: "bigint", nullable: false),
                    ResolveDamage = table.Column<int>(type: "integer", nullable: false),
                    ResolveAfter = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactionRaids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FactionRaids_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FactionRaids_GameInstanceId_RaidedAt",
                table: "FactionRaids",
                columns: new[] { "GameInstanceId", "RaidedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FactionRaids");
        }
    }
}
