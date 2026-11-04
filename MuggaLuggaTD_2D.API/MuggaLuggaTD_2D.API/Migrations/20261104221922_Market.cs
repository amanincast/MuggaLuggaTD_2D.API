using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class Market : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Deposit",
                table: "MarketplaceListings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DepositRefunded",
                table: "MarketplaceListings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "MarketplaceListings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Listings already on the Bazaar get a fresh 72 hours from the migration (Marketplace spec §7), and pay
            // no deposit: they were listed before there was one. Without this they would all fall due at once.
            migrationBuilder.Sql(
                "UPDATE \"MarketplaceListings\" SET \"ExpiresAt\" = now() + interval '72 hours';");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnedAt",
                table: "MarketplaceListings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MarketOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerId = table.Column<string>(type: "text", nullable: false),
                    MaterialName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    QuantityFilled = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<long>(type: "bigint", nullable: false),
                    HeldGold = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketOrders_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketSales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    GoodsKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ItemType = table.Column<int>(type: "integer", nullable: true),
                    Rarity = table.Column<int>(type: "integer", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<long>(type: "bigint", nullable: false),
                    SoldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketSales", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketplaceListings_Status_ExpiresAt",
                table: "MarketplaceListings",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketOrders_GameInstanceId_BuyerId_Status",
                table: "MarketOrders",
                columns: new[] { "GameInstanceId", "BuyerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketOrders_MaterialName_Status_CreatedAt",
                table: "MarketOrders",
                columns: new[] { "MaterialName", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketOrders_Status_ExpiresAt",
                table: "MarketOrders",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketSales_GoodsKey_SoldAt",
                table: "MarketSales",
                columns: new[] { "GoodsKey", "SoldAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketSales_Kind_SoldAt",
                table: "MarketSales",
                columns: new[] { "Kind", "SoldAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketOrders");

            migrationBuilder.DropTable(
                name: "MarketSales");

            migrationBuilder.DropIndex(
                name: "IX_MarketplaceListings_Status_ExpiresAt",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "Deposit",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "DepositRefunded",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "MarketplaceListings");

            migrationBuilder.DropColumn(
                name: "ReturnedAt",
                table: "MarketplaceListings");
        }
    }
}
