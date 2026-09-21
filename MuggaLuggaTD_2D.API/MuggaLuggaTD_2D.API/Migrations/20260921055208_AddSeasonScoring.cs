using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddSeasonScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The generated defaults were 0 days, season 0 and year 1, which would have made every
            // realm that already exists expire the instant this shipped: a zero-length season that
            // started in the year 1 is over, so the next request would have closed it and reset the
            // map under everybody. Existing realms start their first season now, at the default
            // length, instead.
            migrationBuilder.AddColumn<int>(
                name: "SeasonLengthDays",
                table: "GameInstances",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "GameInstances",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "SeasonStartedAt",
                table: "GameInstances",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "timezone('utc', now())");

            migrationBuilder.CreateTable(
                name: "SeasonResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    TotalPoints = table.Column<double>(type: "double precision", nullable: false),
                    HoldingPoints = table.Column<double>(type: "double precision", nullable: false),
                    ClearingPoints = table.Column<double>(type: "double precision", nullable: false),
                    RaidingPoints = table.Column<double>(type: "double precision", nullable: false),
                    RegionsHeld = table.Column<int>(type: "integer", nullable: false),
                    SeasonStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SeasonEndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeasonResults_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeasonResults_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeasonScores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    SettledPoints = table.Column<double>(type: "double precision", nullable: false),
                    PointsPerHour = table.Column<double>(type: "double precision", nullable: false),
                    LastSettledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HoldingPoints = table.Column<double>(type: "double precision", nullable: false),
                    ClearingPoints = table.Column<double>(type: "double precision", nullable: false),
                    RaidingPoints = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeasonScores_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeasonScores_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonResults_GameInstanceId_SeasonNumber_Rank",
                table: "SeasonResults",
                columns: new[] { "GameInstanceId", "SeasonNumber", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonResults_UserId_SeasonEndedAt",
                table: "SeasonResults",
                columns: new[] { "UserId", "SeasonEndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonScores_GameInstanceId_UserId_SeasonNumber",
                table: "SeasonScores",
                columns: new[] { "GameInstanceId", "UserId", "SeasonNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeasonScores_UserId",
                table: "SeasonScores",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeasonResults");

            migrationBuilder.DropTable(
                name: "SeasonScores");

            migrationBuilder.DropColumn(
                name: "SeasonLengthDays",
                table: "GameInstances");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "GameInstances");

            migrationBuilder.DropColumn(
                name: "SeasonStartedAt",
                table: "GameInstances");
        }
    }
}
