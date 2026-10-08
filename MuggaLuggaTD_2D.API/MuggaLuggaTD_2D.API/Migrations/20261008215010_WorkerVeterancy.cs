using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class WorkerVeterancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "HoursWorked",
                table: "HiredWorkers",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "Keep",
                table: "HiredWorkers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LevelRolledTo",
                table: "HiredWorkers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "LifetimeOutput",
                table: "HiredWorkers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Rolls",
                table: "HiredWorkers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RollsSeen",
                table: "HiredWorkers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SeasonsServed",
                table: "HiredWorkers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing workers start with the hours they have been at their current site (spec §6). The
            // levels this crosses roll at their next settle, so testers get a batch of reveals at once.
            migrationBuilder.Sql(
                "UPDATE \"HiredWorkers\" SET \"HoursWorked\" = GREATEST(0, EXTRACT(EPOCH FROM (now() - \"AssignedAt\")) / 3600.0) " +
                "WHERE \"AssignedAt\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HoursWorked",
                table: "HiredWorkers");

            migrationBuilder.DropColumn(
                name: "Keep",
                table: "HiredWorkers");

            migrationBuilder.DropColumn(
                name: "LevelRolledTo",
                table: "HiredWorkers");

            migrationBuilder.DropColumn(
                name: "LifetimeOutput",
                table: "HiredWorkers");

            migrationBuilder.DropColumn(
                name: "Rolls",
                table: "HiredWorkers");

            migrationBuilder.DropColumn(
                name: "RollsSeen",
                table: "HiredWorkers");

            migrationBuilder.DropColumn(
                name: "SeasonsServed",
                table: "HiredWorkers");
        }
    }
}
