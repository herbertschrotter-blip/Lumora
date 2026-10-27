using Microsoft.Data.Sqlite;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Services.Places;
using Lumora.Wpf.Tests.ArchiveTarget;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Galerie-Filter „Ort“ und „Kartenausschnitt“ (ADR-0050) in der SQL: Orte (ODER), „ohne Ort“, „ohne Ortsnamen“,
/// Kartengruppe (Ids) und Ausschnitt (auch über die Datumsgrenze), UND mit dem Datei-Filter, nur in der Bibliothek.
/// </summary>
public sealed class GalleryPlaceFilterTests(GalleryPlaceFilterTests.Catalog catalog) : IClassFixture<GalleryPlaceFilterTests.Catalog>
{
    private const int Wien = 2761369;
    private const int Rom = 3169070;

    private readonly GalleryPageSource _source = catalog.Source;
    private readonly int _libraryId = catalog.LibraryId;

    /// <summary>
    /// wien1, wien2 (Bild/Video), rom, fidschi (Koordinate bei 179,9° O, Ort 1), meer (Koordinate ohne Ort), ohne (keine
    /// Koordinate); dazu eine Datei an „Wien“ in einer anderen Bibliothek.
    /// </summary>
    public sealed class Catalog : IAsyncLifetime
    {
        private ArchiveTestDb _db = null!;

        public int LibraryId => _db.LibraryId;

        public Dictionary<string, int> Ids { get; } = new();

        public GalleryPageSource Source { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _db = await ArchiveTestDb.CreateAsync();
            var folder = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
            (string Name, MediaType Type, double? Lat, double? Lon, int? Place)[] files =
            [
                ("wien1.jpg", MediaType.Image, 48.2082, 16.3738, Wien),
                ("wien2.mp4", MediaType.Video, 48.2100, 16.3600, Wien),
                ("rom.jpg", MediaType.Image, 41.9028, 12.4964, Rom),
                ("fidschi.jpg", MediaType.Image, -16.8, 179.9, 1),
                ("meer.jpg", MediaType.Image, 35.0, -40.0, null),
                ("ohne.jpg", MediaType.Image, null, null, null),
            ];
            using var connection = _db.Factory.OpenSqliteConnection();
            foreach (var (name, type, lat, lon, place) in files)
            {
                var file = await _db.AddFileAsync(folder, name, type);
                Ids[name] = file.Id;
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE Files SET Latitude = $lat, Longitude = $lon, PlaceId = $place, GeoSource = $source WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$lat", (object?)lat ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lon", (object?)lon ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$place", (object?)place ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$source", lat is null ? 0 : 1);
                cmd.Parameters.AddWithValue("$id", file.Id);
                cmd.ExecuteNonQuery();
            }

