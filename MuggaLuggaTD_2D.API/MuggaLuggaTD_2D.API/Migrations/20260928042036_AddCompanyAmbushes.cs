using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyAmbushes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AmbushAt",
                table: "PlayerParties",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AmbushRunId",
                table: "PlayerParties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AmbushRunStartedAt",
                table: "PlayerParties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HaltedAt",
                table: "PlayerParties",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmbushAt",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AmbushRunId",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "AmbushRunStartedAt",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "HaltedAt",
                table: "PlayerParties");
        }
    }
}
