using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DownloadEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnnouncedState",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            // What was there before is already known: only what changes from now on is announced.
            migrationBuilder.Sql("UPDATE \"Downloads\" SET \"AnnouncedState\" = \"State\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnouncedState",
                table: "Downloads");
        }
    }
}
