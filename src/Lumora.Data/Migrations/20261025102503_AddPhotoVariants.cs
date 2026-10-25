using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Foto verbessern (ADR-0071): neue Tabelle PhotoVariants (verbesserte Fassung → Original, beide ON DELETE CASCADE auf
    /// Files). Nur CREATE TABLE — bestehende Tabellen bleiben unverändert.
    /// </summary>
    public partial class AddPhotoVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoVariants",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Tools = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoVariants", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_PhotoVariants_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PhotoVariants_Files_OriginalFileId",
                        column: x => x.OriginalFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoVariants_Original",
                table: "PhotoVariants",
                column: "OriginalFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoVariants");
        }
    }
}
