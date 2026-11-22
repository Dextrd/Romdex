using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <summary>
    /// Updates and DLC now join their game's title, rather than being titles of their own, and titles are named as the game
    /// is written ("The Legend of Zelda: Majora's Mask"). Forgetting each system's DAT version has the next catalogue
    /// update import every system again, which moves and renames them.
    /// </summary>
    public partial class AddOnsJoinTheirGamesAndTitlesReadAsWritten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Systems\" SET \"DatVersion\" = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
