using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class BazaarListings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PurchaseConditions",
                table: "MarketplaceListings");

            migrationBuilder.AddColumn<Guid>(
                name: "BuyerGameInstanceId",
                table: "MarketplaceListings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CollectedGold",
                table: "MarketplaceListings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "EarnedGold",
                table: "MarketplaceListings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "GoodsKey",
                table: "MarketplaceListings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "GoodsName",
                table: "MarketplaceListings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "MarketplaceListings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "MarketplaceListings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "QuantitySold",
                table: "MarketplaceListings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MarketplaceListings_Kind_Status_GoodsKey_CreatedAt",
                table: "MarketplaceListings",
                columns: new[] { "Kind", "Status", "GoodsKey", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketplaceListings_Kind_Status_GoodsKey_CreatedAt",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "BuyerGameInstanceId",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "CollectedGold",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "EarnedGold",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "GoodsKey",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "GoodsName",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "QuantitySold",
                table: "MarketplaceListings");

            migrationBuilder.AddColumn<string>(
                name: "PurchaseConditions",
                table: "MarketplaceListings",
                type: "jsonb",
                nullable: false,
                defaultValue: "");
        }
    }
}
