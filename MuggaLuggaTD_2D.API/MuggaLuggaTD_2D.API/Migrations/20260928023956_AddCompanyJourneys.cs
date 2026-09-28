using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyJourneys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArrivesAt",
                table: "PlayerParties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DepartedAt",
                table: "PlayerParties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromSiteId",
                table: "PlayerParties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteJson",
                table: "PlayerParties",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToSiteId",
                table: "PlayerParties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArrivesAt",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "DepartedAt",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "FromSiteId",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "RouteJson",
                table: "PlayerParties");

            migrationBuilder.DropColumn(
                name: "ToSiteId",
                table: "PlayerParties");
        }
    }
}
