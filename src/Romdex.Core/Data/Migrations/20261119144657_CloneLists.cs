using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CloneLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CloneList",
                table: "Systems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloneList",
                table: "Systems");
        }
    }
}
