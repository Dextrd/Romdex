using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Downloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CandidateId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CandidateJson = table.Column<string>(type: "TEXT", nullable: false),
                    TitleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WantedDumpId = table.Column<Guid>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    StagingDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Downloads_Dumps_WantedDumpId",
                        column: x => x.WantedDumpId,
                        principalTable: "Dumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Downloads_PluginInstances_PluginInstanceId",
                        column: x => x.PluginInstanceId,
                        principalTable: "PluginInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Downloads_Titles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DownloadDumps",
                columns: table => new
                {
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DumpId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Alternative = table.Column<bool>(type: "INTEGER", nullable: false),
                    FiledPaths = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadDumps", x => new { x.DownloadId, x.DumpId });
                    table.ForeignKey(
                        name: "FK_DownloadDumps_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DownloadDumps_Dumps_DumpId",
                        column: x => x.DumpId,
                        principalTable: "Dumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadDumps_DumpId",
                table: "DownloadDumps",
                column: "DumpId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_PluginInstanceId_CandidateId",
                table: "Downloads",
                columns: new[] { "PluginInstanceId", "CandidateId" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_State_CreatedAt",
                table: "Downloads",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_TitleId",
                table: "Downloads",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_WantedDumpId",
                table: "Downloads",
                column: "WantedDumpId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadDumps");

            migrationBuilder.DropTable(
                name: "Downloads");
        }
    }
}