            // Andere Bibliothek mit einer Datei „in Wien“ — darf nie mitzählen.
            var foreign = await _db.AddFileAsync(folder, "fremd.jpg");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO Libraries (Name, RootPath, CreatedAt, LastOpenedAt) VALUES ('Andere', 'X:\andere', 0, 0);
                    UPDATE Files SET LibraryId = last_insert_rowid(), Latitude = 48.2, Longitude = 16.37, PlaceId = $wien, GeoSource = 1 WHERE Id = $id;
                    """;
                cmd.Parameters.AddWithValue("$wien", Wien);
                cmd.Parameters.AddWithValue("$id", foreign.Id);
                cmd.ExecuteNonQuery();
            }

            Source = new GalleryPageSource(_db.Factory);
            Source.Open();
        }

        public Task DisposeAsync()
        {
            Source.Dispose();
            SqliteConnection.ClearAllPools();
            _db.Dispose();
            return Task.CompletedTask;
        }
    }

    private async Task<List<string>> NamesAsync(GalleryFilterState filter)
    {
        var query = new GalleryQuery(_libraryId, filter, null, Sort: GallerySort.NameAsc);
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Should().HaveCount(count, "Zählung und Seite passen zusammen");
        return rows.Select(r => r.Name).ToList();
    }

    [Fact]
    public async Task Tour_zeigt_nur_ihre_Dateien_und_wirkt_mit_den_anderen_Filtern()
    {
        // Filter „Tour“ (ADR-0073 Stufe 7, Mockup 24): Id-Liste aus dem Tour-Leser, UND mit allen anderen Filtern
        var ids = catalog.Ids;
        var tour = new GalleryTourFilter(1, [ids["wien1.jpg"], ids["wien2.mp4"], ids["rom.jpg"]], "Handalm");
        (await NamesAsync(new GalleryFilterState { Tour = tour })).Should().Equal("rom.jpg", "wien1.jpg", "wien2.mp4");
        (await NamesAsync(new GalleryFilterState(GalleryMediaKind.Images) { Tour = tour })).Should().Equal("rom.jpg", "wien1.jpg");
        (await NamesAsync(new GalleryFilterState { Tour = tour, Place = new GalleryPlaceFilter([Wien], false) })).Should().Equal("wien1.jpg", "wien2.mp4");
        (await NamesAsync(new GalleryFilterState { Tour = tour with { FileIds = [] } })).Should().BeEmpty("keine Fotos aus der Zeit der Tour");
        new GalleryFilterState { Tour = tour }.ActiveCount.Should().Be(1);
        new GalleryFilterState { Tour = tour }.ToString().Should().Contain("Tour=3");
    }

    [Fact]
    public async Task Orte_mit_ODER_und_ohne_Ort()
    {
        (await NamesAsync(new GalleryFilterState { Place = new GalleryPlaceFilter([Wien], false) }))
            .Should().Equal("wien1.jpg", "wien2.mp4");
        (await NamesAsync(new GalleryFilterState { Place = new GalleryPlaceFilter([Wien, Rom], false) }))
            .Should().Equal("rom.jpg", "wien1.jpg", "wien2.mp4");
        (await NamesAsync(new GalleryFilterState { Place = new GalleryPlaceFilter([], true) }))
            .Should().Equal("ohne.jpg");
        (await NamesAsync(new GalleryFilterState { Place = new GalleryPlaceFilter([Rom], true, true) }))
            .Should().Equal("meer.jpg", "ohne.jpg", "rom.jpg");
    }

    [Fact]
    public async Task UND_mit_dem_Datei_Filter()
        => (await NamesAsync(new GalleryFilterState(GalleryMediaKind.Images) { Place = new GalleryPlaceFilter([Wien], false) }))
            .Should().Equal("wien1.jpg");

    [Fact]
    public async Task Kartengruppe_ueber_Ids_und_Ausschnitt()
    {
        (await NamesAsync(new GalleryFilterState { Geo = new GalleryGeoFilter(new GeoBounds(0, 0, 1, 1), [catalog.Ids["rom.jpg"], catalog.Ids["ohne.jpg"]]) }))
            .Should().Equal("ohne.jpg", "rom.jpg");
        (await NamesAsync(new GalleryFilterState { Geo = new GalleryGeoFilter(new GeoBounds(40, 10, 49, 17), null) }))
            .Should().Equal("rom.jpg", "wien1.jpg", "wien2.mp4");
        (await NamesAsync(new GalleryFilterState { Geo = new GalleryGeoFilter(new GeoBounds(-20, 170, -10, -170), null) }))
            .Should().Equal("fidschi.jpg");
    }

    [Fact]
    public void Zaehler_und_Gleichheit()
    {
        new GalleryFilterState { Place = new GalleryPlaceFilter([Wien], false) }.ActiveCount.Should().Be(1);
        new GalleryFilterState { Place = new GalleryPlaceFilter([], false) }.IsActive.Should().BeFalse();
        new GalleryFilterState { Geo = new GalleryGeoFilter(new GeoBounds(0, 0, 1, 1), null) }.ActiveCount.Should().Be(1);
        new GalleryFilterState().Should().Be(GalleryFilterState.None);
    }

    [Fact]
    public async Task Ohne_Filter_alle_eigenen_Dateien()
        => (await NamesAsync(GalleryFilterState.None)).Should().HaveCount(6, "die Datei der anderen Bibliothek zählt nicht");
}
