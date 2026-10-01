using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class Fortify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegionFortifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RegionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FromLevel = table.Column<int>(type: "integer", nullable: false),
                    ToLevel = table.Column<int>(type: "integer", nullable: false),
                    Spent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletesAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionFortifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegionFortifications_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegionFortifications_GameInstanceId_RegionId",
                table: "RegionFortifications",
                columns: new[] { "GameInstanceId", "RegionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionFortifications_State_CompletesAt",
                table: "RegionFortifications",
                columns: new[] { "State", "CompletesAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegionFortifications");
        }
    }
}
