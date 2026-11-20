using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetadataPlugins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hasheous was an index plugin; it's a metadata plugin now, under its new ID, with its settings kept.
            migrationBuilder.Sql("UPDATE \"PluginInstances\" SET \"PluginId\" = 'romdex.metadata.hasheous', \"Type\" = 'Metadata' WHERE \"PluginId\" = 'romdex.index.hasheous';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"PluginInstances\" SET \"PluginId\" = 'romdex.index.hasheous', \"Type\" = 'Index' WHERE \"PluginId\" = 'romdex.metadata.hasheous';");
        }
    }
}
