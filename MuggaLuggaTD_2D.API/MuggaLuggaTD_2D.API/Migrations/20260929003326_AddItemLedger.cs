using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddItemLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ItemGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ItemJson = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemGrants_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemLedgerStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    AdoptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AdoptedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemLedgerStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemGrants_GameInstanceId_ItemId",
                table: "ItemGrants",
                columns: new[] { "GameInstanceId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemGrants_GameInstanceId_UserId",
                table: "ItemGrants",
                columns: new[] { "GameInstanceId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemGrants_ListingId",
                table: "ItemGrants",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemLedgerStates_GameInstanceId_UserId",
                table: "ItemLedgerStates",
                columns: new[] { "GameInstanceId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemGrants");

            migrationBuilder.DropTable(
                name: "ItemLedgerStates");
        }
    }
}
