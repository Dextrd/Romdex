using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class GameDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "DumpArt",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinksJson",
                table: "DumpArt",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "GameAbout",
                columns: table => new
                {
                    TitleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LookedUpAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true),
                    IdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameAbout", x => x.TitleId);
                    table.ForeignKey(
                        name: "FK_GameAbout_Titles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Dumps found before links and descriptions were kept are looked up again, when next wanted, for theirs.
            // Their covers stay until then.
            migrationBuilder.Sql("""
                UPDATE "DumpArt" SET "Error" = 'Looked up again, for its links.', "LookedUpAt" = '2000-01-01 00:00:00'
                WHERE "Error" IS NULL AND ("Name" IS NOT NULL OR "CoverFile" IS NOT NULL)
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameAbout");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "DumpArt");

            migrationBuilder.DropColumn(
                name: "LinksJson",
                table: "DumpArt");
        }
    }
}
