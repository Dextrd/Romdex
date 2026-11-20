using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class GameImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImagesJson",
                table: "GameAbout",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImagesJson",
                table: "GameAbout");
        }
    }
}
