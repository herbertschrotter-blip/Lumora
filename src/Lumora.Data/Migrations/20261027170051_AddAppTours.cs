using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// App-Verbindung Stufe 7 (ADR-0073): neue Tabelle AppTours (Touren verbundener Apps, auch ohne Spur) und Spalte
    /// GpsTracks.AppTourId (Spur aus einer Tour, ohne Fremdschlüssel). Nur CREATE TABLE und ADD COLUMN — bestehende Tabellen
    /// bleiben unverändert.
    /// </summary>
    public partial class AddAppTours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppTourId",
                table: "GpsTracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppTours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EndUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    StartOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Sport = table.Column<string>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    DistanceMeters = table.Column<double>(type: "REAL", nullable: false),
                    AscentMeters = table.Column<double>(type: "REAL", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    MinLatitude = table.Column<double>(type: "REAL", nullable: false),
                    MinLongitude = table.Column<double>(type: "REAL", nullable: false),
                    MaxLatitude = table.Column<double>(type: "REAL", nullable: false),
                    MaxLongitude = table.Column<double>(type: "REAL", nullable: false),
                    HasTrack = table.Column<bool>(type: "INTEGER", nullable: false),
                    Summits = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppTours", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpsTracks_AppTour",
                table: "GpsTracks",
                column: "AppTourId",
                filter: "AppTourId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppTours_Time",
                table: "AppTours",
                columns: new[] { "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_AppTours_App_External",
                table: "AppTours",
                columns: new[] { "AppId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppTours");

            migrationBuilder.DropIndex(
                name: "IX_GpsTracks_AppTour",
                table: "GpsTracks");

            migrationBuilder.DropColumn(
                name: "AppTourId",
                table: "GpsTracks");
        }
    }
}
