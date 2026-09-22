using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddSiegeAssault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssaultRunId",
                table: "Sieges",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AssaultStartedAt",
                table: "Sieges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EncounterEnemyLevel",
                table: "Sieges",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EncounterWaves",
                table: "Sieges",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssaultRunId",
                table: "Sieges");

            migrationBuilder.DropColumn(
                name: "AssaultStartedAt",
                table: "Sieges");

            migrationBuilder.DropColumn(
                name: "EncounterEnemyLevel",
                table: "Sieges");

            migrationBuilder.DropColumn(
                name: "EncounterWaves",
                table: "Sieges");
        }
    }
}
