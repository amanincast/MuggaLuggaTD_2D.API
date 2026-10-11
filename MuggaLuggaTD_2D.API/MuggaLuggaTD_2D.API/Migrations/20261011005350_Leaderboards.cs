using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class Leaderboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SeasonResults_GameInstances_GameInstanceId",
                table: "SeasonResults");

            migrationBuilder.AddColumn<DateTime>(
                name: "JoinedAt",
                table: "SeasonScores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Kills",
                table: "SeasonScores",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<Guid>(
                name: "GameInstanceId",
                table: "SeasonResults",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<double>(
                name: "AverageRate",
                table: "SeasonResults",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "Crowned",
                table: "SeasonResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "EndedMonth",
                table: "SeasonResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "HoursInSeason",
                table: "SeasonResults",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "PlayerName",
                table: "SeasonResults",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayersInSeason",
                table: "SeasonResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Qualifies",
                table: "SeasonResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RealmName",
                table: "SeasonResults",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonLengthDays",
                table: "SeasonResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LeaderboardSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Board = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Bracket = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaderboardSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlayerTallies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Day = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kills = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlayerTallies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonResults_Crowned_UserId",
                table: "SeasonResults",
                columns: new[] { "Crowned", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonResults_EndedMonth_Qualifies_AverageRate",
                table: "SeasonResults",
                columns: new[] { "EndedMonth", "Qualifies", "AverageRate" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardSnapshots_Board_Bracket",
                table: "LeaderboardSnapshots",
                columns: new[] { "Board", "Bracket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlayerTallies_Day",
                table: "SlayerTallies",
                column: "Day");

            migrationBuilder.CreateIndex(
                name: "IX_SlayerTallies_UserId_Day",
                table: "SlayerTallies",
                columns: new[] { "UserId", "Day" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SeasonResults_GameInstances_GameInstanceId",
                table: "SeasonResults",
                column: "GameInstanceId",
                principalTable: "GameInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Backfill the seasons already closed, so the boards and the crowns read them at once. The length
            // is taken from the season's own dates, so a season the debug bell cut short is short.
            migrationBuilder.Sql(@"
UPDATE ""SeasonResults"" r SET ""RealmName"" = LEFT(g.""Name"", 64)
FROM ""GameInstances"" g WHERE g.""Id"" = r.""GameInstanceId"";

UPDATE ""SeasonResults"" r SET ""PlayerName"" = LEFT(COALESCE(NULLIF(TRIM(u.""DisplayName""), ''), u.""UserName""), 64)
FROM ""AspNetUsers"" u WHERE u.""Id"" = r.""UserId"";

UPDATE ""SeasonResults"" SET
    ""HoursInSeason"" = GREATEST(0, EXTRACT(EPOCH FROM (""SeasonEndedAt"" - ""SeasonStartedAt"")) / 3600.0),
    ""SeasonLengthDays"" = GREATEST(0, ROUND(EXTRACT(EPOCH FROM (""SeasonEndedAt"" - ""SeasonStartedAt"")) / 86400.0))::int,
    ""EndedMonth"" = (EXTRACT(YEAR FROM ""SeasonEndedAt"") * 100 + EXTRACT(MONTH FROM ""SeasonEndedAt""))::int;

UPDATE ""SeasonResults"" SET ""AverageRate"" =
    CASE WHEN ""HoursInSeason"" > 0 AND ""TotalPoints"" > 0 THEN ROUND((""TotalPoints"" / ""HoursInSeason"")::numeric, 2) ELSE 0 END;

UPDATE ""SeasonResults"" r SET ""PlayersInSeason"" = c.n + COALESCE((SELECT COUNT(*) FROM ""FactionSeasonScores"" f
        WHERE f.""GameInstanceId"" = c.""GameInstanceId"" AND f.""SeasonNumber"" = c.""SeasonNumber"" AND f.""Rank"" IS NOT NULL), 0)
FROM (SELECT ""GameInstanceId"", ""SeasonNumber"", COUNT(*) AS n FROM ""SeasonResults"" GROUP BY ""GameInstanceId"", ""SeasonNumber"") c
WHERE c.""GameInstanceId"" = r.""GameInstanceId"" AND c.""SeasonNumber"" = r.""SeasonNumber"";

UPDATE ""SeasonResults"" r SET ""Crowned"" = TRUE
WHERE r.""Rank"" = 1 AND (
    EXISTS (SELECT 1 FROM ""SeasonResults"" o WHERE o.""GameInstanceId"" = r.""GameInstanceId""
            AND o.""SeasonNumber"" = r.""SeasonNumber"" AND o.""UserId"" <> r.""UserId"")
 OR EXISTS (SELECT 1 FROM ""FactionSeasonScores"" f WHERE f.""GameInstanceId"" = r.""GameInstanceId""
            AND f.""SeasonNumber"" = r.""SeasonNumber"" AND f.""Rank"" IS NOT NULL));

UPDATE ""SeasonResults"" SET ""Qualifies"" =
    (""SeasonLengthDays"" >= 7 AND ""HoursInSeason"" >= 48 AND ""RegionsHeld"" >= 1);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SeasonResults_GameInstances_GameInstanceId",
                table: "SeasonResults");

            migrationBuilder.DropTable(
                name: "LeaderboardSnapshots");

            migrationBuilder.DropTable(
                name: "SlayerTallies");

            migrationBuilder.DropIndex(
                name: "IX_SeasonResults_Crowned_UserId",
                table: "SeasonResults");

            migrationBuilder.DropIndex(
                name: "IX_SeasonResults_EndedMonth_Qualifies_AverageRate",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "JoinedAt",
                table: "SeasonScores");

            migrationBuilder.DropColumn(
                name: "Kills",
                table: "SeasonScores");

            migrationBuilder.DropColumn(
                name: "AverageRate",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "Crowned",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "EndedMonth",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "HoursInSeason",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "PlayerName",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "PlayersInSeason",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "Qualifies",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "RealmName",
                table: "SeasonResults");

            migrationBuilder.DropColumn(
                name: "SeasonLengthDays",
                table: "SeasonResults");

            migrationBuilder.AlterColumn<Guid>(
                name: "GameInstanceId",
                table: "SeasonResults",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SeasonResults_GameInstances_GameInstanceId",
                table: "SeasonResults",
                column: "GameInstanceId",
                principalTable: "GameInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
