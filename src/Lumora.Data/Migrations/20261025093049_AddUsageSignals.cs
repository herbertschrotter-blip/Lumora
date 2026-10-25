using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Nutzungs-Signale (ADR-0072): nur eine neue Tabelle <c>FileUsageDays</c> (Tagessummen je Datei, ON DELETE CASCADE an
    /// <c>Files</c>) mit Index <c>IX_FileUsageDays_Day</c> — kein Neuaufbau bestehender Tabellen, keine Datenübernahme.
    /// </summary>
    public partial class AddUsageSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileUsageDays",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    Opens = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Zooms = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SoftOpens = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SoftSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Uses = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Shares = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileUsageDays", x => new { x.FileId, x.Day });
                    table.ForeignKey(
                        name: "FK_FileUsageDays_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileUsageDays_Day",
                table: "FileUsageDays",
                columns: new[] { "Day", "FileId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileUsageDays");
        }
    }
}
