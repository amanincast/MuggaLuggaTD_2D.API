using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class WeeklyTrial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrialAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Week = table.Column<int>(type: "integer", nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    Hero = table.Column<int>(type: "integer", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    BegunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Kills = table.Column<int>(type: "integer", nullable: false),
                    Waves = table.Column<int>(type: "integer", nullable: false),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<long>(type: "bigint", nullable: false),
                    Reported = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Trimmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrialAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrialEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Week = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    BestScore = table.Column<long>(type: "bigint", nullable: false),
                    BestKills = table.Column<int>(type: "integer", nullable: false),
                    BestWaves = table.Column<int>(type: "integer", nullable: false),
                    BestSeconds = table.Column<int>(type: "integer", nullable: false),
                    BestHero = table.Column<int>(type: "integer", nullable: false),
                    BestAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChestRarity = table.Column<short>(type: "smallint", nullable: true),
                    FinalRank = table.Column<int>(type: "integer", nullable: true),
                    FinalTotal = table.Column<int>(type: "integer", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrialEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrialAttempts_UserId_Day",
                table: "TrialAttempts",
                columns: new[] { "UserId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_TrialAttempts_UserId_Week",
                table: "TrialAttempts",
                columns: new[] { "UserId", "Week" });

            migrationBuilder.CreateIndex(
                name: "IX_TrialEntries_Week_BestScore",
                table: "TrialEntries",
                columns: new[] { "Week", "BestScore" });

            migrationBuilder.CreateIndex(
                name: "IX_TrialEntries_Week_UserId",
                table: "TrialEntries",
                columns: new[] { "Week", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrialAttempts");

            migrationBuilder.DropTable(
                name: "TrialEntries");
        }
    }
}
