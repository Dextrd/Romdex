using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayAndArt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppConnectorId",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppGameId",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DumpArt",
                columns: table => new
                {
                    DumpId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LookedUpAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    CoverFile = table.Column<string>(type: "TEXT", nullable: true),
                    IdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DumpArt", x => x.DumpId);
                    table.ForeignKey(
                        name: "FK_DumpArt_Dumps_DumpId",
                        column: x => x.DumpId,
                        principalTable: "Dumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DumpArt");

            migrationBuilder.DropColumn(
                name: "AppConnectorId",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "AppGameId",
                table: "LibraryFiles");
        }
    }
}
