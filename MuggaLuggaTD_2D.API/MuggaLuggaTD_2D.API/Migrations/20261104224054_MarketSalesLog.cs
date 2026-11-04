using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class MarketSalesLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuyerId",
                table: "MarketSales",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BuyerRealm",
                table: "MarketSales",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoodsName",
                table: "MarketSales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SellerId",
                table: "MarketSales",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SellerRealm",
                table: "MarketSales",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketSales_BuyerId_SoldAt",
                table: "MarketSales",
                columns: new[] { "BuyerId", "SoldAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketSales_SellerId_SoldAt",
                table: "MarketSales",
                columns: new[] { "SellerId", "SoldAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketSales_BuyerId_SoldAt",
                table: "MarketSales");

            migrationBuilder.DropIndex(
                name: "IX_MarketSales_SellerId_SoldAt",
                table: "MarketSales");

            migrationBuilder.DropColumn(
                name: "BuyerId",
                table: "MarketSales");

            migrationBuilder.DropColumn(
                name: "BuyerRealm",
                table: "MarketSales");

            migrationBuilder.DropColumn(
                name: "GoodsName",
                table: "MarketSales");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "MarketSales");

            migrationBuilder.DropColumn(
                name: "SellerRealm",
                table: "MarketSales");
        }
    }
}
