using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Zusammenfügen (ADR-0078 Punkt 11, Stufe 1): neue Tabellen MergedPhotos (Ergebnis → Files, ON DELETE CASCADE),
    /// MergedPhotoSources (Quellen → MergedPhotos bzw. Files, beide CASCADE) mit Index IX_MergedPhotoSources_Source und
    /// MergedPhotoUndo (gesicherte Ergebnis-Zeile zur FileOperation, CASCADE). Nur CREATE TABLE — bestehende Tabellen bleiben
    /// unverändert.
    /// </summary>
    public partial class AddMergedPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MergedPhotos",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    Projection = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    Tools = table.Column<string>(type: "TEXT", nullable: false),
                    RecipeJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergedPhotos", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_MergedPhotos_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MergedPhotoUndo",
                columns: table => new
                {
                    FileOperationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    Projection = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    Tools = table.Column<string>(type: "TEXT", nullable: false),
                    RecipeJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergedPhotoUndo", x => x.FileOperationId);
                    table.ForeignKey(
                        name: "FK_MergedPhotoUndo_FileOperations_FileOperationId",
                        column: x => x.FileOperationId,
                        principalTable: "FileOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MergedPhotoSources",
                columns: table => new
                {
                    ResultFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergedPhotoSources", x => new { x.ResultFileId, x.SourceFileId });
                    table.ForeignKey(
                        name: "FK_MergedPhotoSources_Files_SourceFileId",
                        column: x => x.SourceFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MergedPhotoSources_MergedPhotos_ResultFileId",
                        column: x => x.ResultFileId,
                        principalTable: "MergedPhotos",
                        principalColumn: "FileId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MergedPhotoSources_Source",
                table: "MergedPhotoSources",
                column: "SourceFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MergedPhotoSources");

            migrationBuilder.DropTable(
                name: "MergedPhotoUndo");

            migrationBuilder.DropTable(
                name: "MergedPhotos");
        }
    }
}
