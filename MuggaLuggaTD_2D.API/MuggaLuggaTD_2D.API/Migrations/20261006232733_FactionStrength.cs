using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class FactionStrength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FactionStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Faction = table.Column<int>(type: "integer", nullable: false),
                    Strength = table.Column<double>(type: "double precision", nullable: false),
                    SettledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BloodiedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastActedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactionStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FactionStates_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FactionStates_GameInstanceId_Faction",
                table: "FactionStates",
                columns: new[] { "GameInstanceId", "Faction" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FactionStates");
        }
    }
}
