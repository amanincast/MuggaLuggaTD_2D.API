using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class FactionSieges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FactionSieges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    Faction = table.Column<int>(type: "integer", nullable: false),
                    RegionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DefenderUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    March = table.Column<double>(type: "double precision", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    DeclaredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MusterEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FrozenHold = table.Column<long>(type: "bigint", nullable: true),
                    D20Roll = table.Column<int>(type: "integer", nullable: false),
                    Captured = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortieRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortieStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortieArmyJson = table.Column<string>(type: "text", nullable: false),
                    SortiePower = table.Column<double>(type: "double precision", nullable: false),
                    SortieEnemyLevel = table.Column<int>(type: "integer", nullable: false),
                    SortieWaves = table.Column<int>(type: "integer", nullable: false),
                    SortieWon = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactionSieges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FactionSieges_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FactionSieges_GameInstanceId_State",
                table: "FactionSieges",
                columns: new[] { "GameInstanceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_FactionSieges_State_MusterEndsAt",
                table: "FactionSieges",
                columns: new[] { "State", "MusterEndsAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FactionSieges");
        }
    }
}
