using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddGameInstanceAccessTypeAndCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccessType",
                table: "GameInstances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Capacity",
                table: "GameInstances",
                type: "integer",
                nullable: false,
                defaultValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessType",
                table: "GameInstances");

            migrationBuilder.DropColumn(
                name: "Capacity",
                table: "GameInstances");
        }
    }
}
