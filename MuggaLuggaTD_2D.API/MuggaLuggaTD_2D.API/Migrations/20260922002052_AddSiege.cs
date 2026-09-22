using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddSiege : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sieges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    AttackerUserId = table.Column<string>(type: "text", nullable: false),
                    DefenderUserId = table.Column<string>(type: "text", nullable: false),
                    RegionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ArmyCharacterIdsJson = table.Column<string>(type: "text", nullable: false),
                    MarchingPower = table.Column<double>(type: "double precision", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    DeclaredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MusterEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssaultEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FrozenHold = table.Column<long>(type: "bigint", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sieges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sieges_AspNetUsers_AttackerUserId",
                        column: x => x.AttackerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Sieges_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sieges_AttackerUserId",
                table: "Sieges",
                column: "AttackerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sieges_GameInstanceId_AttackerUserId_State",
                table: "Sieges",
                columns: new[] { "GameInstanceId", "AttackerUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Sieges_GameInstanceId_RegionId_State",
                table: "Sieges",
                columns: new[] { "GameInstanceId", "RegionId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Sieges_State_AssaultEndsAt",
                table: "Sieges",
                columns: new[] { "State", "AssaultEndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sieges_State_MusterEndsAt",
                table: "Sieges",
                columns: new[] { "State", "MusterEndsAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sieges");
        }
    }
}
