using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class Caravans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Perfect",
                table: "PlayerQuests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CaravanPerfect",
                table: "PlayerParties",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "CaravanQuestId",
                table: "PlayerParties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CaravanWagons",
                table: "PlayerParties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "SecondAmbushAt",
                table: "PlayerParties",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Perfect",
                table: "PlayerQuests");

            migrationBuilder.DropColumn(
                name: "CaravanPerfect",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "CaravanQuestId",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "CaravanWagons",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "SecondAmbushAt",
                table: "PlayerParties");
        }
    }
}
