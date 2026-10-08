using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class SeasonEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChestItemName",
                table: "SeasonResults",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChestOpenedAt",
                table: "SeasonResults",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "ChestRarity",
                table: "SeasonResults",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SeenAt",
                table: "SeasonResults",
                type: "timestamp with time zone",
                nullable: true);

            // Seasons closed before the end page existed were already told, as a status line; they
            // must not open as an unseen page on everyone's next return.
            migrationBuilder.Sql("UPDATE \"SeasonResults\" SET \"SeenAt\" = \"SeasonEndedAt\";");

            migrationBuilder.CreateTable(
                name: "FactionSeasonScores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    Faction = table.Column<int>(type: "integer", nullable: false),
                    SettledPoints = table.Column<double>(type: "double precision", nullable: false),
                    PointsPerHour = table.Column<double>(type: "double precision", nullable: false),
                    LastSettledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: true),
                    RegionsHeld = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactionSeasonScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FactionSeasonScores_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FactionSeasonScores_GameInstanceId_SeasonNumber_Faction",
                table: "FactionSeasonScores",
                columns: new[] { "GameInstanceId", "SeasonNumber", "Faction" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FactionSeasonScores");

            migrationBuilder.DropColumn(
                name: "ChestItemName",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "ChestOpenedAt",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "ChestRarity",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "SeenAt",
                table: "SeasonResults");
        }
    }
}
