using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class Revisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PlayerMaterials",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PlayerGold",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "MarketplaceListings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PlayerMaterials");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PlayerGold");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "MarketplaceListings");
        }
    }
}
