using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class AddTavern : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HiredCharacters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CharacterId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sheet = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CharacterClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignatureId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Affinity = table.Column<short>(type: "smallint", nullable: false),
                    Rarity = table.Column<short>(type: "smallint", nullable: false),
                    HiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiredCharacters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HiredCharacters_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TavernRecruits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sheet = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CharacterClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignatureId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Affinity = table.Column<short>(type: "smallint", nullable: false),
                    Rarity = table.Column<short>(type: "smallint", nullable: false),
                    RolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TavernRecruits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TavernRecruits_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HiredCharacters_GameInstanceId_UserId_CharacterId",
                table: "HiredCharacters",
                columns: new[] { "GameInstanceId", "UserId", "CharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TavernRecruits_GameInstanceId_UserId_Slot",
                table: "TavernRecruits",
                columns: new[] { "GameInstanceId", "UserId", "Slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HiredCharacters");

            migrationBuilder.DropTable(
                name: "TavernRecruits");
        }
    }
}
