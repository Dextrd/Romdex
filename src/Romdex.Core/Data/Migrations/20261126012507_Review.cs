using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romdex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Review : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ManualMatchId",
                table: "LibraryMatches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeferredAt",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Failure",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenAt",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeldFor",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KeptIncompletes",
                columns: table => new
                {
                    DumpId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Missing = table.Column<string>(type: "TEXT", nullable: false),
                    KeptAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeptIncompletes", x => x.DumpId);
                    table.ForeignKey(
                        name: "FK_KeptIncompletes_Dumps_DumpId",
                        column: x => x.DumpId,
                        principalTable: "Dumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManualMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sha1 = table.Column<string>(type: "TEXT", nullable: false),
                    DumpFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TitleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualMatches_DumpFiles_DumpFileId",
                        column: x => x.DumpFileId,
                        principalTable: "DumpFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ManualMatches_Titles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PastMatches",
                columns: table => new
                {
                    Sha1 = table.Column<string>(type: "TEXT", nullable: false),
                    SystemName = table.Column<string>(type: "TEXT", nullable: false),
                    DumpName = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    DatVersion = table.Column<string>(type: "TEXT", nullable: false),
                    FirstMatchedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastMatchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PastMatches", x => new { x.Sha1, x.SystemName, x.DumpName, x.FileName });
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryMatches_ManualMatchId",
                table: "LibraryMatches",
                column: "ManualMatchId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryContents_HeaderlessSha1",
                table: "LibraryContents",
                column: "HeaderlessSha1");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryContents_Sha1",
                table: "LibraryContents",
                column: "Sha1");

            migrationBuilder.CreateIndex(
                name: "IX_ManualMatches_DumpFileId",
                table: "ManualMatches",
                column: "DumpFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualMatches_Sha1",
                table: "ManualMatches",
                column: "Sha1");

            migrationBuilder.CreateIndex(
                name: "IX_ManualMatches_TitleId",
                table: "ManualMatches",
                column: "TitleId");

            migrationBuilder.AddForeignKey(
                name: "FK_LibraryMatches_ManualMatches_ManualMatchId",
                table: "LibraryMatches",
                column: "ManualMatchId",
                principalTable: "ManualMatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LibraryMatches_ManualMatches_ManualMatchId",
                table: "LibraryMatches");

            migrationBuilder.DropTable(
                name: "KeptIncompletes");

            migrationBuilder.DropTable(
                name: "ManualMatches");

            migrationBuilder.DropTable(
                name: "PastMatches");

            migrationBuilder.DropIndex(
                name: "IX_LibraryMatches_ManualMatchId",
                table: "LibraryMatches");

            migrationBuilder.DropIndex(
                name: "IX_LibraryContents_HeaderlessSha1",
                table: "LibraryContents");

            migrationBuilder.DropIndex(
                name: "IX_LibraryContents_Sha1",
                table: "LibraryContents");

            migrationBuilder.DropColumn(
                name: "ManualMatchId",
                table: "LibraryMatches");

            migrationBuilder.DropColumn(
                name: "DeferredAt",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "Failure",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "HiddenAt",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "HeldFor",
                table: "Downloads");
        }
    }
}
