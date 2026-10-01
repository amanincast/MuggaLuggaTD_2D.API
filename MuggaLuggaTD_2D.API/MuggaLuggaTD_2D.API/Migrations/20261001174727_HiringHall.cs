using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class HiringHall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HiredWorkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Trade = table.Column<short>(type: "smallint", nullable: false),
                    Tier = table.Column<short>(type: "smallint", nullable: false),
                    Traits = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SecondTrade = table.Column<short>(type: "smallint", nullable: true),
                    HomeBiome = table.Column<int>(type: "integer", nullable: false),
                    Look = table.Column<int>(type: "integer", nullable: false),
                    HiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RatePerHour = table.Column<double>(type: "double precision", nullable: false),
                    LastSettledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Carry = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiredWorkers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HiredWorkers_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HiringCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Trade = table.Column<short>(type: "smallint", nullable: false),
                    Tier = table.Column<short>(type: "smallint", nullable: false),
                    Traits = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SecondTrade = table.Column<short>(type: "smallint", nullable: true),
                    HomeBiome = table.Column<int>(type: "integer", nullable: false),
                    Look = table.Column<int>(type: "integer", nullable: false),
                    RolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiringCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HiringCandidates_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HiringStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RefreshesSinceClear = table.Column<int>(type: "integer", nullable: false),
                    LastArrivalAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiringStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HiringStates_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HiredWorkers_GameInstanceId_SiteId",
                table: "HiredWorkers",
                columns: new[] { "GameInstanceId", "SiteId" });

            migrationBuilder.CreateIndex(
                name: "IX_HiredWorkers_GameInstanceId_UserId",
                table: "HiredWorkers",
                columns: new[] { "GameInstanceId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_HiringCandidates_GameInstanceId_UserId_Slot",
                table: "HiringCandidates",
                columns: new[] { "GameInstanceId", "UserId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HiringStates_GameInstanceId_UserId",
                table: "HiringStates",
                columns: new[] { "GameInstanceId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HiredWorkers");

            migrationBuilder.DropTable(
                name: "HiringCandidates");

            migrationBuilder.DropTable(
                name: "HiringStates");
        }
    }
}
