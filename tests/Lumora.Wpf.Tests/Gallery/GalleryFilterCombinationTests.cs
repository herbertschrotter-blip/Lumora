using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Tags;
using Lumora.Wpf.Tests.ArchiveTarget;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Galerie-Filter unabhängig kombinierbar (0.2.43, ADR-0041): Dateiart, nur Favoriten, Ablage und Mindest-Bewertung
/// wirken mit UND — jede Kombination gegen eine echte, migrierte DB (Eingangs- und Archiv-Wurzel), Zähl- und Seiten-SQL
/// liefern genau die erwartete Menge, auch zusammen mit Tag-Filter, Suche und Ordnerfilter. Die Abfragepläne laufen in
/// keiner Kombination als Vollscan über Files.
/// <para>
/// 24 Dateien f00…f23: Art = i % 3 (Bild, Video, Archiv), Favorit = i gerade, Bewertung = i % 6 (0 = keine),
/// Ablage = Eingang für i % 4 &lt; 2, sonst Bibliothek; Tag „Urlaub“ für i % 5 = 0.
/// </para>
/// </summary>
public sealed class GalleryFilterCombinationTests(GalleryFilterCombinationTests.Catalog catalog, ITestOutputHelper output)
    : IClassFixture<GalleryFilterCombinationTests.Catalog>
{
    private const int FileCount = 24;
    private readonly int _libraryId = catalog.LibraryId;
    private readonly GalleryPageSource _source = catalog.Source;
    private readonly int _urlaubTagId = catalog.UrlaubTagId;
    private readonly int _inboxFolderId = catalog.InboxFolderId;

    public static IEnumerable<object[]> FileFilterCombinations()
    {
        foreach (var media in Enum.GetValues<GalleryMediaKind>())
        {
            foreach (var favorites in new[] { false, true })
            {
                foreach (var location in Enum.GetValues<GalleryLocation>())
                {
                    foreach (var rating in new[] { 0, 1, 3, 5 })
                    {
                        yield return [media, favorites, location, rating];
                    }
                }
            }
        }
    }

    /// <summary>Gemeinsamer Katalog aller Fälle dieser Klasse (einmal angelegt, nur gelesen).</summary>
    public sealed class Catalog : IAsyncLifetime
    {
        private ArchiveTestDb _db = null!;

        public int LibraryId => _db.LibraryId;

        public GalleryPageSource Source { get; private set; } = null!;

        public int UrlaubTagId { get; private set; }

        public int InboxFolderId { get; private set; }

        public async Task InitializeAsync()
        {
            _db = await ArchiveTestDb.CreateAsync();
            var inbox = await _db.AddFolderAsync(_db.InboxRootId, _db.InboxDir);
            var archive = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
            InboxFolderId = inbox.Id;
            var ids = new List<int>();
            for (var i = 0; i < FileCount; i++)
            {
                var (type, ext) = (i % 3) switch
                {
                    0 => (MediaType.Image, "jpg"),
                    1 => (MediaType.Video, "mp4"),
                    _ => (MediaType.Archive, "zip"),
                };
                var file = await _db.AddFileAsync(IsInbox(i) ? inbox : archive, $"f{i:D2}.{ext}", type);
                ids.Add(file.Id);
            }

            using (var connection = _db.Factory.OpenSqliteConnection())
            {
                for (var i = 0; i < FileCount; i++)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "UPDATE Files SET IsFavorite = $fav, Rating = $rating, Missing = $missing WHERE Id = $id;"; // Missing: Ablage „Nur fehlende“ (ADR-0065)
                    cmd.Parameters.AddWithValue("$fav", IsFavorite(i) ? 1 : 0);
                    cmd.Parameters.AddWithValue("$rating", RatingOf(i) is { } r ? r : DBNull.Value);
                    cmd.Parameters.AddWithValue("$missing", IsMissing(i) ? 1 : 0);
                    cmd.Parameters.AddWithValue("$id", ids[i]);
                    cmd.ExecuteNonQuery();
                }
            }

            using var tags = new TagService(_db.Factory, NullLogger<TagService>.Instance, new WeakReferenceMessenger());
            UrlaubTagId = (await tags.CreateAsync("Urlaub")).Id;
            await tags.AssignAsync(ids.Where((_, i) => i % 5 == 0).ToList(), [UrlaubTagId]);

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

    [Theory]
    [MemberData(nameof(FileFilterCombinations))]
    public async Task Jede_Kombination_der_Datei_Filter_wirkt_mit_UND(GalleryMediaKind media, bool favorites, GalleryLocation location, int rating)
    {
        var filter = new GalleryFilterState(media, favorites, location, rating);
        var expected = Expected(filter).ToList();

        (await NamesAsync(new GalleryQuery(_libraryId, filter, null))).Should().BeEquivalentTo(expected, filter.ToString());
    }

    [Theory]
    [MemberData(nameof(FileFilterCombinations))]
    public async Task Datei_Filter_mit_Tag_Filter_und_Suche_kombiniert(GalleryMediaKind media, bool favorites, GalleryLocation location, int rating)
    {
        var filter = new GalleryFilterState(media, favorites, location, rating);

        var withTag = new GalleryQuery(_libraryId, filter, null, TagIds: [_urlaubTagId]);
        (await NamesAsync(withTag)).Should().BeEquivalentTo(Expected(filter, i => i % 5 == 0), "Datei-Filter UND Tag „Urlaub“");

        var withoutTag = withTag with { TagMode = GalleryTagMode.None };
        (await NamesAsync(withoutTag)).Should().BeEquivalentTo(Expected(filter, i => i % 5 != 0), "Datei-Filter UND ohne Tag");

        // Suche „f1“ trifft f10…f19 (Präfix-Phrase auf dem Namen; f01 beginnt nicht mit „f1“)
        var search = new GalleryQuery(_libraryId, filter, "f1");
        (await NamesAsync(search)).Should().BeEquivalentTo(Expected(filter, i => i / 10 == 1), "Datei-Filter UND Suche");

        var folder = new GalleryQuery(_libraryId, filter, null, FolderId: _inboxFolderId);
        (await NamesAsync(folder)).Should().BeEquivalentTo(Expected(filter, IsInbox), "Datei-Filter UND Ordnerfilter (Eingangsordner)");
    }

    [Fact]
    public async Task Beispiel_aus_dem_Auftrag_Bilder_Favoriten_Tag_noch_im_Eingang()
    {
        // „nur Bilder UND Favoriten UND Tag X UND noch im Eingang“ — vorher schlossen sich Art, Favoriten und Ablage aus.
        var filter = new GalleryFilterState(GalleryMediaKind.Images, FavoritesOnly: true, GalleryLocation.Inbox);
        var query = new GalleryQuery(_libraryId, filter, null, TagIds: [_urlaubTagId]);

        (await NamesAsync(query)).Should().Equal("f00.jpg"); // nur i = 0 ist Bild, Favorit, im Eingang und hat „Urlaub“
    }

    [Fact]
    public async Task Unfiltered_Count_zaehlt_nur_mit_Ordnerfilter()
    {
        var filter = new GalleryFilterState(GalleryMediaKind.Images, FavoritesOnly: true, MinRating: 2);
        var query = new GalleryQuery(_libraryId, filter, "f1", TagIds: [_urlaubTagId]);
        (await _source.GetUnfilteredCountAsync(query, CancellationToken.None)).Should().Be(FileCount);
        (await _source.GetUnfilteredCountAsync(query with { FolderId = _inboxFolderId }, CancellationToken.None))
            .Should().Be(Enumerable.Range(0, FileCount).Count(IsInbox));
    }

    [Theory]
    [MemberData(nameof(FileFilterCombinations))]
    public async Task Abfrageplan_ohne_Vollscan_ueber_Files(GalleryMediaKind media, bool favorites, GalleryLocation location, int rating)
    {
        var filter = new GalleryFilterState(media, favorites, location, rating);
        foreach (var count in new[] { true, false })
        {
            var plan = await _source.ExplainAsync(new GalleryQuery(_libraryId, filter, null), count, CancellationToken.None);
            var joined = string.Join(" | ", plan);
            plan.Should().NotContain(l => Regex.IsMatch(l, "^SCAN f( |$)"), $"{(count ? "Zählen" : "Seite")} {filter}: kein Durchlauf aller Dateien (Plan: {joined})");
            plan.Should().Contain(l => l.Contains(" f USING ", StringComparison.Ordinal), $"Files über einen Index (Plan: {joined})");
        }
    }

    [Fact]
    public async Task Abfrageplaene_zur_Dokumentation()
    {
        var cases = new (string Label, GalleryFilterState Filter)[]
        {
            ("ohne Filter", GalleryFilterState.None),
            ("Bilder", new(GalleryMediaKind.Images)),
            ("Favoriten", new(FavoritesOnly: true)),
            ("ab 3 Sternen", new(MinRating: 3)),
            ("noch im Eingang", new(Location: GalleryLocation.Inbox)),
            ("Bilder + Favoriten + ab 3 Sternen + Eingang", new(GalleryMediaKind.Images, true, GalleryLocation.Inbox, 3)),
        };
        foreach (var (label, filter) in cases)
        {
            foreach (var count in new[] { true, false })
            {
                var plan = await _source.ExplainAsync(new GalleryQuery(_libraryId, filter, null), count, CancellationToken.None);
                output.WriteLine($"{label} ({(count ? "Zählen" : "Seite")}): {string.Join(" | ", plan)}");
            }
        }
    }

    [Fact]
    public void Einzelne_Datei_Filter_erzeugen_dieselben_Bedingungen_wie_vor_0_2_43()
    {
        // Gleiche SQL wie das frühere Einzel-Enum → gleiche Abfragepläne für die bisherigen Einzel-Filter.
        GalleryPageSource.BuildFilterClause(GalleryFilterState.None).Should().BeEmpty();
        GalleryPageSource.BuildFilterClause(new(GalleryMediaKind.Images)).Should().Be("AND f.MediaType = 0");
        GalleryPageSource.BuildFilterClause(new(GalleryMediaKind.Videos)).Should().Be("AND f.MediaType = 1");
        GalleryPageSource.BuildFilterClause(new(GalleryMediaKind.Archives)).Should().Be("AND f.MediaType = 2");
        GalleryPageSource.BuildFilterClause(new(FavoritesOnly: true)).Should().Be("AND f.IsFavorite = 1");
        GalleryPageSource.BuildFilterClause(new(Location: GalleryLocation.Inbox)).Should().Contain("r.Role = 0");
        GalleryPageSource.BuildFilterClause(new(Location: GalleryLocation.Library)).Should().Contain("r.Role = 1");
        GalleryPageSource.BuildFilterClause(new(MinRating: 9)).Should().Be("AND f.Rating >= $minRating", "Bewertung wird auf 5 begrenzt, bleibt aber Parameter");
    }

    [Fact]
    public void Filterzustand_zaehlt_aktive_Teile()
    {
        GalleryFilterState.None.IsActive.Should().BeFalse();
        GalleryFilterState.None.ActiveCount.Should().Be(0);
        new GalleryFilterState(GalleryMediaKind.Videos, true, GalleryLocation.Library, 4).ActiveCount.Should().Be(4);
        new GalleryFilterState(MinRating: -2).IsActive.Should().BeFalse("negative Bewertung zählt als „egal“");
        new GalleryFilterState(MinRating: 7).EffectiveMinRating.Should().Be(5);
    }

    [Theory]
    [InlineData(GalleryMediaKind.Images, false, GalleryLocation.Anywhere, 0, "Keine Bilder in dieser Bibliothek.")]
    [InlineData(GalleryMediaKind.All, true, GalleryLocation.Anywhere, 0, "Noch keine Favoriten — markiere Dateien mit dem Herz.")]
    [InlineData(GalleryMediaKind.All, false, GalleryLocation.Anywhere, 3, "Keine Dateien ab 3 Sternen — bewerte Dateien mit den Sternen auf der Kachel.")]
    [InlineData(GalleryMediaKind.All, false, GalleryLocation.Inbox, 0, "Nichts mehr im Eingang — alle Dateien sind in der Bibliothek. Eingänge verwaltest du unter „Bibliothek“.")]
    public void Leertext_je_Datei_Filter(GalleryMediaKind media, bool favorites, GalleryLocation location, int rating, string expected)
        => GalleryViewModel.EmptyMessageForFileFilter(new GalleryFilterState(media, favorites, location, rating)).Should().Be(expected);

    private IEnumerable<string> Expected(GalleryFilterState filter, Func<int, bool>? extra = null)
        => Enumerable.Range(0, FileCount)
            .Where(i => filter.Media switch
            {
                GalleryMediaKind.Images => i % 3 == 0,
                GalleryMediaKind.Videos => i % 3 == 1,
                GalleryMediaKind.Archives => i % 3 == 2,
                _ => true,
            })
            .Where(i => !filter.FavoritesOnly || IsFavorite(i))
            .Where(i => filter.EffectiveMinRating == 0 || (RatingOf(i) ?? 0) >= filter.EffectiveMinRating)
            .Where(i => filter.Location switch
            {
                GalleryLocation.Inbox => IsInbox(i),
                GalleryLocation.Library => !IsInbox(i),
                GalleryLocation.Missing => IsMissing(i), // ADR-0065
                _ => true,
            })
            .Where(i => extra?.Invoke(i) ?? true)
            .Select(i => $"f{i:D2}.{(i % 3) switch { 0 => "jpg", 1 => "mp4", _ => "zip" }}");

    private static bool IsInbox(int i) => i % 4 < 2;

    private static bool IsFavorite(int i) => i % 2 == 0;

    private static bool IsMissing(int i) => i % 7 == 3;

    private static int? RatingOf(int i) => i % 6 == 0 ? null : i % 6;

    private async Task<List<string>> NamesAsync(GalleryQuery query)
    {
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Should().HaveCount(count, "Zähl- und Seiten-SQL nutzen dieselben Bedingungen");
        return rows.Select(r => r.Name).ToList();
    }
}