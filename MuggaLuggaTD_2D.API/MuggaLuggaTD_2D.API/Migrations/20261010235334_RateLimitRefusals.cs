using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class RateLimitRefusals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RateLimitRefusals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    Policy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Route = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Who = table.Column<string>(type: "character varying(460)", maxLength: 460, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    LastAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateLimitRefusals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitRefusals_Day_Policy_Route_Who",
                table: "RateLimitRefusals",
                columns: new[] { "Day", "Policy", "Route", "Who" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RateLimitRefusals");
        }
    }
}
