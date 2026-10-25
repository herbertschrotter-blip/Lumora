using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Titelbild eines Ordners (ADR-0068): nur eine neue Spalte <c>Folders.CoverFileId</c> (null = automatisch) — ohne
    /// Fremdschlüssel, damit SQLite die Tabelle nicht neu aufbaut (FK-Kaskaden-Lehre ADR-0045/0048).
    /// </summary>
    public partial class AddFolderCover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoverFileId",
                table: "Folders",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverFileId",
                table: "Folders");
        }
    }
}
