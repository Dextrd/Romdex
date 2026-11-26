using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Arrivals held before they said why, sorted by the message they were held with.
            migrationBuilder.Sql("""
                UPDATE Downloads SET HeldFor = CASE
                    WHEN Message LIKE 'It''s incomplete%' THEN 'Incomplete'
                    WHEN Message LIKE 'None of its files could be read%' THEN 'Unreadable'
                    WHEN Message LIKE 'It matches nothing%' THEN 'Unmatched'
                    WHEN Message LIKE 'It isn''t what was asked for%' THEN 'NotAskedFor'
                    ELSE 'Alternative' END
                WHERE State = 'NeedsReview' AND HeldFor IS NULL;
                """);
            // Systems no longer hold alternatives for review: those that did file them beside what's there.
            migrationBuilder.Sql("""
                UPDATE Settings SET ValueJson = REPLACE(ValueJson, '"alternatives":"review"', '"alternatives":"file"') WHERE Key LIKE 'filing:%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
