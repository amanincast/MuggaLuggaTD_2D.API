using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuggaLuggaTD_2D.API.Migrations
{
    /// <inheritdoc />
    public partial class Letters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Letters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RegionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ActorName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Letters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Letters_GameInstances_GameInstanceId",
                        column: x => x.GameInstanceId,
                        principalTable: "GameInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Letters_GameInstanceId",
                table: "Letters",
                column: "GameInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Letters_UserId_GameInstanceId_DedupKey",
                table: "Letters",
                columns: new[] { "UserId", "GameInstanceId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Letters_UserId_GameInstanceId_ReadAt_OccurredAt",
                table: "Letters",
                columns: new[] { "UserId", "GameInstanceId", "ReadAt", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Letters");
        }
    }
}
