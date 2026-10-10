using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class Rifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    EventNumber = table.Column<int>(type: "integer", nullable: false),
                    RegionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    OmenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OpensAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosesAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InvadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BreachMax = table.Column<int>(type: "integer", nullable: false),
                    Breach = table.Column<int>(type: "integer", nullable: false),
                    SealedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SealedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rifts_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RiftContributions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Breach = table.Column<int>(type: "integer", nullable: false),
                    Wins = table.Column<int>(type: "integer", nullable: false),
                    LastWinAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChestRarity = table.Column<short>(type: "smallint", nullable: true),
                    ChestOpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiftContributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiftContributions_Rifts_RiftId",
                        column: x => x.RiftId,
                        principalTable: "Rifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RiftContributions_RiftId_UserId",
                table: "RiftContributions",
                columns: new[] { "RiftId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rifts_GameInstanceId_SeasonNumber_Kind_EventNumber",
                table: "Rifts",
                columns: new[] { "GameInstanceId", "SeasonNumber", "Kind", "EventNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rifts_GameInstanceId_Stage",
                table: "Rifts",
                columns: new[] { "GameInstanceId", "Stage" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RiftContributions");

            migrationBuilder.DropTable(
                name: "Rifts");
        }
    }
}
