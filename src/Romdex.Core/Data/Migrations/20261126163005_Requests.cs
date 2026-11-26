using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Requests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TitleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DumpId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Regions = table.Column<string>(type: "TEXT", nullable: false),
                    Replaces = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Requests_Dumps_DumpId",
                        column: x => x.DumpId,
                        principalTable: "Dumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Requests_Titles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_RequestId",
                table: "Downloads",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_DumpId",
                table: "Requests",
                column: "DumpId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_State_UpdatedAt",
                table: "Requests",
                columns: new[] { "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TitleId",
                table: "Requests",
                column: "TitleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Downloads_Requests_RequestId",
                table: "Downloads",
                column: "RequestId",
                principalTable: "Requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Downloads_Requests_RequestId",
                table: "Downloads");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_RequestId",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "Downloads");
        }
    }
}
