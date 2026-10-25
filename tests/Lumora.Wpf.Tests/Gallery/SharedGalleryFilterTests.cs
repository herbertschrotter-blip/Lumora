using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.Archives;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.Services;
using Lumora.Gallery.Tags;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Places.Data;
using Lumora.Places.ViewModels;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Places;
using Lumora.Services.Tags;
using Lumora.Services.Thumbnails;
using Lumora.Shell;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Places;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// EIN Filterzustand für Galerie, Ordner und Orte (ADR-0055) gegen eine echte, migrierte DB: Galerie setzt den Filter →
/// Ordner-Seite und Karte sehen ihn; „Filter zurücksetzen“ wirkt überall; Ordnerauswahl und Sortierung bleiben
/// seitengebunden; die Karte zählt genau die Galerie-Treffer mit Koordinaten (dieselbe Filter-SQL).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SharedGalleryFilterTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private ArchiveTestDb _db = null!;
    private int _folderA;
    private int _folderB;
    private readonly Dictionary<string, int> _ids = new();

    /// <summary>
    /// Ordner A: a-fav (Fav, Wien), a-plain (Wien), a-video (Video, Fav, ohne Ort); Ordner B: b-fav (Fav, Rom),
    /// b-plain (ohne Ort), b-zip (Archiv, Fav — nie auf der Karte).
    /// </summary>
    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var a = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "A"));
        var b = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "B"));
        _folderA = a.Id;
        _folderB = b.Id;
        (string Name, int Folder, MediaType Type, bool Fav, double? Lat, double? Lon)[] files =
        [
            ("a-fav.jpg", a.Id, MediaType.Image, true, 48.2082, 16.3738),
            ("a-plain.jpg", a.Id, MediaType.Image, false, 48.2090, 16.3750),
            ("a-video.mp4", a.Id, MediaType.Video, true, null, null),
            ("b-fav.jpg", b.Id, MediaType.Image, true, 41.9028, 12.4964),
            ("b-plain.jpg", b.Id, MediaType.Image, false, null, null),
            ("b-zip.zip", b.Id, MediaType.Archive, true, null, null),
        ];
        using var connection = _db.Factory.OpenSqliteConnection();
        foreach (var (name, folderId, type, fav, lat, lon) in files)
        {
            var file = await _db.AddFileAsync(folderId == a.Id ? a : b, name, type);
            _ids[name] = file.Id;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Files SET IsFavorite = $fav, Latitude = $lat, Longitude = $lon WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$fav", fav ? 1 : 0);
            cmd.Parameters.AddWithValue("$lat", (object?)lat ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lon", (object?)lon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", file.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void Veroeffentlicht_ohne_Ordner_und_Sortierung_und_nur_bei_Aenderung()
    {
        var shared = new SharedGalleryFilter();
        var changes = 0;
        shared.Changed += (_, _) => changes++;

        shared.Publish(new GalleryQuery(1, new GalleryFilterState(FavoritesOnly: true), null, FolderId: 7, IncludeSubfolders: false, Sort: GallerySort.NameAsc));
        shared.Current!.FolderId.Should().BeNull("die Ordnerauswahl ist seitengebunden");
        shared.Current.Sort.Should().Be(GallerySort.CapturedDesc);
        shared.IsActive.Should().BeTrue();
        changes.Should().Be(1);

        shared.Publish(new GalleryQuery(1, new GalleryFilterState(FavoritesOnly: true), null, FolderId: 9, Sort: GallerySort.SizeAsc));
        changes.Should().Be(1, "anderer Ordner/andere Sortierung = gleicher Filter");

        shared.Publish(new GalleryQuery(1, GalleryFilterState.None, null));
        changes.Should().Be(2);
        shared.IsActive.Should().BeFalse();

        shared.Publish(new GalleryQuery(1, GalleryFilterState.None, "urlaub"));
        shared.IsActive.Should().BeTrue("Suche gehört zum Filter");
    }

    [Fact]
    public Task Filter_gilt_auf_allen_Seiten_Ordner_und_Sortierung_seitengebunden() => OnUiAsync(async () =>
    {
        var shared = new SharedGalleryFilter();
        var navigation = new RecordingNavigation();
        var gallery = CreateGallery(shared, navigation);
        await gallery.EnsureInitializedAsync();
        gallery.TotalCount.Should().Be(6);

        // (1) Galerie setzt „Nur Favoriten“ → gemeinsamer Filter
        gallery.FavoritesOnly = true;
        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 4, "Galerie: 4 Favoriten");
        shared.IsActive.Should().BeTrue();
        shared.Current!.Filter.FavoritesOnly.Should().BeTrue();

        // (2) Ordner-Seite: derselbe Filter, eigene Ordnerauswahl
        navigation.NavigateTo(NavigationTags.Folders);
        await WaitAsync(() => gallery.ActivePage == GalleryPageKind.Folders, "Ordner-Seite aktiv");
        gallery.FavoritesOnly.Should().BeTrue("Filter bleibt beim Seitenwechsel");
        await SelectFolderAsync(gallery, _folderA);
        await WaitAsync(() => !gallery.IsLoading && gallery.FolderId == _folderA && gallery.TotalCount == 2, "Ordner A: 2 Favoriten");
        shared.Current!.FolderId.Should().BeNull("der Ordner gehört nur der Ordner-Seite");

        // (3) Zurück zur Galerie: kein Ordner, Filter weiter aktiv; Sortierung der Galerie ändern
        navigation.NavigateTo(NavigationTags.Gallery);
        await WaitAsync(() => !gallery.IsLoading && gallery.FolderId is null && gallery.TotalCount == 4, "Galerie ohne Ordner, mit Filter");
        gallery.SelectedSort = gallery.SortOptions.First(o => o.Sort == GallerySort.NameAsc);
        await WaitAsync(() => !gallery.IsLoading, "Galerie neu sortiert");

        // (4) Ordner-Seite stellt ihren Ordner und ihre Sortierung wieder her
        navigation.NavigateTo(NavigationTags.Folders);
        await WaitAsync(() => !gallery.IsLoading && gallery.FolderId == _folderA && gallery.TotalCount == 2, "Ordner A wieder da");
        gallery.SelectedSort.Sort.Should().Be(GallerySort.CapturedDesc, "Sortierung ist seitengebunden");

        // (5) „Filter zurücksetzen“ auf der Ordner-Seite wirkt überall — die Ordnerauswahl bleibt
        await gallery.ResetFiltersCommand.ExecuteAsync(null);
        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 3, "Ordner A ohne Filter: 3 Dateien");
        gallery.FolderId.Should().Be(_folderA);
        shared.IsActive.Should().BeFalse();
        navigation.NavigateTo(NavigationTags.Gallery);
        await WaitAsync(() => !gallery.IsLoading && gallery.FolderId is null && gallery.TotalCount == 6, "Galerie ohne Filter");
        gallery.FavoritesOnly.Should().BeFalse();
        gallery.SelectedSort.Sort.Should().Be(GallerySort.NameAsc, "Sortierung der Galerie blieb erhalten");

        // Seiten ohne Galerie-Ansicht ändern nichts
        navigation.NavigateTo(NavigationTags.Places);
        gallery.ActivePage.Should().Be(GalleryPageKind.Gallery);
    });

    [Fact]
    public Task Bibliothekswechsel_behaelt_den_Filter_und_verwirft_Bereichsbezogenes() => OnUiAsync(async () =>
    {
        var shared = new SharedGalleryFilter();
        var navigation = new RecordingNavigation();
        var gallery = CreateGallery(shared, navigation);
        await gallery.EnsureInitializedAsync();
        navigation.NavigateTo(NavigationTags.Folders);
        await SelectFolderAsync(gallery, _folderB);
        await WaitAsync(() => !gallery.IsLoading && gallery.FolderId == _folderB, "Ordner B");
        gallery.FilterState = new GalleryFilterState(FavoritesOnly: true)
        {
            Geo = new GalleryGeoFilter(new GeoBounds(40, 10, 49, 17), [_ids["b-fav.jpg"]]),
        };
        await WaitAsync(() => !gallery.IsLoading && gallery.HasMapAreaFilter, "Kartenausschnitt gesetzt");

        WeakReferenceMessenger.Default.Send(new ActiveLibraryChangedMessage(_db.LibraryId)); // z. B. „Alle verknüpften“ und zurück
        await WaitAsync(() => !gallery.IsLoading && gallery.FolderId is null && !gallery.HasMapAreaFilter, "Bereich neu geladen");
        gallery.FavoritesOnly.Should().BeTrue("Datei-Filter gelten in jedem Bereich");
        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 4, "Favoriten des Bereichs");
        shared.Current!.Filter.Geo.Should().BeNull("der Kartenausschnitt (Datei-Ids) gehörte zum alten Bereich");
    });

    [Fact]
    public Task Schnelle_Bereichswechsel_der_zuletzt_angestossene_gewinnt() => OnUiAsync(async () =>
    {
        // „Lösen“ meldet verzögert die alte aktive Bibliothek, gleich danach kommt der Wechsel zurück (Rauchtest 0.4.23):
        // die Galerie darf nicht auf dem Bereich der zuerst gemeldeten, aber zuletzt fertig geladenen Bibliothek landen.
        var other = Path.Combine(_db.ArchiveDir, "Zweite");
        Directory.CreateDirectory(other);
        int otherId;
        using (var connection = _db.Factory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO Libraries (Name, RootPath, CreatedAt, LastOpenedAt) VALUES ('Zweite', $root, 0, 0); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$root", other);
            otherId = Convert.ToInt32(cmd.ExecuteScalar());
        }

        var gallery = CreateGallery(new SharedGalleryFilter(), new RecordingNavigation());
        await gallery.EnsureInitializedAsync();
        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount > 0, "erste Seite");
        var own = gallery.TotalCount;

        for (var i = 0; i < 5; i++)
        {
            WeakReferenceMessenger.Default.Send(new ActiveLibraryChangedMessage(otherId));
            WeakReferenceMessenger.Default.Send(new ActiveLibraryChangedMessage(_db.LibraryId));
        }

        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == own, "eigene Bibliothek wieder geladen");
        await Task.Delay(300); // überholte Ladevorgänge laufen aus
        gallery.TotalCount.Should().Be(own, "der zuletzt angestoßene Wechsel gewinnt");
        gallery.LibraryNameDisplay.Should().NotContain("Zweite");
    });

    [Fact]
    public Task Ueberholter_Bereichswechsel_ueberschreibt_den_Bereich_des_neueren_nicht() => OnUiAsync(async () =>
    {
        // Rauchtest-Wackler „gelöst: Ort-Flyout in A wieder ohne den Ort aus B (Bereich der Galerie: 2)“ (Issue #20, 0.4.41):
        // B liest seinen Bereich langsamer als der gleich danach angestoßene Wechsel auf A — Seite aus A, Flyouts aus B.
        var other = Path.Combine(_db.ArchiveDir, "Zweite");
        Directory.CreateDirectory(other);
        int otherId;
        using (var connection = _db.Factory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO Libraries (Name, RootPath, CreatedAt, LastOpenedAt) VALUES ('Zweite', $root, 0, 0); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$root", other);
            otherId = Convert.ToInt32(cmd.ExecuteScalar());
        }

        var scope = new GatedScope();
        var gallery = CreateGallery(new SharedGalleryFilter(), new RecordingNavigation(), scope);
        await gallery.EnsureInitializedAsync();
        await WaitAsync(() => !gallery.HasPendingWork && gallery.TotalCount > 0, "erste Seite");
        var own = gallery.TotalCount;

        var gate = scope.Hold(otherId);
        WeakReferenceMessenger.Default.Send(new ActiveLibraryChangedMessage(otherId)); // hängt beim Lesen des Bereichs
        await WaitAsync(() => gallery.HasPendingWork, "Wechsel auf B läuft");
        WeakReferenceMessenger.Default.Send(new ActiveLibraryChangedMessage(_db.LibraryId));
        await WaitAsync(() => gallery.TotalCount == own && gallery.ScopeLibraryIdsForSmoke.SequenceEqual([_db.LibraryId]), "A geladen");
        gallery.HasPendingWork.Should().BeTrue("der überholte Wechsel auf B wartet noch");

        gate.SetResult();
        await WaitAsync(() => !gallery.HasPendingWork, "überholter Wechsel ausgelaufen");
        gallery.ScopeLibraryIdsForSmoke.Should().Equal([_db.LibraryId], "der überholte Wechsel darf den Bereich nicht überschreiben");
        gallery.TotalCount.Should().Be(own);
    });

    [Fact]
    public Task Karte_zaehlt_genau_die_Galerie_Treffer_mit_Koordinaten() => OnUiAsync(async () =>
    {
        var shared = new SharedGalleryFilter();
        var gallery = CreateGallery(shared, new RecordingNavigation());
        await gallery.EnsureInitializedAsync();
        var reader = new PlacesCatalogReader(_db.Factory);
        int[] libraries = [_db.LibraryId];

        foreach (var filter in new[] { new GalleryFilterState(FavoritesOnly: true), new GalleryFilterState(GalleryMediaKind.Images), GalleryFilterState.None })
        {
            gallery.FilterState = filter;
            await WaitAsync(() => !gallery.IsLoading && shared.Current?.Filter == filter, $"Filter {filter}");

            var galleryIds = await new GalleryPageSourceProbe(_db).IdsAsync(shared.Current!);
            var media = await reader.GetMediaAsync(libraries, shared.IsActive ? shared.Current : null, CancellationToken.None);
            var (index, _, unlocated, _) = PlacesViewModel.Build(Points(), media);

            var expectedLocated = galleryIds.Count(id => Points().Any(p => p.FileId == id));
            index.PhotoCount.Should().Be(expectedLocated, $"Gruppen-Summe = Galerie-Treffer mit Koordinaten ({filter})");
            unlocated.Should().OnlyContain(id => galleryIds.Contains(id), "„Ohne Ort“ nur aus den Treffern");
            unlocated.Should().NotContain(_ids["b-zip.zip"], "Archive gehören nicht auf die Karte");
        }

        gallery.FilterState = new GalleryFilterState(FavoritesOnly: true);
        await WaitAsync(() => !gallery.IsLoading && shared.IsActive, "Favoriten");
        var favorites = await reader.GetMediaAsync(libraries, shared.Current, CancellationToken.None);
        favorites.Select(m => m.FileId).Should().BeEquivalentTo([_ids["a-fav.jpg"], _ids["a-video.mp4"], _ids["b-fav.jpg"]]);
    });

    [Fact]
    public Task Orte_Seite_laedt_bei_Filteraenderung_neu() => OnUiAsync(async () =>
    {
        var harness = new PlacesHarness();
        harness.SeedAustria();
        var vm = harness.Create();
        await vm.ActivateAsync();
        vm.LocatedCount.Should().Be(5);
        vm.IsFiltered.Should().BeFalse();

        // Galerie setzt einen Filter (Fake: trifft nur Wien) → Karte lädt entprellt neu
        harness.World.FilterMatches = id => id is 1 or 2 or 6;
        harness.Filter.Publish(new GalleryQuery(1, new GalleryFilterState(FavoritesOnly: true), null));
        await WaitAsync(() => vm.LocatedCount == 2 && !vm.IsLoading, "Karte gefiltert");
        vm.UnlocatedCount.Should().Be(1);
        vm.IsFiltered.Should().BeTrue();
        vm.SummaryText.Should().EndWith("gefiltert");
        harness.World.MediaFilters.Last()!.Filter.FavoritesOnly.Should().BeTrue();

        // Zurücksetzen (anderswo) → wieder alles
        harness.Filter.Publish(new GalleryQuery(1, GalleryFilterState.None, null));
        await WaitAsync(() => vm.LocatedCount == 5 && !vm.IsLoading, "Karte ungefiltert");
        harness.World.MediaFilters.Last().Should().BeNull("ohne Filter der bisherige Lesepfad");

        // „Kartenausschnitt“ (aus „In der Galerie zeigen“) gilt auf der Karte selbst nicht
        var mapArea = new GalleryFilterState { Geo = new GalleryGeoFilter(null, [1, 2]) };
        var loadsBefore = harness.World.MediaFilters.Count;
        harness.Filter.Publish(new GalleryQuery(1, mapArea, null));
        harness.Filter.IsActive.Should().BeTrue();
        await WaitAsync(() => harness.World.MediaFilters.Count > loadsBefore && !vm.IsLoading, "neu geladen");
        harness.World.MediaFilters.Last().Should().BeNull("nur Kartenausschnitt → Karte ungefiltert");
        vm.LocatedCount.Should().Be(5);
        vm.IsFiltered.Should().BeFalse();

        harness.Filter.Publish(new GalleryQuery(1, mapArea with { FavoritesOnly = true }, null));
        await WaitAsync(() => vm.IsFiltered && !vm.IsLoading, "Favoriten gefiltert");
        harness.World.MediaFilters.Last()!.Filter.Geo.Should().BeNull("der Kartenausschnitt wird für die Karte entfernt");
    });

    // ---- Hilfen ---------------------------------------------------------------------------------------------------

    private List<GeoPoint> Points()
    {
        using var connection = _db.Factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Latitude, Longitude FROM Files WHERE Latitude IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        var points = new List<GeoPoint>();
        while (reader.Read())
        {
            points.Add(new GeoPoint(reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2)));
        }

        return points;
    }

    private GalleryViewModel CreateGallery(ISharedGalleryFilter shared, INavigationService navigation, Lumora.Services.Library.ILibraryScope? libraryScope = null)
    {
        var source = new GalleryPageSource(_db.Factory);
        return new GalleryViewModel(
            _db.Factory,
            source,
            NullService.Create<IThumbnailService>(),
            NullService.Create<ICatalogUpdateService>(),
            NullService.Create<IFileOperationService>(),
            NullService.Create<IArchiveMoveService>(),
            NullService.Create<ILightboxService>(),
            NullService.Create<IGalleryDialogService>(),
            NullService.Create<IFolderPickerService>(),
            NullService.Create<IArchiveExtractDialogService>(),
            NullService.Create<ITagService>(),
            NullService.Create<ITagPickerService>(),
            NullService.Create<ICategoryService>(),
            NullService.Create<ICategoryPickerService>(),
            NullService.Create<IGalleryViewSettingsStore>(),
            NullLogger<GalleryViewModel>.Instance,
            libraryScope: libraryScope,
            sharedFilter: shared,
            navigation: navigation);
    }

    /// <summary>Bereichsdienst je Bibliothek; <see cref="Hold"/> lässt das Lesen einer Bibliothek warten.</summary>
    private sealed class GatedScope : Lumora.Services.Library.ILibraryScope
    {
        private readonly Dictionary<int, TaskCompletionSource> _gates = new();

        public TaskCompletionSource Hold(int libraryId) => _gates[libraryId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Lumora.Services.Library.LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default)
            => Task.FromResult(Lumora.Services.Library.LibraryScopeInfo.None);

        public async Task<Lumora.Services.Library.LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default)
        {
            if (_gates.TryGetValue(libraryId, out var gate))
            {
                await gate.Task;
            }

            return Lumora.Services.Library.LibraryScopeInfo.ForLibrary(libraryId, libraryId);
        }

        public Task<IReadOnlyList<Lumora.Services.Library.LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Lumora.Services.Library.LibraryGroupInfo>>([]);

        public Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    private Task SelectFolderAsync(GalleryViewModel gallery, int folderId)
    {
        WeakReferenceMessenger.Default.Send(new FolderSelectedMessage(_db.LibraryId, folderId, true));
        return Task.CompletedTask;
    }

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Nicht erreicht: " + what);
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Treffer-Ids einer Galerie-Abfrage (ohne Ordner) über die Galerie-SQL.</summary>
    private sealed class GalleryPageSourceProbe(ArchiveTestDb db)
    {
        public async Task<IReadOnlyList<int>> IdsAsync(GalleryQuery query)
        {
            using var source = new GalleryPageSource(db.Factory);
            source.Open();
            return await source.GetFileIdsAsync(query, null, CancellationToken.None);
        }
    }
}
