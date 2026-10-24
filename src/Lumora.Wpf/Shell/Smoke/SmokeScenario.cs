using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Folders;
using Lumora.Domain.Enums;
using Lumora.Gallery.Controls;
using Lumora.Libraries;
using Lumora.Lightbox;
using Lumora.Services.Archive;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Media;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;
using Lumora.Settings;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Gallery.ListView;
using Lumora.Gallery.Views;
using Lumora.Gallery.Tags;
using Lumora.Gallery.Categories;
using Lumora.Services.Tags;
using Lumora.Settings.TagManagement;
using Lumora.Settings.CategoryManagement;
using Lumora.Services.Categories;
using Lumora.Sorter.ViewModels;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>Ein fehlgeschlagener Prüfpunkt im Rauchtest (der Schritt wird als FAIL protokolliert, der Lauf geht weiter).</summary>
internal sealed class SmokeCheckException : Exception
{
    public SmokeCheckException(string message) : base(message)
    {
    }
}

/// <summary>
/// Integrations-Rauchtest der gesamten App (Stand 0.2.0): Bibliothek anlegen und Fresh-Scan, Galerie (Thumbnails,
/// Sortierung, Seitengröße, FTS-Suche, Filter, Ordnerfilter mit Container-State, Favorit/Bewertung,
/// Verschieben/Rückgängig, Papierkorb), Lightbox (Bild + Video), Ordnerbaum, Einstellungen (Cache, Theme), zum
/// Schluss der zweite Scan: „Aktualisieren“ = Incremental mit stabilen FileIds (ohne Änderung, mit neuer und
/// fehlender Datei, wieder vorhanden) und „Neu aufbauen“ = ClearAndRescan über
/// <see cref="LibraryViewModel.RunSmokeRebuildAsync"/> (neue FileIds, Favoriten/Bewertungen übernommen, Thumbnails neu).
/// Läuft auf dem UI-Thread (alle awaits bleiben im Dispatcher-Kontext) und steuert ausschließlich ViewModels,
/// Commands und Messenger — keine Maus-/Tastatureingaben. Jeder Schritt prüft Dateisystem und DB; Dialoge und
/// Ordnerauswahl sind Fakes (<see cref="SmokeTestRunner.ConfigureServices"/>), das Scan-Ergebnis kommt als
/// <see cref="ScanCompletedMessage"/> vom Messenger.
/// </summary>
internal sealed partial class SmokeScenario
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(120);
    private static readonly Regex TokenSplit = new("[^\\p{L}\\p{N}]+", RegexOptions.Compiled);

    /// <summary>Datei, die der Incremental-Schritt in „Unsortiert“ anlegt (bleibt in der Testbibliothek; vor dem nächsten Lauf entfernt).</summary>
    private const string AddedFileName = "smoke-neu.jpg";

    /// <summary>
    /// Mindestzahl Mediendateien: der Mass-Missing-Guard (&gt; 20 %) darf bei einer geparkten Datei nicht greifen,
    /// sonst öffnete LibraryViewModel den echten Drei-Knopf-Dialog und der unbeaufsichtigte Lauf bliebe stehen.
    /// </summary>
    private const int MinMediaFilesForRescan = 6;

    private readonly INavigationService _navigation;
    private readonly ILogger _logger;
    private readonly string _root;
    private readonly string? _shotsDir;
    private readonly bool _cleanup;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
    private readonly List<string> _results = new();

    private readonly GalleryViewModel _gallery;
    private readonly LibraryViewModel _library;
    private readonly FolderTreeViewModel _folders;
    private readonly SettingsViewModel _settings;
    private readonly ILightboxService _lightbox;
    private readonly ILibraryContextFactory _db;
    private readonly ILibraryService _libraries;
    private readonly IMediaFormatRegistry _formats;
    private readonly IThemeService _theme;
    private readonly IThumbnailService _thumbnails;
    private readonly StatusBarViewModel _statusBar;
    private readonly SmokeFolderPickerService _picker;
    private readonly SmokeArchiveTargetPicker _archivePicker;
    private readonly IFileOperationService _fileOps;
    private readonly IAppDataPaths _paths;
    private readonly ILibraryWatchService _watch;

    /// <summary>Datei, die der Ordnerüberwachungs-Schritt kopiert und wieder löscht (Rückstand nur nach einem Abbruch).</summary>
    private const string WatchedFileName = "smoke-watch.jpg";

    private readonly IArchiveService _archives;
    private readonly ITagService _tags;
    private readonly SmokeTagPickerService? _tagPicker;
    private readonly SmokeCategoryPickerService? _categoryPicker;
    private readonly TagManagementViewModel _tagManagement;
    private readonly ICategoryService _categories;
    private readonly CategoryManagementViewModel _categoryManagement;
    private readonly SorterPageViewModel _sorterPage;

    private int _libraryId;
    private int _mediaCount;
    private readonly HashSet<int> _movedIds = new();

    /// <summary>Ergebnis des zuletzt abgeschlossenen Scans (ScanCompletedMessage kommt vom Scan-Thread).</summary>
    private volatile ScanResult? _lastScan;

    public SmokeScenario(IServiceProvider services, INavigationService navigation, ILogger logger, string root, string? shotsDir, bool cleanup)
    {
        _navigation = navigation;
        _logger = logger;
        _root = root;
        _shotsDir = shotsDir;
        _cleanup = cleanup;

        _gallery = services.GetRequiredService<GalleryViewModel>();
        _library = services.GetRequiredService<LibraryViewModel>();
        _folders = services.GetRequiredService<FolderTreeViewModel>();
        _settings = services.GetRequiredService<SettingsViewModel>();
        _lightbox = services.GetRequiredService<ILightboxService>();
        _db = services.GetRequiredService<ILibraryContextFactory>();
        _libraries = services.GetRequiredService<ILibraryService>();
        _formats = services.GetRequiredService<IMediaFormatRegistry>();
        _theme = services.GetRequiredService<IThemeService>();
        _thumbnails = services.GetRequiredService<IThumbnailService>();
        _statusBar = services.GetRequiredService<StatusBarViewModel>();
        _picker = services.GetRequiredService<SmokeFolderPickerService>();
        _archivePicker = services.GetRequiredService<SmokeArchiveTargetPicker>();
        _fileOps = services.GetRequiredService<IFileOperationService>();
        _paths = services.GetRequiredService<IAppDataPaths>();
        _watch = services.GetRequiredService<ILibraryWatchService>();
        _archives = services.GetRequiredService<IArchiveService>();
        _tags = services.GetRequiredService<ITagService>();
        _tagPicker = services.GetService<SmokeTagPickerService>();
        _categoryPicker = services.GetService<SmokeCategoryPickerService>();
        _tagManagement = services.GetRequiredService<TagManagementViewModel>();
        _categories = services.GetRequiredService<ICategoryService>();
        _categoryManagement = services.GetRequiredService<CategoryManagementViewModel>();
        _sorterPage = services.GetRequiredService<SorterPageViewModel>();
        _scanGate = services.GetRequiredService<IScanGate>();
        _autoExtract = services.GetRequiredService<IArchiveAutoExtractService>();
        _adoptionPicker = services.GetService<SmokeFolderAdoptionPicker>();
        _faceIndex = services.GetService<Lumora.Services.Faces.Indexing.IFaceIndexService>(); // Schritt „Gesichter“ (ADR-0035)
        // Schritt „Personen“ (ADR-0037)
        _personsPage = services.GetService<Lumora.Persons.ViewModels.PersonsViewModel>();
        _recognition = services.GetService<Lumora.Services.Faces.Assignment.IFaceRecognitionService>();
        _personsDialogs = services.GetService<SmokePersonsDialogs>();
        InitSettingsTabs(services); // Einstellungs-Reiter, Info, Bibliotheks-Umschalter (ADR-0042)
        InitLibraryScoping(services); // Schritt „Bibliotheks-Trennung“ (ADR-0048)
        _captureDialog = services.GetService<SmokeCaptureDateDialog>(); // Schritt „Aufnahmedatum“ (ADR-0046)
        InitHiddenLibrary(services); // Schritt „Versteckte Bibliothek“ (ADR-0049)
        InitPlaces(services); // Schritt „Orte“ (ADR-0050)
        InitMotifs(services); // Schritt „Motive“ (ADR-0052, SmokeScenario.Motifs.cs)
        InitDuplicates(services); // Schritt „Doppelte“ (ADR-0053, SmokeScenario.Duplicates.cs)
        InitAlbumsSmoke(services); // Schritt „Alben“ (ADR-0054, SmokeScenario.Albums.cs)
        InitStoriesSmoke(services); // Schritt „Storys“ (ADR-0058, SmokeScenario.Stories.cs)
        InitSlideshowSmoke(services); // Schritt „Diashow“ (ADR-0059, SmokeScenario.Slideshow.cs)
        InitStoryPageSmoke(services); // Schritt „Story-Seite“ (ADR-0060, SmokeScenario.StoryPage.cs)
        _messenger.Register<SmokeScenario, ScanCompletedMessage>(this, static (r, m) => r._lastScan = m.Result);
    }

    public int Failed { get; private set; }

    public IReadOnlyList<string> Results => _results;

    /// <summary>Ablage für die im Incremental-Schritt vorübergehend geparkte Datei — im isolierten AppData, nie neben der Bibliothek.</summary>
    private string ParkingDir => Path.Combine(_paths.AppDataRoot, "smoke-geparkt");

    /// <summary>Eingangs-Ordner für den Schritt „Eingang → Archiv“ — Geschwisterordner der Testbibliothek (nie darunter: er wäre sonst Teil der Archiv-Wurzel).</summary>
    private string InboxDir => Path.Combine(Path.GetDirectoryName(_root) ?? _root, Path.GetFileName(_root) + "-Eingang");

    public async Task RunAsync()
    {
        RemoveLeftovers();
        // Ordnerüberwachung (Services/Watch) erst im eigenen Schritt am Ende: die manuellen Scans und Datei-Operationen der
        // übrigen Schritte dürfen keinen automatischen Diff-Scan auslösen (Zähler-Prüfungen, kein paralleler Scan).
        // Die Einstellungen des Benutzers werden am Ende wiederhergestellt (der Rauchtest läuft ohnehin gegen ein eigenes AppData).
        var watchEnabledBefore = _watch.IsEnabled;
        var watchQuietBefore = _watch.QuietPeriod;
        await _watch.SetEnabledAsync(false).ConfigureAwait(true);
        await DisableMotifsForSmokeAsync().ConfigureAwait(true); // Motiv-Erkennung erst im eigenen Schritt (ADR-0052)
        try
        {
            await RunStepsAsync().ConfigureAwait(true);
        }
        finally
        {
            // nach dem Schritt „Versteckte Bibliothek“ gehört die Überwachung zur neu aufgebauten Sitzung (ADR-0049)
            await CurrentWatch.SetQuietPeriodAsync(watchQuietBefore).ConfigureAwait(true);
            await CurrentWatch.SetEnabledAsync(watchEnabledBefore).ConfigureAwait(true);
        }
    }

    private async Task RunStepsAsync()
    {
        await StepAsync("Start → Startseite", StartPageAsync).ConfigureAwait(true);
        await StepAsync("Bibliothek anlegen + Fresh-Scan", FreshScanAsync).ConfigureAwait(true);
        await StepAsync("Thumbnails JPG/PNG/MP4/MOV", ThumbnailsAsync).ConfigureAwait(true);
        await StepAsync("Sortierung", SortingAsync).ConfigureAwait(true);
        await StepAsync("Seitengröße", PageSizeAsync).ConfigureAwait(true);
        await StepAsync("Suche (FTS)", SearchAsync).ConfigureAwait(true);
        await StepAsync("Filter Bilder/Videos/Favoriten", FiltersAsync).ConfigureAwait(true);
        await StepAsync("Ordnerfilter + Container-State „Leer“", FolderFilterAsync).ConfigureAwait(true);
        await StepAsync("Favorit + Bewertung", FavoriteRatingAsync).ConfigureAwait(true);
        await StepAsync("Verschieben + Rückgängig", MoveUndoAsync).ConfigureAwait(true);
        await StepAsync("Papierkorb", RecycleAsync).ConfigureAwait(true);
        await StepAsync("Lightbox Bild + Video", LightboxAsync).ConfigureAwait(true);
        await StepAsync("Ordnerbaum-Seite", FolderPageAsync).ConfigureAwait(true);
        await StepAsync("Einstellungen + Theme", SettingsAsync).ConfigureAwait(true);
        await StepAsync("Aktualisieren (Incremental) + Neu aufbauen (ClearAndRescan)", RescanAsync).ConfigureAwait(true);
        await StepAsync("Archiv: ZIP → Kachel → Inhaltsansicht → Entpacken → Katalog", ArchiveAsync).ConfigureAwait(true);
        await StepAsync("Eingang → Archiv (zweite Wurzel, Status-Filter, Ins Archiv, Rückgängig, Ordner anlegen/umbenennen)", InboxArchiveAsync).ConfigureAwait(true);
        await StepAsync("Tags (Zuweisen, Filter, Suche, Verwaltung) + Listenansicht", TagsAndListViewAsync).ConfigureAwait(true);
        await StepAsync("Kategorien (Anlegen, Zuweisen, Strg+K, Filter, Suche, Sortierung, Lightbox, Listenspalte, Verwaltung)", CategoriesAsync).ConfigureAwait(true);
        await StepAsync("Sortierer: Eingang → Archiv (Wurzel-Auswahl, gesperrtes Ziel, Katalog, Rückgängig, Flatten im Eingang)", SorterInboxToArchiveAsync).ConfigureAwait(true);
        await StepAsync("Wartezustände: Datei-Operation wartet auf Scan (Statusleiste, Abbrechen), Scan wartet auf Datei-Batch (Bibliotheksseite)", GateWaitAsync).ConfigureAwait(true);
        await StepAsync("Katalog gesperrt: Bewertung wartet auf die Schreibsperre (Statusleiste), kommt danach an", CatalogWriteWaitAsync).ConfigureAwait(true);
        await StepAsync("Auto-Entpacken: ZIP in den Eingang kopieren → Scan → entpackt → Katalog (Einstellung je Bibliothek, vorhandene Archive nachholen)", AutoExtractAsync).ConfigureAwait(true);
        await StepAsync("Ordner übernehmen: Eingang mit Kette leer/leer/inhalt und Verzweigung → Bibliothek, Struktur geprüft, Quelle im Papierkorb, Eingang ausgetragen, Bewertung/Tag wandert mit, Rückgängig (dazu Bewertung/Tags für alle Dateien eines Ordners)", FolderAdoptionAsync).ConfigureAwait(true);
        await StepAsync("Gesichter: Index mit Fake-Analyzer → FaceScans/Faces geschrieben, Statusleiste, Pause/Fortsetzen", FacesAsync).ConfigureAwait(true);
        await StepAsync("Personen: Gruppe benennen (Ausreißer abgewählt), Vorschlag bestätigen/ablehnen, Zusammenführen", PersonsAsync).ConfigureAwait(true);
        await StepAsync("Personen in Galerie/Lightbox: Filter, Suche, Spalte, Rahmen, bestätigen, Gesichtsdaten löschen", PersonsGalleryAsync).ConfigureAwait(true);
        await StepAsync("Kombinierte Filter: Bilder + Favoriten + Tag + Bewertung, × je Bedingung, Filter zurücksetzen (Ordnerauswahl bleibt)", CombinedFiltersAsync).ConfigureAwait(true); // 0.2.43
        await StepAsync("Einstellungen-Reiter und Info: Navigation ohne Bibliothek/Tags/Kategorien, alle Reiter, Info-Version = CHANGELOG, Umschalter, Eingang auf der Ordner-Seite", SettingsTabsAndInfoAsync).ConfigureAwait(true);
        await StepAsync("Vorschaubild-Größe und Bildmaße: kurze Kante min(400, Original), EXIF-6 gedreht, Files.Width/Height, Kachel × Skalierung", ThumbnailSizeAsync).ConfigureAwait(true); // ADR-0043
        await StepAsync("Personen-Details und Beziehungen: benennen mit Vor-/Nachname/Beziehung, Vorschlag beim zweiten Mal, Filter „Beziehung“, Umbenennen in den Einstellungen wirkt überall, „Das bin ich“ eindeutig", PersonDetailsAsync).ConfigureAwait(true); // ADR-0045
        await StepAsync("Collage-Ansicht: umschalten, Reihen randvoll ±1 px, Seitenverhältnisse, Doppelklick öffnet richtige Datei, zurück zu Kacheln", CollageAsync).ConfigureAwait(true); // ADR-0044
        await StepAsync("Aufnahmedatum: EXIF-Foto 1975 nachgetragen, Sortierung Aufnahme ≠ Änderung, Datum setzen ca. 1982 + Rückgängig", CaptureDateAsync).ConfigureAwait(true); // ADR-0046
        await StepAsync("Bibliotheks-Trennung: zweite Bibliothek, Tag/Person/Motiv-Begriff aus A in B und Ort aus B in A unsichtbar, verknüpfen → sichtbar + „Alle verknüpften“ mit Summe und Karte, lösen → getrennt, Begriffe in beiden", LibraryScopingAsync).ConfigureAwait(true); // ADR-0048
        await StepAsync("Motive: Index mit Fake-Encoder → Vektoren/Treffer, Statusleiste, Pause/Fortsetzen, Filter, „Motiv: …“-Suche, Spalte, Lightbox, eigener Begriff ohne neue Analyse", MotifsAsync).ConfigureAwait(true); // ADR-0052
        await StepAsync("Orte: GPS lesen und setzen — Foto mit GPS → Ort erkannt, Ort setzen → EXIF in der Datei, Filter „Ort“, Rückgängig → Bytes identisch", PlacesAsync).ConfigureAwait(true); // ADR-0050
        await StepAsync("Orte-Karte: Seite ohne Kacheln, 3 Fotos per „Hier verorten“ + 2 per Ablegen verortet, Gruppe zerfällt beim Hineinzoomen, „In der Galerie zeigen“", PlacesMapAsync).ConfigureAwait(true); // ADR-0051
        await StepAsync("Doppelte: Kopie und verkleinerte Kopie erzeugt, Prüfsumme gleich, pHash nah, Galerie-Filter, Seite exakt/ähnlich (hell/dunkel), Übrige in den Papierkorb mit Übertragung, Rückgängig mit derselben FileId", DuplicatesAsync).ConfigureAwait(true); // ADR-0053
        await StepAsync("Alben: Album anlegen, 3 Fotos verknüpfen, Galerie gefiltert, Foto in anderen Ordner verknüpfen → Ordneransicht mit Ketten-Kennzeichen und Zähler, Original verschieben → Verknüpfung bleibt + Rückgängig, intelligentes Album speichern/öffnen, Export in Temp", AlbumsAsync).ConfigureAwait(true); // ADR-0054
        await StepAsync("Storys: Erkennung (Mindestgröße 5), nur Dateien der Gruppe, Übersicht und Story-Ansicht, Story aus Auswahl mit Titelbild, anheften, als Album speichern, Neuberechnung behält den Zustand", StoriesAsync).ConfigureAwait(true); // ADR-0058
        await StepAsync("Diashow: aus der Galerie (ganze Ansicht), zwei weiter, Video läuft und wird übersprungen, Esc → Galerie, „Diashow ab hier“ aus der Lightbox", SlideshowAsync).ConfigureAwait(true); // ADR-0059
        await StepAsync("Story-Seite: Navigation „Storys“, Übersicht zeigt die erkannten Storys, Statusleiste, Story öffnen (gesehen), Highlights ↔ Alle Fotos, Anheften, Diashow + Esc, zurück (hell/dunkel)", StoryPageAsync).ConfigureAwait(true); // ADR-0060
        await StepAsync("Ungenaue Orte: Genauigkeit bewertet, synthetische GPX-Spur zu zwei Fotos ohne Ort eingelesen (Doppelte übersprungen), Vorschläge „Garmin-Spur“ und Zähler, Karte mit Spur, „nur im Katalog“ übernommen (Datei unverändert) + Rückgängig, Bereich „Ungenau“, Einstellungen › Orte (hell/dunkel)", InaccuratePlacesAsync).ConfigureAwait(true); // ADR-0062
        await StepAsync("Story-Assistent: Satz (Attrappe statt Ollama) → Verständnis-Karte mit Treffern → eigene Story → Collage aus den Originalen in Temp (1920 × 1080) → Verlauf, aufgeräumt (hell/dunkel)", StoryAssistantAsync).ConfigureAwait(true); // ADR-0063
        await StepAsync("Versteckte Bibliothek: anlegen (_hidden simuliert), Fotos im eigenen Katalog im Container, Zusperren nach Untätigkeit + Aushängen (Fake), falsches Kennwort, wieder öffnen, normale Bibliothek unverändert, keine Spuren im AppData, Sicherheits-Check (Fake-Leser, hell/dunkel, im normalen Modus kein Reiter)", HiddenLibraryAsync).ConfigureAwait(true); // ADR-0049
        // Nach dem Katalogwechsel ist die normale Sitzung neu aufgebaut — die restlichen Schritte laufen mit deren Diensten
        await RunAfterHiddenLibraryAsync(async s =>
        {
            await s.StepAsync("Ordnerüberwachung: Datei kopieren → automatischer Diff-Scan → Galerie", s.WatchAsync).ConfigureAwait(true);
            if (s._cleanup)
            {
                await s.StepAsync("Cleanup", s.CleanupAsync).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Rückstände eines früheren Laufs gegen dieselbe Bibliothek entfernen: die angelegte Datei (sonst meldet der
    /// Incremental-Schritt Added = 0) und eine noch geparkte Datei zurückstellen (abgebrochener Lauf).
    /// </summary>
    private void RemoveLeftovers()
    {
        var unsorted = Path.Combine(_root, "Unsortiert");
        var watched = Path.Combine(unsorted, WatchedFileName);
        if (File.Exists(watched))
        {
            File.Delete(watched); // Rückstand des Ordnerüberwachungs-Schritts nach einem Abbruch
        }

        var leftover = Path.Combine(unsorted, AddedFileName);
        if (File.Exists(leftover))
        {
            File.Delete(leftover);
            _logger.LogInformation("Rauchtest   Rückstand entfernt: {Path}", leftover);
        }

        RemoveArchiveLeftovers(unsorted);
        if (Directory.Exists(ParkingDir))
        {
            foreach (var parked in Directory.EnumerateFiles(ParkingDir))
            {
                var back = Path.Combine(unsorted, Path.GetFileName(parked));
                File.Move(parked, back, overwrite: true);
                _logger.LogInformation("Rauchtest   Geparkte Datei zurückgestellt: {Path}", back);
            }

            Directory.Delete(ParkingDir);
        }

        if (Directory.Exists(InboxDir))
        {
            Directory.Delete(InboxDir, recursive: true);
            _logger.LogInformation("Rauchtest   Eingangs-Ordner eines früheren Laufs entfernt: {Path}", InboxDir);
        }

        // Ordner, die der Schritt „Eingang → Archiv“ in der Archiv-Wurzel anlegt (nach Rückgängig leer)
        var smokeYear = Path.Combine(_root, "2026");
        foreach (var name in new[] { "Smoke-Archiv", "Smoke-Neu", "Smoke-Umbenannt" })
        {
            var dir = Path.Combine(smokeYear, name);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                _logger.LogInformation("Rauchtest   Ordner eines früheren Laufs entfernt: {Path}", dir);
            }
        }

        if (Directory.Exists(smokeYear) && !Directory.EnumerateFileSystemEntries(smokeYear).Any())
        {
            Directory.Delete(smokeYear);
        }
    }

    /// <summary>
    /// ADR-0020: Geschwisterordner als Eingangs-Wurzel aufnehmen, Incremental-Scan über beide Wurzeln (Added = 2,
    /// RootsScanned = 2), Filter „Nicht archiviert“ (2, Badge „Eingang“), „Ins Archiv…“ mit Fake-Zieldialog nach
    /// <c>2026\Smoke-Archiv</c> (Dateien + Files.FolderId unter der Archiv-Wurzel, Ansicht bleibt beim Filter und ist
    /// leer, Setting archive.lastTarget), Filter „Archiviert“ = alle, Rückgängig (zurück in den Eingang), Ordner
    /// anlegen + umbenennen (MkDir/RenameFolder-Batches), Eingangs-Wurzel entfernen (nur Katalog).
    /// </summary>
    private async Task InboxArchiveAsync()
    {
        var inbox = InboxDir;
        Directory.CreateDirectory(inbox);
        var unsorted = Path.Combine(_root, "Unsortiert");
        var sources = Directory.EnumerateFiles(unsorted, "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        Check(sources.Count == 2, "2 JPGs aus „Unsortiert“ als Eingangs-Material kopiert");
        var inboxFiles = new List<string>();
        foreach (var source in sources)
        {
            var copy = Path.Combine(inbox, "eingang-" + Path.GetFileName(source));
            File.Copy(source, copy, overwrite: true);
            inboxFiles.Add(copy);
        }

        // (1) Eingangs-Wurzel hinzufügen
        var inboxRoot = await _libraries.AddRootAsync(_libraryId, inbox, LibraryRootRole.Inbox).ConfigureAwait(true);
        var info = await _libraries.GetAsync(_libraryId).ConfigureAwait(true);
        Check(info is not null && info.Roots.Count == 2 && info.ArchiveRoot is { } && PathEquals(info.ArchiveRoot.Path, _root) && inboxRoot.IsInbox,
            $"Bibliothek hat 2 Wurzeln: Archiv „{info?.ArchiveRoot?.Path}“ + Eingang „{inboxRoot.Path}“");
        var dupe = await CatchAsync(() => _libraries.AddRootAsync(_libraryId, Path.Combine(_root, "2024"), LibraryRootRole.Inbox)).ConfigureAwait(true);
        Check(dupe is InvalidOperationException, "Ordner innerhalb der Archiv-Wurzel wird als Eingang abgelehnt");

        // (2) Incremental-Scan über beide Wurzeln
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_library.ErrorMessage is null && !_library.ScanSummaryIsWarning, $"Aktualisieren mit Eingang: {_library.ScanSummary}");
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 2, RootsScanned: 2 },
            $"Incremental über 2 Wurzeln: Added={_lastScan?.Added}, RootsScanned={_lastScan?.RootsScanned}");
        _mediaCount += 2;
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount,
            ShortWait,
            $"Galerie zeigt {_mediaCount} Dateien inkl. Eingang").ConfigureAwait(true);
        var inboxRootId = await DbScalarAsync<int>("SELECT Id FROM LibraryRoots WHERE LibraryId = $lib AND Role = 0", ("$lib", _libraryId)).ConfigureAwait(true);
        var archiveRootId = await DbScalarAsync<int>("SELECT Id FROM LibraryRoots WHERE LibraryId = $lib AND Role = 1", ("$lib", _libraryId)).ConfigureAwait(true);
        var inboxFolderRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Folders WHERE LibraryId = $lib AND RootId = $r", ("$lib", _libraryId), ("$r", inboxRootId)).ConfigureAwait(true);
        var nullRootRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Folders WHERE LibraryId = $lib AND RootId IS NULL", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(inboxFolderRows == 1 && nullRootRows == 0, $"DB: Folders.RootId gesetzt (Eingang {inboxFolderRows} Ordner, {nullRootRows} ohne Wurzel)");

        // (3) Filter „Nicht archiviert“ + Badge
        _gallery.FilterState = new GalleryFilterState(Location: GalleryLocation.Inbox);
        await WaitForGalleryAsync("Filter Nicht archiviert").ConfigureAwait(true);
        Check(_gallery.TotalCount == 2 && _gallery.CurrentPage.All(i => i.IsInbox), $"Filter „Nicht archiviert“: {_gallery.TotalCount} Dateien mit Badge „Eingang“");
        await SnapshotAsync("11-galerie-eingang").ConfigureAwait(true);

        // (4) Ins Archiv… (Fake-Zieldialog) — Ansicht bleibt beim Filter und ist danach leer
        var archived = _gallery.CurrentPage.ToList();
        var archivedIds = string.Join(",", archived.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)));
        foreach (var item in archived)
        {
            item.IsSelected = true;
        }

        var target = Path.Combine(_root, "2026", "Smoke-Archiv");
        _archivePicker.NextTarget = target;
        await _gallery.ArchiveSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Ins Archiv beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Ins Archiv").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Ins Archiv ohne Fehler ({_gallery.StatusText})");
        var request = _archivePicker.LastRequest;
        Check(request is { } && request.ArchiveRootId == archiveRootId && request.FileIds.Count == 2 && request.SuggestedRelativePath is not null,
            $"Zieldialog bekam Archiv-Wurzel, 2 Dateien, Vorschlag „{request?.SuggestedRelativePath}“");
        foreach (var item in archived)
        {
            Check(File.Exists(Path.Combine(target, item.Name)) && !File.Exists(item.SourcePath), $"Datei archiviert: {item.Name}");
        }

        var underArchive = await DbScalarAsync<int>(
            $"SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({archivedIds}) AND d.RootId = $r", ("$r", archiveRootId)).ConfigureAwait(true);
        Check(underArchive == 2, "DB: Files.FolderId zeigt auf einen Ordner der Archiv-Wurzel");
        Check(_gallery.FilterState == new GalleryFilterState(Location: GalleryLocation.Inbox) && _gallery.TotalCount == 0 && _gallery.IsEmpty, $"Ansicht bleibt bei „Nicht archiviert“ und ist leer: {_gallery.EmptyMessage}");
        var remembered = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", "archive.lastTarget." + _libraryId)).ConfigureAwait(true);
        Check(PathEquals(remembered ?? string.Empty, target), $"Setting archive.lastTarget = {remembered}");

        _gallery.FilterState = new GalleryFilterState(Location: GalleryLocation.Library);
        await WaitForGalleryAsync("Filter Archiviert").ConfigureAwait(true);
        Check(_gallery.TotalCount == _mediaCount && _gallery.CurrentPage.All(i => !i.IsInbox), $"Filter „Archiviert“: {_gallery.TotalCount} Dateien");

        // (5) Rückgängig → zurück in den Eingang
        Check(_gallery.CanUndo, "Rückgängig verfügbar");
        await _gallery.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Rückgängig beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Rückgängig").ConfigureAwait(true);
        foreach (var item in archived)
        {
            Check(File.Exists(item.SourcePath) && !File.Exists(Path.Combine(target, item.Name)), $"Datei zurück im Eingang: {item.Name}");
        }

        var underInbox = await DbScalarAsync<int>(
            $"SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({archivedIds}) AND d.RootId = $r", ("$r", inboxRootId)).ConfigureAwait(true);
        Check(underInbox == 2, "DB: Files.FolderId wieder unter der Eingangs-Wurzel");
        _gallery.FilterState = new GalleryFilterState(Location: GalleryLocation.Inbox);
        await WaitForGalleryAsync("Filter Nicht archiviert nach Rückgängig").ConfigureAwait(true);
        Check(_gallery.TotalCount == 2, $"„Nicht archiviert“ nach Rückgängig: {_gallery.TotalCount}");

        // (6) Ordner anlegen + umbenennen (Dateisystem + Folders-Zeile, protokolliert)
        var created = await _fileOps.CreateFolderAsync(_libraryId, Path.Combine(_root, "2026"), "Smoke-Neu").ConfigureAwait(true);
        Check(created.Success && created.FolderId is not null && Directory.Exists(created.Path), $"Ordner angelegt: {created.Path} (FolderId {created.FolderId})");
        var renamed = await _fileOps.RenameFolderAsync(created.FolderId!.Value, "Smoke-Umbenannt").ConfigureAwait(true);
        Check(renamed.Success && Directory.Exists(renamed.Path) && !Directory.Exists(created.Path), $"Ordner umbenannt: {renamed.Path}");
        var renamedRow = await DbScalarAsync<string>("SELECT Path FROM Folders WHERE Id = $id", ("$id", created.FolderId.Value)).ConfigureAwait(true);
        Check(PathEquals(renamedRow ?? string.Empty, renamed.Path), $"DB: Folders.Path = {renamedRow}");
        var folderBatches = await DbScalarAsync<int>("SELECT COUNT(*) FROM OperationBatches WHERE OperationType IN (4, 5) AND Status = 2").ConfigureAwait(true);
        Check(folderBatches >= 2, $"OperationBatches MkDir/RenameFolder angewendet: {folderBatches}");

        // (7) Eingangs-Wurzel entfernen (nur Katalog), Dateien bleiben auf der Platte
        _gallery.FilterState = GalleryFilterState.None;
        await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);
        await _libraries.RemoveRootAsync(inboxRoot.Id).ConfigureAwait(true);
        var leftRows = await DbScalarAsync<int>($"SELECT COUNT(*) FROM Files WHERE Id IN ({archivedIds})").ConfigureAwait(true);
        Check(leftRows == 0 && inboxFiles.All(File.Exists), "Eingang entfernt: Katalogzeilen weg, Dateien auf der Platte unverändert");
        _mediaCount -= 2;
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, $"Galerie nach Entfernen des Eingangs: {_mediaCount}").ConfigureAwait(true);
        var rootsLeft = await DbScalarAsync<int>("SELECT COUNT(*) FROM LibraryRoots WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(rootsLeft == 1, "nur noch die Archiv-Wurzel");
        Directory.Delete(inbox, recursive: true);
    }

    // ---- Sortierer: Eingang → Archiv (0.2.16, ADR-0024) ----------------------------------------

    /// <summary>Eingangs-Ordner des Sortierer-Schritts — Geschwisterordner der Testbibliothek, eigener Name.</summary>
    private string SorterInboxDir => Path.Combine(Path.GetDirectoryName(_root) ?? _root, Path.GetFileName(_root) + "-Sortierer-Eingang");

    /// <summary>
    /// Sortierer und Flatten über Wurzelgrenzen (ADR-0020/ADR-0024): Geschwisterordner als Eingang (Handy\sm01_0001.jpg,
    /// sm01_0002.jpg, sm02_0001.jpg, Handy\Reise\Tag1\sm03_0001.jpg), Incremental über 2 Wurzeln, Vorbelegung der
    /// Wurzel-Auswahl (Quelle = Eingang, Ziel = Archiv), gesperrtes Ziel außerhalb, Sortieren nach
    /// <c>Archiv\Smoke-Sortiert</c> (Dateien, Folders.RootId, stabile FileIds, FTS, Filter „Nicht archiviert“),
    /// Rückgängig, Flatten im Eingang + Rückgängig, Eingang entfernen (Auswahl lädt neu).
    /// </summary>
    private async Task SorterInboxToArchiveAsync()
    {
        var inbox = SorterInboxDir;
        var target = Path.Combine(_root, "Smoke-Sortiert");
        foreach (var leftover in new[] { inbox, target })
        {
            if (Directory.Exists(leftover))
            {
                Directory.Delete(leftover, recursive: true);
                _logger.LogInformation("Rauchtest   Rückstand eines früheren Laufs entfernt: {Path}", leftover);
            }
        }

        // (1) Eingang mit 4 Dateien anlegen, als Wurzel aufnehmen und scannen
        var handy = Path.Combine(inbox, "Handy");
        var deep = Path.Combine(handy, "Reise", "Tag1");
        Directory.CreateDirectory(deep);
        var sources = Directory.EnumerateFiles(Path.Combine(_root, "Unsortiert"), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        Check(sources.Count == 3, "3 JPGs aus „Unsortiert“ als Eingangs-Material");
        var names = new[] { "sm01_0001.jpg", "sm01_0002.jpg", "sm02_0001.jpg" };
        for (var i = 0; i < names.Length; i++)
        {
            File.Copy(sources[i], Path.Combine(handy, names[i]), overwrite: true);
        }

        File.Copy(sources[0], Path.Combine(deep, "sm03_0001.jpg"), overwrite: true);
        var inboxRoot = await _libraries.AddRootAsync(_libraryId, inbox, LibraryRootRole.Inbox).ConfigureAwait(true);
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 4, RootsScanned: 2 },
            $"Incremental über 2 Wurzeln: Added={_lastScan?.Added}, RootsScanned={_lastScan?.RootsScanned}");
        _mediaCount += 4;
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount,
            ShortWait,
            $"Galerie zeigt {_mediaCount} Dateien inkl. Eingang").ConfigureAwait(true);
        var archiveRootId = await DbScalarAsync<int>("SELECT Id FROM LibraryRoots WHERE LibraryId = $lib AND Role = 1", ("$lib", _libraryId)).ConfigureAwait(true);
        var ids = new List<int>();
        foreach (var name in names)
        {
            ids.Add(await DbScalarAsync<int>("SELECT Id FROM Files WHERE Name = $n", ("$n", name)).ConfigureAwait(true));
        }

        Check(ids.All(id => id > 0), $"FileIds im Katalog: {string.Join(", ", ids)}");
        var idList = string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

        // (2) Sortieren-Seite: Vorbelegung der Wurzel-Auswahl
        _navigation.NavigateTo("sorter");
        await WaitUntilAsync(() => _navigation.CurrentTag == "sorter", ShortWait, "Sortieren-Seite").ConfigureAwait(true);
        await _sorterPage.EnsureInitializedAsync().ConfigureAwait(true);
        var sort = _sorterPage.Sort;
        var flatten = _sorterPage.Flatten;
        await WaitUntilAsync(() => !sort.IsBusy && sort.Location.SourceRoots.Count == 2 && flatten.Location.SourceRoots.Count == 2, ShortWait, "Wurzel-Auswahl mit 2 Wurzeln").ConfigureAwait(true);
        Check(sort.Location.SelectedSourceRoot?.RootId == inboxRoot.Id && PathEquals(sort.Location.SourceFolder, inbox),
            $"Quelle vorbelegt mit dem Eingang: {sort.Location.SourceFolder}");
        Check(sort.Location.SelectedTargetRoot?.RootId == archiveRootId && PathEquals(sort.Location.TargetRoot ?? string.Empty, _root),
            $"Ziel vorbelegt mit der Archiv-Wurzel: {sort.Location.TargetRoot}");
        Check(flatten.Location.SelectedSourceRoot?.RootId == inboxRoot.Id, "Flatten: Quelle ebenfalls der Eingang");

        // (3) Analyse im Eingang; Ziel außerhalb der Bibliothek ist gesperrt
        sort.Location.SourceFolder = handy;
        sort.Location.TargetRoot = _paths.AppDataRoot;
        Check(sort.Location.IsTargetBlocked, $"Ziel außerhalb gesperrt: {sort.Location.TargetHint}");
        await sort.AnalyzeCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(sort.ErrorMessage is null && sort.Groups.Count == 2 && sort.Groups.Any(g => g.Key == "sm01" && g.FileCount == 2),
            $"Analyse: {sort.AnalysisSummary} ({sort.ErrorMessage})");
        await sort.ExecuteCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(sort.PlanTargetBlocked && sort.ErrorMessage is not null && names.All(n => File.Exists(Path.Combine(handy, n))),
            $"Gesperrt, nichts verschoben: {sort.ErrorMessage}");

        // (4) Sortieren Eingang → Archiv\Smoke-Sortiert
        sort.Location.TargetRoot = target;
        Check(!sort.Location.IsTargetBlocked && sort.Location.SelectedTargetRoot?.RootId == archiveRootId, $"Ziel unter dem Archiv: {sort.Location.TargetHint}");
        sort.PreviewCommand.Execute(null);
        Check(!sort.PlanTargetBlocked && sort.PlanCount == 3, $"Vorschau: {sort.PlanSummary}");
        await SnapshotAsync("14-sortierer-eingang-archiv").ConfigureAwait(true);
        await sort.ExecuteCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(sort.ErrorMessage is null && sort.CanUndo, $"Sortiert: {sort.StatusText}");
        Check(File.Exists(Path.Combine(target, "sm01", "sm01_0001.jpg")) && File.Exists(Path.Combine(target, "sm02", "sm02_0001.jpg"))
              && !names.Any(n => File.Exists(Path.Combine(handy, n))), "Dateien liegen unter Archiv\\Smoke-Sortiert");
        var underArchive = await DbScalarAsync<int>(
            $"SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({idList}) AND d.RootId = $r", ("$r", archiveRootId)).ConfigureAwait(true);
        Check(underArchive == 3, $"DB: dieselben FileIds unter der Archiv-Wurzel ({underArchive}/3)");
        var ftsHits = await DbScalarAsync<int>($"SELECT COUNT(*) FROM FilesFts WHERE FilesFts MATCH '\"sm01\"' AND rowid IN ({idList})").ConfigureAwait(true);
        Check(ftsHits == 2, $"FTS findet die verschobenen Dateien ({ftsHits}/2)");
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie aktiv").ConfigureAwait(true);
        _gallery.FilterState = new GalleryFilterState(Location: GalleryLocation.Inbox);
        await WaitForGalleryAsync("Filter Nicht archiviert nach Sortieren").ConfigureAwait(true);
        Check(_gallery.TotalCount == 1, $"„Nicht archiviert“ nach dem Sortieren: {_gallery.TotalCount} (nur Handy\\Reise\\Tag1)");

        // (5) Rückgängig → zurück in den Eingang
        await sort.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(names.All(n => File.Exists(Path.Combine(handy, n))) && !Directory.Exists(target), $"Rückgängig: {sort.StatusText}");
        var backInInbox = await DbScalarAsync<int>(
            $"SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({idList}) AND d.RootId = $r", ("$r", inboxRoot.Id)).ConfigureAwait(true);
        Check(backInInbox == 3, $"DB: FileIds wieder unter der Eingangs-Wurzel ({backInInbox}/3)");
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == 4, ShortWait, "„Nicht archiviert“ nach Rückgängig: 4 Dateien").ConfigureAwait(true);

        // (6) Flatten im Eingang (Ziel = Quellordner) + Rückgängig
        flatten.Location.SourceFolder = handy;
        flatten.Location.SelectedTargetRoot = SorterRootOption.SourceFolderOption;
        await flatten.AnalyzeCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(flatten.ErrorMessage is null && flatten.Folders.Count == 2, $"Flatten-Analyse: {flatten.AnalysisSummary} ({flatten.ErrorMessage})");
        await flatten.ExecuteCommand.ExecuteAsync(null).ConfigureAwait(true);
        var flattened = Path.Combine(handy, "Reise_Tag1", "sm03_0001.jpg");
        Check(flatten.ErrorMessage is null && File.Exists(flattened) && !Directory.Exists(Path.Combine(handy, "Reise")), $"Flatten: {flatten.StatusText}");
        var flatRoot = await DbScalarAsync<int>(
            "SELECT d.RootId FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Name = $n", ("$n", "sm03_0001.jpg")).ConfigureAwait(true);
        Check(flatRoot == inboxRoot.Id, "DB: Flatten bleibt unter der Eingangs-Wurzel");
        await flatten.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(deep, "sm03_0001.jpg")) && !File.Exists(flattened), $"Flatten rückgängig: {flatten.StatusText}");

        // (7) Eingang entfernen (nur Katalog) — die Wurzel-Auswahl lädt über LibraryRootsChangedMessage neu
        _gallery.FilterState = GalleryFilterState.None;
        await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);
        await _libraries.RemoveRootAsync(inboxRoot.Id).ConfigureAwait(true);
        _mediaCount -= 4;
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, $"Galerie nach Entfernen des Eingangs: {_mediaCount}").ConfigureAwait(true);
        await WaitUntilAsync(() => sort.Location.SourceRoots.Count == 1 && flatten.Location.SourceRoots.Count == 1, ShortWait, "Wurzel-Auswahl ohne Eingang").ConfigureAwait(true);
        Check(PathEquals(sort.Location.SourceFolder, _root), $"Quelle fällt auf die Archiv-Wurzel zurück: {sort.Location.SourceFolder}");
        Directory.Delete(inbox, recursive: true);
    }

    // ---- Schritte ------------------------------------------------------------------------------

    private async Task StartPageAsync()
    {
        var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
        await WaitUntilAsync(() => _navigation.CurrentTag is not null, ShortWait, "erste Navigation").ConfigureAwait(true);
        // Ohne Bibliothek (Ersteinrichtung): Einstellungen › Bibliotheken & Eingänge (ADR-0042), sonst Galerie
        if (active is null)
        {
            await WaitUntilAsync(() => IsOnSettingsSection(NavigationTags.Library), ShortWait, "Reiter Bibliotheken & Eingänge").ConfigureAwait(true);
            Check(IsOnSettingsSection(NavigationTags.Library),
                $"Startseite ohne Bibliothek = Einstellungen › Bibliotheken & Eingänge ({_navigation.CurrentTag}/{_settingsSections.SelectedSection})");
            await WaitUntilAsync(() => !_switcher.HasLibrary && _switcher.ActiveName == "Keine Bibliothek", ShortWait, "Umschalter ohne Bibliothek").ConfigureAwait(true);
            Check(_switcher.CaptionText == "Bibliothek einrichten", $"Umschalter: {_switcher.ActiveName} / {_switcher.CaptionText}");
        }
        else
        {
            Check(_navigation.CurrentTag == NavigationTags.Gallery, $"Startseite = Galerie (aktive Bibliothek: {active.Name})");
        }

        await SnapshotAsync("01-start-bibliothek").ConfigureAwait(true);
    }

    private async Task FreshScanAsync()
    {
        _mediaCount = CountMediaFiles();
        _navigation.NavigateTo(NavigationTags.Library);
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_library.ErrorMessage is null, $"Scan ohne Fehler ({_library.ScanSummaryTitle}: {_library.ScanSummary})");
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Wechsel zur Galerie nach dem Scan").ConfigureAwait(true);
        await WaitUntilAsync(
            () => _gallery.HasLibrary && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount && _gallery.CurrentPage.Count == _mediaCount,
            ShortWait,
            $"Galerie zeigt {_mediaCount} Dateien").ConfigureAwait(true);

        _libraryId = await DbScalarAsync<int>("SELECT Id FROM Libraries ORDER BY LastOpenedAt DESC, Id DESC LIMIT 1").ConfigureAwait(true);
        var files = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        var fts = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts").ConfigureAwait(true);
        var txt = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Extension = '.txt'").ConfigureAwait(true);
        Check(files == _mediaCount, $"Files-Tabelle: {files} Zeilen (erwartet {_mediaCount})");
        Check(fts == files, $"FTS-Index: {fts} Zeilen");
        Check(txt == 0, "TXT-Datei ignoriert");
        Check(!_gallery.IsRootOffline, "Bibliotheksordner online");
    }

    private async Task ThumbnailsAsync()
    {
        // Erst auf die geladene Galerieseite warten: unter Last kommt das Kachel-Grid nach dem Fresh-Scan verzögert —
        // eine leere Seite hätte sonst „0 fehlende Bilder“ gemeldet und den Schritt sofort beendet.
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.CurrentPage.Count > 0, ShortWait, "Galerieseite geladen").ConfigureAwait(true);

        // Warten, bis jede Kachel ein Bild hat — oder die Queue leer bleibt (Fehler je Datei stehen dann in der DB).
        var deadline = DateTime.UtcNow + LongWait;
        while (DateTime.UtcNow < deadline)
        {
            var missing = _gallery.CurrentPage.Count(i => i.Thumbnail is null);
            if (missing == 0 && _gallery.CurrentPage.Count > 0)
            {
                break;
            }

            if (_thumbnails.PendingCount == 0)
            {
                await Task.Delay(2000).ConfigureAwait(true);
                if (_gallery.CurrentPage.Count(i => i.Thumbnail is null) == missing && _thumbnails.PendingCount == 0)
                {
                    break;
                }
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        var withThumb = _gallery.CurrentPage.Count(i => i.Thumbnail is not null);
        var byStatus = await DbRowsAsync("SELECT Status, COUNT(*) FROM Thumbnails GROUP BY Status").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Thumbnails je Status (2=Generated, 3=Failed): {Rows}", string.Join("; ", byStatus));
        var failures = await DbRowsAsync(
            "SELECT f.Name, t.ErrorCode, t.ErrorMessage FROM Thumbnails t JOIN Files f ON f.Id = t.FileId WHERE t.Status = 3").ConfigureAwait(true);
        foreach (var failure in failures)
        {
            _logger.LogWarning("Rauchtest   Thumbnail FEHLER: {Row}", failure);
        }

        var perExtension = await DbRowsAsync(
            "SELECT f.Extension, COUNT(*) FROM Thumbnails t JOIN Files f ON f.Id = t.FileId WHERE t.Status = 2 GROUP BY f.Extension ORDER BY f.Extension").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Generated je Endung: {Rows}", string.Join("; ", perExtension));
        var generated = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE Status = 2").ConfigureAwait(true);
        var cachePaths = await DbRowsAsync("SELECT CachePath FROM Thumbnails WHERE Status = 2").ConfigureAwait(true);
        var missingOnDisk = cachePaths.Count(p => !File.Exists(p));
        var portrait = await DbRowsAsync(
            "SELECT f.Name, t.ByteSize FROM Thumbnails t JOIN Files f ON f.Id = t.FileId WHERE t.Status = 2 ORDER BY f.Name LIMIT 3").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Beispiel-Thumbnails: {Rows}", string.Join("; ", portrait));

        Check(generated == _mediaCount, $"Thumbnails Generated: {generated}/{_mediaCount} (Failed: {failures.Count})");
        foreach (var ext in new[] { ".jpg", ".png", ".mp4", ".mov" })
        {
            Check(perExtension.Any(r => r.StartsWith(ext + " |", StringComparison.OrdinalIgnoreCase)), $"Thumbnail für {ext} erzeugt");
        }

        Check(missingOnDisk == 0, $"alle {cachePaths.Count} Cache-Dateien vorhanden");
        Check(withThumb == _mediaCount, $"Kacheln mit Vorschaubild: {withThumb}/{_mediaCount}");
        await SnapshotAsync("02-galerie-thumbnails").ConfigureAwait(true);
    }

    private async Task SortingAsync()
    {
        await SetSortAsync(GallerySort.NameAsc).ConfigureAwait(true);
        var names = _gallery.CurrentPage.Select(i => i.Name).ToList();
        Check(IsSorted(names, (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase)), $"Name A→Z: „{names.First()}“ … „{names.Last()}“");

        await SetSortAsync(GallerySort.NameDesc).ConfigureAwait(true);
        names = _gallery.CurrentPage.Select(i => i.Name).ToList();
        Check(IsSorted(names, (a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase)), $"Name Z→A: „{names.First()}“ … „{names.Last()}“");

        await SetSortAsync(GallerySort.SizeDesc).ConfigureAwait(true);
        var sizes = _gallery.CurrentPage.Select(i => i.Size).ToList();
        Check(IsSorted(sizes, (a, b) => b.CompareTo(a)), $"Größe groß→klein: {sizes.First():N0} … {sizes.Last():N0} B");

        await SetSortAsync(GallerySort.ModifiedDesc).ConfigureAwait(true);
        Check(_gallery.TotalCount == _mediaCount && _gallery.CurrentPage.Count == _mediaCount, "Standard-Sortierung wiederhergestellt");
    }

    private async Task PageSizeAsync()
    {
        _gallery.PageSize = 200;
        await WaitForGalleryAsync("Seitengröße 200").ConfigureAwait(true);
        Check(_gallery.PageSize == 200 && _gallery.PageCount == 1 && _gallery.CurrentPage.Count == _gallery.TotalCount,
            $"Seitengröße 200 → {_gallery.PageCount} Seite(n), {_gallery.CurrentPage.Count} Kacheln");
        Check(!_gallery.NextPageCommand.CanExecute(null) && !_gallery.PrevPageCommand.CanExecute(null), "Pager bei einer Seite deaktiviert");
        _gallery.PageSize = 500;
        await WaitForGalleryAsync("Seitengröße 500").ConfigureAwait(true);
        Check(_gallery.PageSize == 500 && _gallery.StatusText.Contains("Seite 1 von 1", StringComparison.Ordinal), $"Status: {_gallery.StatusText}");
    }

    private async Task SearchAsync()
    {
        var expected = MediaFiles().Count(f => TokenSplit.Split(Path.GetFileName(f)).Any(t => t.StartsWith("img", StringComparison.OrdinalIgnoreCase)));
        _gallery.SearchTerm = "IMG";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche IMG").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, "Suche ohne Fehler");
        Check(_gallery.TotalCount == expected, $"Suche „IMG“: {_gallery.TotalCount} Treffer (erwartet {expected})");

        _gallery.SearchTerm = "foto-(2024):\"x*";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche mit Sonderzeichen").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Sonderzeichen-Suche ohne FTS-Syntaxfehler ({_gallery.TotalCount} Treffer)");

        // Nur Sonderzeichen = kein suchbarer Inhalt (FtsQueryBuilder liefert null) → keine Einschränkung, kein FTS-Fehler
        _gallery.SearchTerm = "---";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nur Sonderzeichen").ConfigureAwait(true);
        Check(
            _gallery.ErrorMessage is null && _gallery.SearchTerm is null && _gallery.TotalCount == _mediaCount,
            $"Suche „---“ → kein suchbarer Inhalt: Feld geleert, Hinweis „{_gallery.StatusText}“, alle {_gallery.TotalCount} Dateien");

        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);
        Check(_gallery.SearchTerm is null && _gallery.TotalCount == _mediaCount, "Suche geleert → alle Dateien");
    }

    private async Task FiltersAsync()
    {
        var videos = MediaFiles().Count(f => _formats.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        var images = _mediaCount - videos;

        _gallery.FilterState = new GalleryFilterState(GalleryMediaKind.Videos);
        await WaitForGalleryAsync("Filter Videos").ConfigureAwait(true);
        Check(_gallery.TotalCount == videos && _gallery.CurrentPage.All(i => i.IsVideo), $"Filter Videos: {_gallery.TotalCount} (erwartet {videos})");

        _gallery.FilterState = new GalleryFilterState(GalleryMediaKind.Images);
        await WaitForGalleryAsync("Filter Bilder").ConfigureAwait(true);
        Check(_gallery.TotalCount == images && _gallery.CurrentPage.All(i => !i.IsVideo), $"Filter Bilder: {_gallery.TotalCount} (erwartet {images})");

        _gallery.FilterState = new GalleryFilterState(FavoritesOnly: true);
        await WaitForGalleryAsync("Filter Favoriten").ConfigureAwait(true);
        Check(_gallery.TotalCount == 0 && _gallery.IsEmpty, $"Filter Favoriten (noch keine): {_gallery.EmptyMessage}");

        _gallery.FilterState = GalleryFilterState.None;
        await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);
        Check(_gallery.TotalCount == _mediaCount, "Filter Alle");
    }

    private async Task FolderFilterAsync()
    {
        var leerId = await FolderIdAsync(Path.Combine(_root, "Leer")).ConfigureAwait(true);
        var screenshotsId = await FolderIdAsync(Path.Combine(_root, "2025", "Screenshots")).ConfigureAwait(true);
        var screenshotCount = Directory.EnumerateFiles(Path.Combine(_root, "2025", "Screenshots")).Count(f => _formats.IsSupported(Path.GetExtension(f)));

        _messenger.Send(new FolderSelectedMessage(_libraryId, screenshotsId, true));
        await WaitUntilAsync(
            () => _gallery.FolderId == screenshotsId && !_gallery.IsLoading && _gallery.TotalCount == screenshotCount,
            ShortWait,
            $"Ordnerfilter Screenshots ({screenshotCount} PNG)").ConfigureAwait(true);
        Check(_gallery.HasFolderFilter && _gallery.FolderName == "Screenshots", $"Chip: {_gallery.FolderChipText}");

        _messenger.Send(new FolderSelectedMessage(_libraryId, leerId, false));
        await WaitUntilAsync(
            () => _gallery.FolderId == leerId && _gallery.ShowSubfolders && !_gallery.IsLoading,
            ShortWait,
            "Container-State für „Leer“ (ohne Unterordner)").ConfigureAwait(true);
        var subfolders = _gallery.Subfolders.Select(s => s.Name).ToList();
        Check(subfolders.SequenceEqual(new[] { "Sub A", "Sub B" }), $"Unterordner-Kacheln: {string.Join(", ", subfolders)}");
        Check(_gallery.Subfolders.All(s => s.FileCount == 1), "Dateizähler je Unterordner-Kachel = 1");
        Check(!_gallery.ShowGrid && !_gallery.IsEmpty, "Grid ausgeblendet, kein Leerzustand");
        await SnapshotAsync("03-galerie-container-leer").ConfigureAwait(true);

        await _gallery.OpenSubfolderCommand.ExecuteAsync(_gallery.Subfolders[0]).ConfigureAwait(true);
        await WaitUntilAsync(
            () => !_gallery.ShowSubfolders && !_gallery.IsLoading && _gallery.TotalCount == 1,
            ShortWait,
            "Unterordner-Kachel „Sub A“ geöffnet").ConfigureAwait(true);
        Check(_gallery.FolderName == "Sub A" && !_gallery.IncludeSubfolders, $"Chip: {_gallery.FolderChipText}");

        _messenger.Send(new FolderSelectedMessage(_libraryId, leerId, true));
        await WaitUntilAsync(
            () => _gallery.FolderId == leerId && _gallery.IncludeSubfolders && !_gallery.IsLoading && _gallery.TotalCount == 2,
            ShortWait,
            "„Leer“ rekursiv → 2 Dateien").ConfigureAwait(true);

        await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Ordnerfilter aufgehoben").ConfigureAwait(true);
        Check(_gallery.FolderId is null && !_gallery.HasFolderFilter && _gallery.TotalCount == _mediaCount, "Ordnerfilter aufgehoben → alle Dateien");
    }

    private async Task FavoriteRatingAsync()
    {
        _gallery.ClearSelectionCommand.Execute(null);
        var targets = _gallery.CurrentPage.Take(3).ToList();
        foreach (var item in targets)
        {
            item.IsSelected = true;
        }

        Check(_gallery.SelectedCount == 3 && _gallery.HasSelection && _gallery.SelectionText == "3 ausgewählt", $"Auswahl: {_gallery.SelectionText}");

        await _gallery.FavoriteSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        await _gallery.SetSelectionRatingCommand.ExecuteAsync(new RatingRequest(null, 4)).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true); // FilesChangedMessage(Metadata) → RefreshMetadata

        var ids = string.Join(",", targets.Select(t => t.Id.ToString(CultureInfo.InvariantCulture)));
        var rows = await DbScalarAsync<int>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids}) AND IsFavorite = 1 AND Rating = 4").ConfigureAwait(true);
        Check(rows == 3, "DB: 3 Dateien mit IsFavorite=1 und Rating=4");
        Check(targets.All(t => t.IsFavorite && t.Rating == 4), "Kacheln zeigen Favorit + 4 Sterne");
        Check(_gallery.SelectionRating == 4, "Aktionsleiste: gemeinsame Bewertung 4");
        Check(_gallery.ErrorMessage is null, "keine Fehlermeldung");
        await SnapshotAsync("04-galerie-auswahl-aktionsleiste").ConfigureAwait(true);

        _gallery.ClearSelectionCommand.Execute(null);
        Check(_gallery.SelectedCount == 0 && !_gallery.HasSelection, "Auswahl aufgehoben (Esc)");

        _gallery.FilterState = new GalleryFilterState(FavoritesOnly: true);
        await WaitForGalleryAsync("Filter Favoriten").ConfigureAwait(true);
        Check(_gallery.TotalCount == 3, $"Favoriten-Filter: {_gallery.TotalCount} Treffer");
        _gallery.FilterState = GalleryFilterState.None;
        await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);

        var single = _gallery.CurrentPage.First(i => targets.All(t => t.Id != i.Id));
        await _gallery.ToggleFavoriteCommand.ExecuteAsync(single).ConfigureAwait(true);
        await _gallery.SetItemRatingCommand.ExecuteAsync(new RatingRequest(single, 2)).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);
        var row = await DbRowsAsync("SELECT IsFavorite, Rating FROM Files WHERE Id = $id", ("$id", single.Id)).ConfigureAwait(true);
        Check(row.FirstOrDefault() == "1 | 2", $"DB Einzelkachel „{single.Name}“: IsFavorite | Rating = {row.FirstOrDefault()}");
        Check(single.IsFavorite && single.Rating == 2 && single.HasRating, "Einzelkachel zeigt Herz + 2 Sterne");
    }

    private async Task MoveUndoAsync()
    {
        _gallery.ClearSelectionCommand.Execute(null);
        var unsorted = Path.Combine(_root, "Unsortiert");
        var movers = _gallery.CurrentPage.Where(i => PathEquals(i.FolderPath, unsorted) && !i.IsVideo).Take(2).ToList();
        Check(movers.Count == 2, "2 Bilder aus „Unsortiert“ gewählt");
        var originalFolderId = movers[0].FolderId;
        foreach (var item in movers)
        {
            item.IsSelected = true;
            _movedIds.Add(item.Id);
        }

        var ids = string.Join(",", movers.Select(m => m.Id.ToString(CultureInfo.InvariantCulture)));
        var target = Path.Combine(unsorted, "Smoke-Verschoben");
        _picker.NextFolder = target;

        await _gallery.MoveSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Verschieben beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Verschieben").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Verschieben ohne Fehler ({_gallery.StatusText})");
        foreach (var item in movers)
        {
            Check(File.Exists(Path.Combine(target, item.Name)) && !File.Exists(item.SourcePath), $"Datei verschoben: {item.Name}");
        }

        var targetFolderId = await FolderIdAsync(target).ConfigureAwait(true);
        var movedRows = await DbScalarAsync<int>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids}) AND FolderId = $f", ("$f", targetFolderId)).ConfigureAwait(true);
        Check(movedRows == 2, $"DB: Files.FolderId = {targetFolderId} (neuer Unterordner „Smoke-Verschoben“)");
        Check(_gallery.CurrentPage.Where(i => _movedIds.Contains(i.Id)).All(i => PathEquals(i.FolderPath, target)), "Kacheln zeigen den neuen Ordner");
        Check(_gallery.CanUndo, "Rückgängig verfügbar");

        await _gallery.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Rückgängig beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Rückgängig").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Rückgängig ohne Fehler ({_gallery.StatusText})");
        foreach (var item in movers)
        {
            Check(File.Exists(item.SourcePath) && !File.Exists(Path.Combine(target, item.Name)), $"Datei zurück: {item.Name}");
        }

        var backRows = await DbScalarAsync<int>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids}) AND FolderId = $f", ("$f", originalFolderId)).ConfigureAwait(true);
        Check(backRows == 2, $"DB: Files.FolderId wieder {originalFolderId}");
        Check(!_gallery.CanUndo, "nach Rückgängig nichts mehr rückgängig zu machen");
        Check(_statusBar.RecoveryText is null, "Statusleiste: kein Recovery-Hinweis nach erfolgreichen Vorgängen");

        // Fehlgeschlagener Batch provozieren (Zielname existiert bereits → Item scheitert, Batch Failed)
        // → Fehlerdialog (Fake) + Recovery-Hinweis in der Statusleiste (ADR-0013 Nr. 4).
        _gallery.ClearSelectionCommand.Execute(null);
        var blocked = movers[0];
        var blocker = Path.Combine(target, blocked.Name);
        File.WriteAllBytes(blocker, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        try
        {
            _gallery.CurrentPage.First(i => i.Id == blocked.Id).IsSelected = true;
            _picker.NextFolder = target;
            await _gallery.MoveSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "fehlgeschlagenes Verschieben beendet").ConfigureAwait(true);
            await WaitForGalleryAsync("Reload nach fehlgeschlagenem Verschieben").ConfigureAwait(true);
            Check(File.Exists(blocked.SourcePath), $"Quelle unverändert: {blocked.Name}");
            Check(_gallery.StatusText.Contains("fehlgeschlagen", StringComparison.Ordinal), $"Galerie-Status: {_gallery.StatusText}");
            // Komplett fehlgeschlagener Batch sendet keine FilesChangedMessage → Statusleiste pollt (10 s)
            await WaitUntilAsync(() => _statusBar.RecoveryText is not null, TimeSpan.FromSeconds(25), "Recovery-Hinweis in der Statusleiste").ConfigureAwait(true);
            Check(_statusBar.RecoveryText!.Contains("fehlgeschlagen", StringComparison.Ordinal), $"Statusleiste: {_statusBar.RecoveryText} — {_statusBar.RecoveryDetails}");
        }
        finally
        {
            File.Delete(blocker);
        }

        var batches = await DbRowsAsync("SELECT Id, OperationType, Status FROM OperationBatches ORDER BY Id").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   OperationBatches (Status 2=Applied, 4=Undone, 5=Failed): {Rows}", string.Join("; ", batches));
        Check(batches.Any(b => b.EndsWith("| 5", StringComparison.Ordinal)), "DB: ein OperationBatch mit Status Failed");
    }

    private async Task RecycleAsync()
    {
        _gallery.ClearSelectionCommand.Execute(null);
        var unsorted = Path.Combine(_root, "Unsortiert");
        var victim = _gallery.CurrentPage.First(i => PathEquals(i.FolderPath, unsorted) && !i.IsVideo && !_movedIds.Contains(i.Id));
        victim.IsSelected = true;
        var thumbPath = await DbScalarAsync<string>("SELECT CachePath FROM Thumbnails WHERE FileId = $id", ("$id", victim.Id)).ConfigureAwait(true);
        var token = Path.GetFileNameWithoutExtension(victim.Name);
        var ftsBefore = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts WHERE FilesFts MATCH $t", ("$t", "\"" + token + "\"")).ConfigureAwait(true);

        await _gallery.RecycleSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Papierkorb beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Papierkorb").ConfigureAwait(true);

        Check(_gallery.ErrorMessage is null, $"Papierkorb ohne Fehler ({_gallery.StatusText})");
        Check(!File.Exists(victim.SourcePath), $"Datei im Papierkorb: {victim.Name}");
        var fileRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Id = $id", ("$id", victim.Id)).ConfigureAwait(true);
        var thumbRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE FileId = $id", ("$id", victim.Id)).ConfigureAwait(true);
        var ftsAfter = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts WHERE FilesFts MATCH $t", ("$t", "\"" + token + "\"")).ConfigureAwait(true);
        Check(fileRows == 0, "DB: Files-Zeile entfernt");
        Check(thumbRows == 0, "DB: Thumbnails-Zeile entfernt");
        Check(ftsAfter == ftsBefore - 1, $"FTS-Eintrag entfernt ({ftsBefore} → {ftsAfter})");
        Check(string.IsNullOrEmpty(thumbPath) || !File.Exists(thumbPath), "Thumbnail-Cache-Datei entfernt");
        _mediaCount--;
        Check(_gallery.TotalCount == _mediaCount && _gallery.CurrentPage.All(i => i.Id != victim.Id), $"Galerie: {_gallery.TotalCount} Dateien, Kachel entfernt");
    }

    private async Task LightboxAsync()
    {
        var image = _gallery.CurrentPage.First(i => !i.IsVideo);
        var startIndex = _gallery.CurrentPage.IndexOf(image);
        _gallery.OpenInLightboxCommand.Execute(image);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().FirstOrDefault();
        Check(window is not null, "Lightbox-Fenster vorhanden");
        var vm = window!.ViewModel;
        await WaitUntilAsync(() => vm.IsImage && vm.ImageSource is not null && !vm.IsLoading, ShortWait, "Bild dekodiert").ConfigureAwait(true);
        Check(vm.CurrentItem?.FileId == image.Id && vm.CurrentIndex == startIndex, $"Lightbox zeigt „{vm.Title}“ ({vm.CounterText}, {vm.Subtitle})");
        Check(ReferenceEquals(window.Owner, Application.Current.MainWindow), "Lightbox-Owner = Hauptfenster");
        Check(vm.ErrorMessage is null, "kein Lightbox-Fehler");
        vm.ToggleInfoCommand.Execute(null);
        Check(vm.IsInfoVisible && vm.FileInfoRows.Count >= 5, $"Info-Panel: {vm.FileInfoRows.Count} Datei-Zeilen");
        await SnapshotAsync("05-lightbox-bild", window).ConfigureAwait(true);

        if (vm.CanGoNext)
        {
            vm.NextCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentIndex == startIndex + 1 && !vm.IsLoading, ShortWait, "Nächster Eintrag").ConfigureAwait(true);
            vm.PreviousCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentIndex == startIndex && !vm.IsLoading, ShortWait, "Vorheriger Eintrag").ConfigureAwait(true);
            Check(vm.CurrentItem?.FileId == image.Id, "Navigation ← → zurück beim Startbild");
        }

        vm.LastCommand.Execute(null);
        await WaitUntilAsync(() => vm.CurrentIndex == vm.Count - 1 && !vm.IsLoading, ShortWait, "Ende").ConfigureAwait(true);
        vm.FirstCommand.Execute(null);
        await WaitUntilAsync(() => vm.CurrentIndex == 0 && !vm.IsLoading, ShortWait, "Anfang").ConfigureAwait(true);

        foreach (var extension in new[] { ".mp4", ".mov" })
        {
            var video = _gallery.CurrentPage.FirstOrDefault(i => i.IsVideo && i.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
            if (video is null)
            {
                continue;
            }

            _gallery.OpenInLightboxCommand.Execute(video);
            Check(ReferenceEquals(Application.Current.Windows.OfType<LightboxWindow>().FirstOrDefault(), window), "Lightbox-Fenster wiederverwendet");
            await WaitUntilAsync(() => vm.IsVideo && vm.CurrentItem?.FileId == video.Id && (vm.IsPlaying || vm.ErrorMessage is not null), TimeSpan.FromSeconds(30), $"Video {extension} spielt").ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);
            Check(vm.ErrorMessage is null && vm.Player is not null && vm.IsPlaying, $"Video {extension}: „{vm.Title}“ {vm.TimeText}");
            Check(vm.MediaInfoRows.Any(r => r.Label is "Dauer" or "Auflösung"), $"Video-Infos: {string.Join(", ", vm.MediaInfoRows.Select(r => r.Label + "=" + r.Value))}");
            if (extension == ".mp4")
            {
                await SnapshotAsync("06-lightbox-video", window).ConfigureAwait(true);
            }

            vm.PlayPauseCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsPlaying, ShortWait, "Pause").ConfigureAwait(true);
        }

        vm.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        Check(!Application.Current.Windows.OfType<LightboxWindow>().Any(), "Lightbox-Fenster zerstört");
        await TileDoubleClickTargetAsync().ConfigureAwait(true); // 0.2.42
    }

    private async Task FolderPageAsync()
    {
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Folders && !_folders.IsLoading && _folders.Roots.Count > 0,
            ShortWait,
            "Ordnerbaum geladen").ConfigureAwait(true);
        Check(_folders.HasLibrary && _folders.HintText is null, $"Ordnerbaum für „{_folders.LibraryName}“ ohne Hinweis");

        var root = _folders.Roots[0];
        root.IsExpanded = true;
        var childNames = string.Join(", ", root.Children.Select(c => c.Name));
        var year = root.Children.FirstOrDefault(c => c.Name == "2024");
        Check(year is not null, $"Wurzel „{root.Name}“ ({root.TotalFileCount} Dateien) hat Kinder: {childNames}");
        Check(root.TotalFileCount == _mediaCount, $"Wurzel TotalFileCount = {root.TotalFileCount} (erwartet {_mediaCount})");

        year!.IsExpanded = true;
        var familie = year.Children.First(c => c.Name == "Familie");
        var familieCount = Directory.EnumerateFiles(familie.Path).Count(f => _formats.IsSupported(Path.GetExtension(f)));
        familie.IsSelected = true;
        await WaitUntilAsync(
            () => _gallery.FolderId == familie.Id && !_gallery.IsLoading && _gallery.TotalCount == familieCount,
            ShortWait,
            $"Baum-Auswahl „Familie“ → {familieCount} Dateien").ConfigureAwait(true);
        Check(_folders.SelectionText == familie.Path, $"SelectionText = {_folders.SelectionText}");
        Check(familie.FileCount == familieCount && familie.TotalFileCount == familieCount, $"Zähler im Baum: {familie.FileCount}/{familie.TotalFileCount}");
        await SnapshotAsync("07-ordnerbaum").ConfigureAwait(true);

        _folders.IncludeSubfolders = false;
        await WaitUntilAsync(() => _gallery.FolderId == familie.Id && !_gallery.IncludeSubfolders && !_gallery.IsLoading, ShortWait, "ohne Unterordner").ConfigureAwait(true);
        _folders.IncludeSubfolders = true;
        await WaitUntilAsync(() => _gallery.IncludeSubfolders && !_gallery.IsLoading, ShortWait, "mit Unterordnern").ConfigureAwait(true);

        _folders.SelectWholeLibraryCommand.Execute(null);
        await WaitUntilAsync(
            () => _gallery.FolderId is null && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount,
            ShortWait,
            "Gesamte Bibliothek").ConfigureAwait(true);
        Check(_folders.SelectionText == "Gesamte Bibliothek", "Baum: gesamte Bibliothek");
    }

    private async Task SettingsAsync()
    {
        // Theme auf dem Reiter „Darstellung“ (ADR-0042); Cache/DB/Formate stehen unter „Speicher & Wartung“ (gleiches ViewModel)
        await OpenSettingsSectionAsync(NavigationTags.SettingsAppearance, "Einstellungen › Darstellung").ConfigureAwait(true);
        // Seit 0.2.44 startet die App ohne Bibliothek in den Einstellungen — deren Werte (Cache damals leer) stehen schon da;
        // das Laden beim Anzeigen abwarten und einmal frisch laden, statt den alten Stand zu prüfen
        await Task.Delay(200).ConfigureAwait(true);
        await WaitUntilAsync(() => !_settings.LoadCommand.IsRunning, ShortWait, "Einstellungen laden").ConfigureAwait(true);
        await _settings.LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Settings && _settings.ThumbnailCacheText != "wird berechnet…" && _settings.AppVersionText.Length > 0,
            ShortWait,
            "Einstellungen geladen").ConfigureAwait(true);
        Check(_settings.ThumbnailCacheText.Contains("Dateien", StringComparison.Ordinal), $"Cache: {_settings.ThumbnailCacheText}");
        // Erwartet die Assembly-Version (aus CHANGELOG.md, siehe Wpf.csproj) — nicht fest „0.2.0“, sonst schlägt der
        // Schritt bei jeder neuen Version fehl („0.2.20“ enthält „0.2.0“ nicht).
        var assemblyVersion = typeof(SmokeScenario).Assembly.GetName().Version;
        var expectedVersion = assemblyVersion is null ? "?" : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        Check(_settings.AppVersionText.StartsWith($"Lumora {expectedVersion}", StringComparison.Ordinal), $"Version: {_settings.AppVersionText}");
        Check(_settings.IsFfmpegAvailable, $"FFmpeg: {_settings.FfmpegText}");
        Check(_settings.DatabasePath == _db.DatabasePath && _settings.DatabaseSizeText.Length > 0, $"DB: {_settings.DatabasePath} ({_settings.DatabaseSizeText})");
        Check(_settings.ImageFormatsText.Contains(".jpg", StringComparison.Ordinal) && _settings.VideoFormatsText.Contains(".mp4", StringComparison.Ordinal), "Formate angezeigt");

        _settings.IsThemeLight = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("08-einstellungen-hell").ConfigureAwait(true);

        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("09-einstellungen-dunkel").ConfigureAwait(true);

        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        var stored = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = 'ui.theme'").ConfigureAwait(true);
        Check(stored == "System", $"Settings-Tabelle ui.theme = {stored}");
        Check(_settings.ErrorMessage is null, "Einstellungen ohne Fehler");
    }

    /// <summary>
    /// Zweiter Scan einer bestehenden Bibliothek ist „Aktualisieren“ (Incremental, stabile FileIds): (1) ohne Änderung
    /// auf der Platte, (2) mit einer neuen und einer vorübergehend entfernten Datei (Added + MarkedMissing, „fehlt“-Badge,
    /// Favoriten bleiben), (3) Datei wieder da (Reappeared). Danach „Neu aufbauen“ (ClearAndRescan über
    /// <see cref="LibraryViewModel.RunSmokeRebuildAsync"/>): neue FileIds, Favoriten/Bewertungen übernommen, Thumbnails neu.
    /// </summary>
    private async Task RescanAsync()
    {
        Check(_mediaCount >= MinMediaFilesForRescan, $"Testbibliothek hat {_mediaCount} Mediendateien (mindestens {MinMediaFilesForRescan}, sonst greift der Mass-Missing-Guard)");
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        var maxIdBefore = await DbScalarAsync<int>("SELECT COALESCE(MAX(Id), 0) FROM Files").ConfigureAwait(true);
        var favoritesBefore = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE IsFavorite = 1").ConfigureAwait(true);

        // (1) Aktualisieren ohne Änderung: Incremental, nichts neu/geändert/fehlend, Ids unverändert
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_library.ErrorMessage is null && _library.IsScanSummaryOpen && !_library.ScanSummaryIsWarning, $"Aktualisieren ohne Änderung: {_library.ScanSummary}");
        Check(
            _lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, HasChanges: false },
            $"Incremental ohne Änderungen (Added={_lastScan?.Added}, Modified={_lastScan?.Modified}, Missing={_lastScan?.MarkedMissing}, Ordner={_lastScan?.FoldersAdded})");
        var maxIdAfterNoop = await DbScalarAsync<int>("SELECT COALESCE(MAX(Id), 0) FROM Files").ConfigureAwait(true);
        Check(maxIdAfterNoop == maxIdBefore, $"FileIds stabil (MAX(Id) = {maxIdAfterNoop})");

        // (2) Neue Datei + eine Datei vorübergehend außerhalb der Bibliothek geparkt → Added 1, MarkedMissing 1
        var unsorted = Path.Combine(_root, "Unsortiert");
        var unsortedId = await FolderIdAsync(unsorted).ConfigureAwait(true);
        var jpgs = Directory.EnumerateFiles(unsorted, "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Check(jpgs.Count >= 2, $"„Unsortiert“ hat {jpgs.Count} JPGs (mindestens 2 für Kopie + Parken nötig)");
        var added = Path.Combine(unsorted, AddedFileName);
        File.Copy(jpgs[0], added, overwrite: true);
        var parkedSource = jpgs[^1];
        var parkedId = await DbScalarAsync<int>(
            "SELECT Id FROM Files WHERE FolderId = $f AND Name = $n", ("$f", unsortedId), ("$n", Path.GetFileName(parkedSource))).ConfigureAwait(true);
        Directory.CreateDirectory(ParkingDir);
        var parked = Path.Combine(ParkingDir, Path.GetFileName(parkedSource));
        File.Move(parkedSource, parked, overwrite: true);
        try
        {
            _lastScan = null;
            await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
            Check(_library.ErrorMessage is null && !_library.ScanSummaryIsWarning, $"Aktualisieren mit Änderungen: {_library.ScanSummary}");
            Check(
                _lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 1, MarkedMissing: 1, Reappeared: 0 },
                $"Incremental: Added={_lastScan?.Added}, MarkedMissing={_lastScan?.MarkedMissing}, Reappeared={_lastScan?.Reappeared}");
            _mediaCount++;
            await WaitUntilAsync(
                () => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount
                      && _gallery.CurrentPage.Any(i => i.Id == parkedId && i.Missing),
                ShortWait,
                $"Galerie nach Aktualisieren: {_mediaCount} Dateien inkl. fehlender").ConfigureAwait(true);
            var addedItem = _gallery.CurrentPage.FirstOrDefault(i => i.Name == "smoke-neu.jpg");
            Check(addedItem is not null && addedItem.Id > maxIdBefore, $"neue Datei mit neuer Id ({addedItem?.Id} > {maxIdBefore})");
            Check(_gallery.CurrentPage.Any(i => i.Id == parkedId && i.Missing), $"Kachel „{Path.GetFileName(parkedSource)}“ zeigt „fehlt“ (Id {parkedId} bleibt)");
            var favoritesAfterUpdate = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE IsFavorite = 1").ConfigureAwait(true);
            Check(favoritesAfterUpdate == favoritesBefore, $"Favoriten bleiben erhalten ({favoritesAfterUpdate})");
            await SnapshotAsync("10-galerie-fehlt-badge").ConfigureAwait(true);
        }
        finally
        {
            File.Move(parked, parkedSource, overwrite: true);
            try
            {
                Directory.Delete(ParkingDir); // nur, wenn leer — nie rekursiv
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Rauchtest   Park-Ordner nicht entfernt: {Path}", ParkingDir);
            }
        }

        // (3) Datei wieder da → Reappeared 1, Badge weg
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(
            _lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Reappeared: 1, Added: 0, MarkedMissing: 0 },
            $"Incremental: Reappeared={_lastScan?.Reappeared}, Added={_lastScan?.Added}, MarkedMissing={_lastScan?.MarkedMissing}");
        await WaitUntilAsync(
            () => !_gallery.IsLoading && _gallery.CurrentPage.Any(i => i.Id == parkedId && !i.Missing),
            ShortWait,
            "„fehlt“-Badge nach Rückkehr der Datei weg").ConfigureAwait(true);

        // (4) Neu aufbauen: ClearAndRescan vergibt neue FileIds (sqlite_sequence) — erst wenn die Galerie die neuen Ids zeigt, ist sie umgestellt.
        //     Favoriten und Bewertungen hängen am Pfad und werden übernommen (ADR-0023).
        const string userDataSql = "SELECT d.Path, f.Name, f.IsFavorite, f.Rating FROM Files f JOIN Folders d ON d.Id = f.FolderId " +
                                   "WHERE f.LibraryId = $lib AND (f.IsFavorite = 1 OR f.Rating IS NOT NULL) ORDER BY d.Path, f.Name";
        var userDataBefore = await DbRowsAsync(userDataSql, ("$lib", _libraryId)).ConfigureAwait(true);
        Check(userDataBefore.Count > 0, $"vor ClearAndRescan: {userDataBefore.Count} Dateien mit Favorit/Bewertung");
        var previousMaxId = await DbScalarAsync<int>("SELECT COALESCE(MAX(Id), 0) FROM Files").ConfigureAwait(true);
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        _lastScan = null;
        await _library.RunSmokeRebuildAsync(_root).ConfigureAwait(true);
        Check(_library.ErrorMessage is null && _library.IsScanSummaryOpen && !_library.ScanSummaryIsWarning, $"ClearAndRescan: {_library.ScanSummary}");
        Check(_lastScan is { Mode: ScanMode.ClearAndRescan, Outcome: ScanOutcome.Completed }, $"ClearAndRescan-Ergebnis: Modus {_lastScan?.Mode}, {_lastScan?.Outcome}");

        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount
                  && _gallery.CurrentPage.Count == _mediaCount && _gallery.CurrentPage.All(i => i.Id > previousMaxId),
            ShortWait,
            $"Galerie nach ClearAndRescan: {_mediaCount} Dateien mit neuen Ids (> {previousMaxId})").ConfigureAwait(true);
        var files = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        var fts = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts").ConfigureAwait(true);
        Check(files == _mediaCount && fts == files, $"DB nach ClearAndRescan: {files} Files, {fts} FTS-Zeilen");
        var userDataAfter = await DbRowsAsync(userDataSql, ("$lib", _libraryId)).ConfigureAwait(true);
        Check(userDataAfter.SequenceEqual(userDataBefore), $"ClearAndRescan hat Favoriten/Bewertungen übernommen ({userDataAfter.Count}/{userDataBefore.Count} Dateien, gleiche Werte)");

        await WaitUntilAsync(
            () => _gallery.CurrentPage.Count > 0 && _gallery.CurrentPage.All(i => i.Thumbnail is not null),
            LongWait,
            "Thumbnails nach ClearAndRescan (neue FileIds)").ConfigureAwait(true);
        var generated = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE Status = 2").ConfigureAwait(true);
        Check(generated == _mediaCount, $"Thumbnails neu erzeugt: {generated}/{_mediaCount}");
    }

    /// <summary>
    /// Ordnerüberwachung (Services/Watch): Ruhezeit 1 s, einschalten, Datei nach „Unsortiert“ kopieren → Statusleiste
    /// „Änderungen erkannt — Aktualisierung in n s“ → automatischer Incremental-Scan (Added 1) → Galerie zeigt die Datei;
    /// danach löschen → automatischer Scan (MarkedMissing 1) → „fehlt“-Badge. Einstellungen bleiben gespeichert (an, 1 s).
    /// </summary>
    private async Task WatchAsync()
    {
        await _watch.SetQuietPeriodAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        await _watch.SetEnabledAsync(true).ConfigureAwait(true);
        await WaitUntilAsync(() => _watch.Status is { State: WatchState.Idle } s && s.LibraryId == _libraryId, ShortWait, "Überwachung der aktiven Bibliothek aktiv").ConfigureAwait(true);
        Check(_watch.IsEnabled && _watch.QuietPeriod == TimeSpan.FromSeconds(1), "Einstellungen gespeichert: an, Ruhezeit 1 s");

        var unsorted = Path.Combine(_root, "Unsortiert");
        var source = Directory.EnumerateFiles(unsorted, "*.jpg")
            .Where(f => !PathEquals(Path.GetFileName(f), WatchedFileName) && !PathEquals(Path.GetFileName(f), AddedFileName))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .First();
        var target = Path.Combine(unsorted, WatchedFileName);

        // (1) Kopie → Countdown → automatischer Diff-Scan → Galerie
        var sawCountdown = false;
        var sawResult = false;
        _lastScan = null;
        File.Copy(source, target, overwrite: true);
        await WaitUntilAsync(
            () =>
            {
                sawCountdown |= _statusBar.WatchText?.StartsWith("Änderungen erkannt", StringComparison.Ordinal) == true;
                return _lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 1 };
            },
            ShortWait,
            "automatischer Incremental-Scan nach der Kopie").ConfigureAwait(true);
        Check(sawCountdown, "Statusleiste zeigte „Änderungen erkannt — Aktualisierung in n s“");
        _mediaCount++;
        await WaitUntilAsync(
            () =>
            {
                sawResult |= _statusBar.WatchText?.StartsWith("Automatisch aktualisiert", StringComparison.Ordinal) == true;
                return !_gallery.IsLoading && _gallery.TotalCount == _mediaCount
                       && _gallery.CurrentPage.Any(i => i.Name == WatchedFileName && !i.Missing);
            },
            ShortWait,
            $"Galerie zeigt {WatchedFileName} ({_mediaCount} Dateien)").ConfigureAwait(true);
        await WaitUntilAsync(
            () =>
            {
                sawResult |= _statusBar.WatchText?.StartsWith("Automatisch aktualisiert", StringComparison.Ordinal) == true;
                return _watch.Status.State == WatchState.Idle && sawResult;
            },
            ShortWait,
            "Statusleiste meldet den abgeschlossenen automatischen Scan").ConfigureAwait(true);
        await SnapshotAsync("11-galerie-ordnerueberwachung").ConfigureAwait(true);

        // (2) Löschen → automatischer Scan → „fehlt“-Badge (Zeile bleibt, Missing statt Löschen)
        var watchedId = _gallery.CurrentPage.First(i => i.Name == WatchedFileName).Id;
        _lastScan = null;
        File.Delete(target);
        await WaitUntilAsync(
            () => _lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, MarkedMissing: 1, Added: 0 },
            ShortWait,
            "automatischer Incremental-Scan nach dem Löschen").ConfigureAwait(true);
        await WaitUntilAsync(
            () => !_gallery.IsLoading && _gallery.CurrentPage.Any(i => i.Id == watchedId && i.Missing),
            ShortWait,
            "„fehlt“-Badge nach dem automatischen Scan").ConfigureAwait(true);
        Check(_watch.Status.State is WatchState.Idle or WatchState.ChangesPending, $"Überwachung läuft weiter ({_watch.Status.State})");
    }

    // ---- Archive (ADR-0022) --------------------------------------------------------------------

    /// <summary>Das ZIP, das der Archiv-Schritt in „Unsortiert“ anlegt; sein Zielordner heißt wie das Archiv ohne Endung.</summary>
    private const string ArchiveFileName = "smoke-archiv.zip";

    private void RemoveArchiveLeftovers(string unsorted)
    {
        var zip = Path.Combine(unsorted, ArchiveFileName);
        if (File.Exists(zip))
        {
            File.Delete(zip);
            _logger.LogInformation("Rauchtest   Rückstand entfernt: {Path}", zip);
        }

        var folder = Path.Combine(unsorted, Path.GetFileNameWithoutExtension(ArchiveFileName));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
            _logger.LogInformation("Rauchtest   Rückstand entfernt: {Path}", folder);
        }
    }

    /// <summary>
    /// ZIP (zwei JPGs, eine Textdatei, ein verschachteltes ZIP mit einem JPG) in „Unsortiert“ anlegen → Aktualisieren nimmt es als
    /// Archiv auf (Kachel ohne Thumbnail, Badge ZIP, Filter „Archive“) → Lightbox zeigt die Inhaltsliste → Entpacken ohne
    /// Dialog mit Papierkorb-Option → Dateien liegen im Unterordner, das Archiv ist weg, der Incremental-Scan hat die
    /// Fotos in den Katalog gebracht.
    /// </summary>
    private async Task ArchiveAsync()
    {
        var unsorted = Path.Combine(_root, "Unsortiert");
        var jpgs = Directory.EnumerateFiles(unsorted, "*.jpg")
            .Where(f => !Path.GetFileName(f).Equals(AddedFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Check(jpgs.Count >= 2, $"„Unsortiert“ hat {jpgs.Count} JPGs für das Archiv (mindestens 2)");
        var zipPath = Path.Combine(unsorted, ArchiveFileName);
        byte[] nested;
        using (var ms = new MemoryStream())
        {
            using (var inner = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                inner.CreateEntryFromFile(jpgs[0], "archiv-c.jpg");
            }

            nested = ms.ToArray();
        }

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(jpgs[0], "archiv-a.jpg");
            zip.CreateEntryFromFile(jpgs[1], "sub/archiv-b.jpg");
            using (var text = new StreamWriter(zip.CreateEntry("liesmich.txt").Open()))
            {
                text.Write("Rauchtest");
            }

            using var innerStream = zip.CreateEntry("innen.zip").Open();
            innerStream.Write(nested);
        }

        // (1) Aktualisieren nimmt das Archiv in den Katalog auf
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 1 }, $"Incremental nimmt das ZIP auf (Added={_lastScan?.Added})");
        _mediaCount++;
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount
                  && _gallery.CurrentPage.Any(i => i.Name == ArchiveFileName),
            ShortWait,
            "Galerie zeigt die Archiv-Kachel").ConfigureAwait(true);
        var tile = _gallery.CurrentPage.First(i => i.Name == ArchiveFileName);
        Check(tile.IsArchive && tile.MediaType == MediaType.Archive && tile.ArchiveBadge == "ZIP" && !tile.IsVideo, $"Kachel „{tile.Name}“ ist ein Archiv (Badge {tile.ArchiveBadge}, {tile.SizeDisplay})");
        var mediaType = await DbScalarAsync<int>("SELECT MediaType FROM Files WHERE Id = $id", ("$id", tile.Id)).ConfigureAwait(true);
        Check(mediaType == (int)MediaType.Archive, $"DB: Files.MediaType = {mediaType} (Archive)");
        await Task.Delay(800).ConfigureAwait(true);
        var thumbRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE FileId = $id", ("$id", tile.Id)).ConfigureAwait(true);
        Check(tile.Thumbnail is null && thumbRows == 0, "Archiv ohne Thumbnail (keine Thumbnails-Zeile, Kachel zeigt Icon)");

        // (2) Filter „Archive“
        _gallery.FilterState = new GalleryFilterState(GalleryMediaKind.Archives);
        await WaitForGalleryAsync("Filter Archive").ConfigureAwait(true);
        Check(_gallery.TotalCount == 1 && _gallery.CurrentPage.Count == 1 && _gallery.CurrentPage[0].Id == tile.Id, $"Filter „Archive“: {_gallery.TotalCount} Treffer");
        await SnapshotAsync("11-galerie-archive").ConfigureAwait(true);
        _gallery.FilterState = GalleryFilterState.None;
        await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);
        tile = _gallery.CurrentPage.First(i => i.Id == tile.Id);

        // (3) Lightbox: Inhaltsansicht statt Bild
        _gallery.OpenInLightboxCommand.Execute(tile);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var vm = window.ViewModel;
        await WaitUntilAsync(() => vm.IsArchive && !vm.IsLoading, ShortWait, "Archiv-Inhalt gelesen").ConfigureAwait(true);
        Check(vm.ArchiveError is null && !vm.IsImage && !vm.IsVideo, $"Inhaltsansicht ohne Fehler ({vm.ArchiveSummary})");
        Check(vm.ArchiveEntries.Count == 4 && vm.ArchiveEntries.Count(e => e.IsMedia) == 2 && vm.ArchiveEntries.Count(e => e.IsArchive) == 1,
            $"Inhaltsliste: {vm.ArchiveEntries.Count} Einträge ({string.Join(", ", vm.ArchiveEntries.Select(e => e.Path))})");
        Check(vm.FileInfoRows.Any(r => r.Label == "Typ" && r.Value == "Archiv"), "Info-Panel: Typ = Archiv");
        await SnapshotAsync("12-lightbox-archiv", window).ConfigureAwait(true);
        vm.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (4) Entpacken (ohne Dialog, mit Papierkorb) → Dateien im Unterordner, Archiv weg, Katalog aktualisiert
        _gallery.ClearSelectionCommand.Execute(null);
        tile.IsSelected = true;
        Check(_gallery.SelectedArchiveCount == 1 && _gallery.CanExtractSelection, "Aktionsleiste bietet „Entpacken…“ für die Auswahl");
        var results = await _gallery.ExtractArchivesHeadlessAsync([tile.Id], new ArchiveExtractOptions { RecycleArchiveAfterExtract = true }, _archives).ConfigureAwait(true);
        var result = results.Single();
        Check(result.Errors.Count == 0 && !result.Canceled, $"Entpacken ohne Fehler ({string.Join(" | ", result.Errors.Select(e => e.Message))})");
        Check(result.FilesExtracted == 4 && result.NestedArchivesExtracted == 1 && result.Rounds == 2, $"Entpackt: {result.FilesExtracted} Dateien, {result.NestedArchivesExtracted} verschachtelt, {result.Rounds} Runden");
        var target = Path.Combine(unsorted, Path.GetFileNameWithoutExtension(ArchiveFileName));
        Check(result.TargetFolder.Equals(target, StringComparison.OrdinalIgnoreCase), $"Zielordner = {result.TargetFolder}");
        Check(File.Exists(Path.Combine(target, "archiv-a.jpg")) && File.Exists(Path.Combine(target, "sub", "archiv-b.jpg")) && File.Exists(Path.Combine(target, "innen", "archiv-c.jpg")), "Dateien liegen im Zielordner (inkl. verschachteltem Archiv)");
        Check(!File.Exists(Path.Combine(target, "liesmich.txt")) && !File.Exists(Path.Combine(target, "innen.zip")), "Nur Medien entpackt, inneres ZIP entfernt");
        Check(result.ArchiveRecycled && !File.Exists(zipPath), "Archiv im Papierkorb");
        Check(result.Scan is { Mode: ScanMode.Incremental, Added: 3 }, $"Incremental-Scan nach dem Entpacken: Added={result.Scan?.Added}");
        _mediaCount += 3 - 1;
        await WaitForGalleryAsync("Reload nach Entpacken").ConfigureAwait(true);
        await WaitUntilAsync(
            () => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount && _gallery.CurrentPage.All(i => i.Id != tile.Id)
                  && _gallery.CurrentPage.Any(i => i.Name == "archiv-c.jpg"),
            ShortWait,
            $"Galerie: {_mediaCount} Dateien, Archiv-Kachel weg, entpackte Fotos da").ConfigureAwait(true);
        var archiveRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Id = $id", ("$id", tile.Id)).ConfigureAwait(true);
        var extractedRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Name IN ('archiv-a.jpg', 'archiv-b.jpg', 'archiv-c.jpg')").ConfigureAwait(true);
        Check(archiveRows == 0 && extractedRows == 3, $"DB: Archiv-Zeile entfernt, {extractedRows} entpackte Dateien im Katalog");
        Check(_gallery.ErrorMessage is null, $"Galerie ohne Fehler ({_gallery.StatusText})");
        await SnapshotAsync("13-galerie-nach-entpacken").ConfigureAwait(true);
    }

    /// Tags und Listenansicht (0.2.6, ADR-0021): Tags anlegen, über das Tag-Menü (3 Dateien) und den Strg+T-Dialog
    /// (Fake, 2 Dateien) zuweisen, Chips/DB prüfen, Tag-Filter ODER/UND/ohne Tag, Suche nach Tag-Namen, Umschalten auf
    /// die Listenansicht (Setting, Spalte ausblenden, Sortierung, Lightbox mit Tags), Verwaltungsseite, Umbenennen und
    /// Löschen samt Aufräumen der Zuweisungen. Läuft nach dem Neuaufbau (neue FileIds) und hinterlässt keine Tags.
    /// </summary>
    private async Task TagsAndListViewAsync()
    {
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie aktiv").ConfigureAwait(true);
        if (_gallery.FilterState.IsActive)
        {
            _gallery.FilterState = GalleryFilterState.None;
            await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);
        }

        var baseline = _gallery.TotalCount;
        Check(baseline >= 4 && _gallery.CurrentPage.Count >= 3, $"Ausgangslage: {baseline} Dateien");

        var urlaub = await _tags.CreateAsync("Smoketag Urlaub").ConfigureAwait(true);
        var familie = await _tags.CreateAsync("Smoketag Familie").ConfigureAwait(true);
        await WaitUntilAsync(
            () => _gallery.TagMenuEntries.Any(e => e.Tag.Id == urlaub.Id) && _gallery.TagMenuEntries.Any(e => e.Tag.Id == familie.Id),
            ShortWait,
            "Neue Tags im Tag-Menü der Galerie").ConfigureAwait(true);

        // Tag-Menü (Aktionsleiste/Kontextmenü): „Urlaub“ für 3 Dateien
        _gallery.ClearSelectionCommand.Execute(null);
        var targets = _gallery.CurrentPage.Take(3).ToList();
        foreach (var item in targets)
        {
            item.IsSelected = true;
        }

        var entry = _gallery.TagMenuEntries.First(e => e.Tag.Id == urlaub.Id);
        Check(entry.State == false, "Menüeintrag „Urlaub“ vor der Zuweisung ohne Häkchen");
        await _gallery.ToggleSelectionTagCommand.ExecuteAsync(entry).ConfigureAwait(true);
        // Aktuellen Eintrag lesen: seit 0.2.29 kommen die TagsChangedMessages der beiden CreateAsync vom Thread-Pool, die
        // zweite kann das Menü nach dem Abgreifen von „entry“ noch einmal neu aufbauen (neue Instanzen).
        Check(_gallery.TagMenuEntries.First(e => e.Tag.Id == urlaub.Id).State == true, "Menüeintrag „Urlaub“ nach der Zuweisung mit Häkchen");
        var urlaubRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileTags WHERE TagId = $t", ("$t", urlaub.Id)).ConfigureAwait(true);
        Check(urlaubRows == 3, $"DB: {urlaubRows} Zuweisungen „Urlaub“ (erwartet 3)");
        Check(targets.All(t => t.HasTag(urlaub.Id) && t.HasTags), "Kacheln zeigen den Tag-Punkt „Urlaub“");

        // Strg+T-Dialog (Fake): „Familie“ für die ersten beiden
        if (_tagPicker is not null)
        {
            _gallery.ClearSelectionCommand.Execute(null);
            targets[0].IsSelected = true;
            targets[1].IsSelected = true;
            _tagPicker.NextResult = new TagPickResult(new[] { familie.Id }, Array.Empty<int>());
            await _gallery.EditSelectionTagsCommand.ExecuteAsync(null).ConfigureAwait(true);
            var request = _tagPicker.LastRequest;
            Check(
                request is { SelectionCount: 2 } && request.CountsInSelection.GetValueOrDefault(urlaub.Id) == 2 && request.Tags.Count >= 2,
                "Tag-Auswahl (Strg+T): 2 Dateien, „Urlaub“ bei beiden gesetzt");
        }
        else
        {
            await _tags.AssignAsync(new[] { targets[0].Id, targets[1].Id }, new[] { familie.Id }).ConfigureAwait(true);
        }

        await WaitUntilAsync(() => targets[0].HasTag(familie.Id) && targets[1].HasTag(familie.Id), ShortWait, "Chips „Familie“ nach Strg+T").ConfigureAwait(true);
        var familieRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileTags WHERE TagId = $t", ("$t", familie.Id)).ConfigureAwait(true);
        Check(familieRows == 2, $"DB: {familieRows} Zuweisungen „Familie“ (erwartet 2)");
        Check(targets[0].Tags.Select(t => t.Id).SequenceEqual(new[] { urlaub.Id, familie.Id }), $"Chip-Reihenfolge nach SortOrder: {targets[0].TagsText}");
        await SnapshotAsync("10-galerie-tags").ConfigureAwait(true);
        _gallery.ClearSelectionCommand.Execute(null);

        // Tag-Filter: ODER / UND / ohne Tag
        var filterUrlaub = _gallery.TagFilterEntries.First(e => e.Tag.Id == urlaub.Id);
        var filterFamilie = _gallery.TagFilterEntries.First(e => e.Tag.Id == familie.Id);
        filterUrlaub.IsSelected = true;
        await _gallery.ApplyTagFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Tag-Filter Urlaub").ConfigureAwait(true);
        Check(_gallery.TotalCount == 3 && _gallery.HasTagFilter, $"Tag-Filter „Urlaub“: {_gallery.TotalCount} Treffer (erwartet 3), Knopf „{_gallery.TagFilterText}“");

        filterFamilie.IsSelected = true;
        _gallery.TagFilterMode = GalleryTagMode.All;
        await WaitForGalleryAsync("Tag-Filter UND").ConfigureAwait(true);
        Check(_gallery.TotalCount == 2, $"Tag-Filter „Urlaub UND Familie“: {_gallery.TotalCount} Treffer (erwartet 2), „{_gallery.TagFilterText}“");

        _gallery.TagFilterMode = GalleryTagMode.Any;
        await WaitForGalleryAsync("Tag-Filter ODER").ConfigureAwait(true);
        Check(_gallery.TotalCount == 3, $"Tag-Filter „Urlaub ODER Familie“: {_gallery.TotalCount} Treffer (erwartet 3)");

        _gallery.TagFilterMode = GalleryTagMode.None;
        await WaitForGalleryAsync("Tag-Filter ohne Tag").ConfigureAwait(true);
        Check(_gallery.TotalCount == baseline - 3, $"Tag-Filter „ohne Tag“: {_gallery.TotalCount} Treffer (erwartet {baseline - 3})");

        await _gallery.ClearTagFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Tag-Filter zurückgesetzt").ConfigureAwait(true);
        Check(!_gallery.HasTagFilter && _gallery.TotalCount == baseline && _gallery.TagFilterText == "Tags", "Tag-Filter zurückgesetzt → alle Dateien");

        // Suche findet Tag-Namen (kein Dateiname enthält „Smoketag“)
        _gallery.SearchTerm = "smoketag";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nach Tag-Namen").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null && _gallery.TotalCount == 3, $"Suche „smoketag“ findet {_gallery.TotalCount} getaggte Dateien (erwartet 3)");
        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);

        // Listenansicht
        _gallery.ViewMode = GalleryViewMode.List;
        await Task.Delay(400).ConfigureAwait(true);
        Check(_gallery.ShowList && !_gallery.ShowGrid && _gallery.IsListView, "Umschalter: Listenansicht aktiv");
        var listView = FindVisualChild<GalleryListView>(Application.Current.MainWindow);
        Check(listView is not null, "Listenansicht im Fenster");
        await WaitUntilAsync(
            () => listView!.DataGrid.Items.Count == _gallery.CurrentPage.Count && FindVisualChild<GalleryDataGridRow>(listView.DataGrid) is not null,
            ShortWait,
            "Zeilen der Listenansicht realisiert").ConfigureAwait(true);
        var columns = listView!.VisibleColumnKeysInOrder();
        Check(columns.Count == GalleryListColumns.All.Count && columns[0] == GalleryListColumnKey.Name, $"Spalten: {string.Join(", ", columns)}");
        var storedMode = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", GalleryViewSettingsStore.ViewModeKey)).ConfigureAwait(true);
        Check(storedMode == nameof(GalleryViewMode.List), $"Setting {GalleryViewSettingsStore.ViewModeKey} = {storedMode}");

        _gallery.ListColumnSettings = GalleryListColumnSettings.Parse(_gallery.ListColumnSettings)
            .WithVisibility(GalleryListColumnKey.Folder, false)
            .Serialize();
        await Task.Delay(300).ConfigureAwait(true);
        Check(!listView.VisibleColumnKeysInOrder().Contains(GalleryListColumnKey.Folder), "Spalte „Ordner“ ausgeblendet");
        var storedColumns = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", GalleryViewSettingsStore.ListColumnsKey)).ConfigureAwait(true);
        Check(storedColumns is not null && !storedColumns.Contains(nameof(GalleryListColumnKey.Folder), StringComparison.Ordinal), $"Setting {GalleryViewSettingsStore.ListColumnsKey} = {storedColumns}");

        var nameSort = GalleryListColumns.NextSort(GalleryListColumnKey.Name, _gallery.SelectedSort.Sort);
        Check(nameSort is GallerySort.NameAsc or GallerySort.NameDesc, $"Kopfzeile „Name“ → {nameSort}");
        await SetSortAsync(nameSort!.Value).ConfigureAwait(true);
        var names = _gallery.CurrentPage.Select(i => i.Name).ToList();
        Check(
            nameSort == GallerySort.NameAsc
                ? IsSorted(names, (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase))
                : IsSorted(names, (a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase)),
            $"Liste nach Name sortiert ({nameSort})");
        await SnapshotAsync("11-galerie-liste").ConfigureAwait(true);

        // Lightbox aus der Liste zeigt die Tags
        var tagged = _gallery.CurrentPage.First(i => i.HasTag(familie.Id));
        _gallery.OpenInLightboxCommand.Execute(tagged);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox aus der Liste").ConfigureAwait(true);
        var lightbox = Application.Current.Windows.OfType<LightboxWindow>().First().ViewModel;
        await WaitUntilAsync(() => lightbox.CurrentItem?.FileId == tagged.Id && lightbox.CurrentTags.Count == 2, ShortWait, "Lightbox-Tags geladen").ConfigureAwait(true);
        Check(lightbox.HasCurrentTags, $"Lightbox: {lightbox.CurrentTagsText}");
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        _gallery.ListColumnSettings = null;
        _gallery.ViewMode = GalleryViewMode.Grid;
        await SetSortAsync(GallerySort.ModifiedDesc).ConfigureAwait(true);
        Check(_gallery.ShowGrid && !_gallery.ShowList, "zurück zur Kachelansicht");

        // Verwaltungsseite „Tags“
        _navigation.NavigateTo(NavigationTags.Tags); // wie „Tags verwalten…“ → Einstellungen › Tags (ADR-0042)
        await WaitUntilAsync(
            () => IsOnSettingsSection(NavigationTags.Tags) && _tagManagement.Tags.Any(t => t.Id == urlaub.Id && t.FileCount == 3),
            ShortWait,
            "Tag-Verwaltung geladen").ConfigureAwait(true);
        Check(_tagManagement.Tags.First(t => t.Id == familie.Id).FileCount == 2, $"Verwaltung: {_tagManagement.SummaryText}");
        await SnapshotAsync("12-tags-verwaltung").ConfigureAwait(true);

        // Nach Sortier-/Seitenwechseln können die Items neue Instanzen sein → über die Id in der aktuellen Seite suchen.
        var targetIds = targets.Select(t => t.Id).ToList();
        GalleryItem? InPage(int id) => _gallery.CurrentPage.FirstOrDefault(i => i.Id == id);
        await _tags.RenameAsync(urlaub.Id, "Smoketag Reise").ConfigureAwait(true);
        await WaitUntilAsync(() => InPage(targetIds[0])?.Tags.Any(t => t.Name == "Smoketag Reise") == true, ShortWait, "Umbenennen erreicht die Kachel-Chips").ConfigureAwait(true);
        await _tagManagement.LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(_tagManagement.Tags.Any(t => t.Name == "Smoketag Reise"), "Verwaltung zeigt den neuen Namen");

        var removedFamilie = await _tags.DeleteAsync(familie.Id).ConfigureAwait(true);
        var removedUrlaub = await _tags.DeleteAsync(urlaub.Id).ConfigureAwait(true);
        Check(removedFamilie == 2 && removedUrlaub == 3, $"Tags gelöscht: {removedFamilie} + {removedUrlaub} Zuweisungen entfernt");
        var leftover = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileTags").ConfigureAwait(true);
        Check(leftover == 0, "DB: keine Zuweisungen übrig");
        await WaitUntilAsync(
            () => targetIds.All(id => InPage(id) is { HasTags: false }) && _gallery.TagMenuEntries.Count == 0,
            ShortWait,
            "Galerie ohne Tags").ConfigureAwait(true);

        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "zurück zur Galerie").ConfigureAwait(true);
    }

    /// <summary>
    /// Kategorien (ADR-0027): Kategorie „Smoke XX“ mit den Werten N und NN anlegen, über das Kontextmenü-Kommando
    /// zuweisen (3 Dateien N, eine davon danach NN), Zustand der Menüeinträge, Filter (Wert, ODER, „ohne Wert“), Spalte
    /// der Listenansicht (ein-/ausblenden), Verwaltungsseite, Umbenennen eines Werts und Löschen samt Zuordnungen.
    /// Hinterlässt keine Kategorien.
    /// </summary>
    private async Task CategoriesAsync()
    {
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie aktiv").ConfigureAwait(true);
        var baseline = _gallery.TotalCount;
        Check(baseline >= 4 && _gallery.CurrentPage.Count >= 3 && !_gallery.HasCategories, $"Ausgangslage: {baseline} Dateien, keine Kategorien");

        var xx = await _categories.CreateAsync("Smoke XX", ["N", "NN"]).ConfigureAwait(true);
        int n = xx.Values[0].Id, nn = xx.Values[1].Id;
        await WaitUntilAsync(
            () => _gallery.HasCategories && _gallery.CategoryMenuGroups.Any(g => g.CategoryId == xx.Id),
            ShortWait,
            "Neue Kategorie in Galerie-Menü und -Filter").ConfigureAwait(true);

        // Kontextmenü „Smoke XX → N“ für 3 Dateien, danach „NN“ für die erste
        _gallery.ClearSelectionCommand.Execute(null);
        var targets = _gallery.CurrentPage.Take(3).ToList();
        foreach (var item in targets)
        {
            item.IsSelected = true;
        }

        CategoryMenuEntry Entry(int? valueId) => _gallery.CategoryMenuGroups.First(g => g.CategoryId == xx.Id).Entries.First(e => e.ValueId == valueId);
        Check(Entry(n).State == false, "Menüeintrag „N“ vor der Zuweisung ohne Häkchen");
        Check(_gallery.SetSelectionCategoryValueCommand.CanExecute(Entry(n)), "Menüeintrag „N“ bei Auswahl bedienbar");
        await _gallery.SetSelectionCategoryValueCommand.ExecuteAsync(Entry(n)).ConfigureAwait(true);
        Check(Entry(n).State == true, "Menüeintrag „N“ nach der Zuweisung mit Häkchen");

        _gallery.ClearSelectionCommand.Execute(null);
        targets[0].IsSelected = true;
        await _gallery.SetSelectionCategoryValueCommand.ExecuteAsync(Entry(nn)).ConfigureAwait(true);
        Check(Entry(nn).State == true && Entry(n).State == false, "ein Wert je Kategorie: NN ersetzt N");
        var rows = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileCategoryValues WHERE CategoryId = $c", ("$c", xx.Id)).ConfigureAwait(true);
        var nRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileCategoryValues WHERE ValueId = $v", ("$v", n)).ConfigureAwait(true);
        Check(rows == 3 && nRows == 2, $"DB: {rows} Zuordnungen, davon {nRows} × N (erwartet 3 / 2)");
        Check(targets[0].Categories[xx.Id] == "NN" && targets[1].Categories[xx.Id] == "N", "Werte an den Kacheln");
        _gallery.ClearSelectionCommand.Execute(null);

        // Filter: N, N ODER NN, ohne Wert
        CategoryFilterEntry Filter(int? valueId) => _gallery.CategoryFilterGroups.First(g => g.CategoryId == xx.Id).Entries.First(e => e.ValueId == valueId);
        Filter(n).IsSelected = true;
        await _gallery.ApplyCategoryFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Kategorien-Filter N").ConfigureAwait(true);
        Check(_gallery.TotalCount == 2 && _gallery.HasCategoryFilter, $"Filter „N“: {_gallery.TotalCount} Treffer (erwartet 2), Knopf „{_gallery.CategoryFilterText}“");

        Filter(nn).IsSelected = true;
        await _gallery.ApplyCategoryFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Kategorien-Filter N oder NN").ConfigureAwait(true);
        Check(_gallery.TotalCount == 3, $"Filter „N oder NN“: {_gallery.TotalCount} Treffer (erwartet 3)");

        Filter(n).IsSelected = false;
        Filter(nn).IsSelected = false;
        Filter(null).IsSelected = true;
        await _gallery.ApplyCategoryFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Kategorien-Filter ohne Wert").ConfigureAwait(true);
        Check(_gallery.TotalCount == baseline - 3, $"Filter „ohne Wert“: {_gallery.TotalCount} Treffer (erwartet {baseline - 3})");

        await _gallery.ClearCategoryFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Kategorien-Filter zurückgesetzt").ConfigureAwait(true);
        Check(!_gallery.HasCategoryFilter && _gallery.TotalCount == baseline && _gallery.CategoryFilterText == "Kategorien", "Filter zurückgesetzt → alle Dateien");

        await CategoryShortcutSearchSortLightboxAsync(xx, n, nn, targets, baseline).ConfigureAwait(true);

        // Spalte der Listenansicht
        _gallery.ViewMode = GalleryViewMode.List;
        await Task.Delay(400).ConfigureAwait(true);
        var listView = FindVisualChild<GalleryListView>(Application.Current.MainWindow);
        Check(listView is not null, "Listenansicht im Fenster");
        await WaitUntilAsync(() => listView!.VisibleCategoryHeaders().Contains("Smoke XX"), ShortWait, "Spalte „Smoke XX“ sichtbar").ConfigureAwait(true);
        await SnapshotAsync("15-galerie-kategorien-liste").ConfigureAwait(true);
        _gallery.ListColumnSettings = GalleryListColumnSettings.Parse(_gallery.ListColumnSettings).WithCategoryVisibility(xx.Id, false).Serialize();
        await Task.Delay(300).ConfigureAwait(true);
        Check(!listView!.VisibleCategoryHeaders().Contains("Smoke XX"), "Spalte „Smoke XX“ ausgeblendet");
        _gallery.ListColumnSettings = null;
        _gallery.ViewMode = GalleryViewMode.Grid;
        await Task.Delay(300).ConfigureAwait(true);

        // Verwaltungsseite „Kategorien“
        _navigation.NavigateTo(NavigationTags.Categories); // Einstellungen › Kategorien (ADR-0042)
        await WaitUntilAsync(
            () => IsOnSettingsSection(NavigationTags.Categories) && _categoryManagement.Categories.Any(c => c.Id == xx.Id && c.FileCount == 3),
            ShortWait,
            "Kategorien-Verwaltung geladen").ConfigureAwait(true);
        await SnapshotAsync("16-kategorien-verwaltung").ConfigureAwait(true);

        var targetIds = targets.Select(t => t.Id).ToList();
        GalleryItem? InPage(int id) => _gallery.CurrentPage.FirstOrDefault(i => i.Id == id);
        await _categories.RenameValueAsync(nn, "Doppel-N").ConfigureAwait(true);
        await WaitUntilAsync(() => InPage(targetIds[0])?.Categories[xx.Id] == "Doppel-N", ShortWait, "Umbenennen erreicht die Kachel").ConfigureAwait(true);

        var removed = await _categories.DeleteAsync(xx.Id).ConfigureAwait(true);
        Check(removed == 3, $"Kategorie gelöscht: {removed} Zuordnungen entfernt");
        var leftover = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileCategoryValues").ConfigureAwait(true);
        Check(leftover == 0, "DB: keine Zuordnungen übrig");
        await WaitUntilAsync(() => !_gallery.HasCategories && targetIds.All(id => InPage(id) is { Categories.Count: 0 }), ShortWait, "Galerie ohne Kategorien").ConfigureAwait(true);

        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "zurück zur Galerie").ConfigureAwait(true);
    }

    /// <summary>
    /// Kategorien 0.2.23: Strg+K-Dialog (Fake) in Galerie und Lightbox, Tastenkürzel gebunden, Suche nach einem
    /// Kategorie-Wert, serverseitige Sortierung nach der Kategorie (auf/ab, Pfeil in der Kopfzeile). Stellt den Zustand
    /// am Ende wieder her (targets[0] = NN, targets[1..2] = N).
    /// </summary>
    private async Task CategoryShortcutSearchSortLightboxAsync(CategoryInfo xx, int n, int nn, IReadOnlyList<GalleryItem> targets, int baseline)
    {
        // Die Items der Page werden nach jedem Neuladen neu erzeugt (Filter, Suche) → immer über die Id auflösen.
        var targetIds = targets.Select(t => t.Id).ToList();
        GalleryItem? Item(int k) => _gallery.CurrentPage.FirstOrDefault(i => i.Id == targetIds[k]);

        async Task<int> CountValueAsync(int valueId)
            => await DbScalarAsync<int>("SELECT COUNT(*) FROM FileCategoryValues WHERE ValueId = $v", ("$v", valueId)).ConfigureAwait(true);

        // Strg+K in der Galerie: gebunden, ohne bestehendes Kürzel zu überschreiben
        var galleryView = FindVisualChild<GalleryView>(Application.Current.MainWindow);
        var bindings = galleryView?.InputBindings.OfType<KeyBinding>().ToList() ?? [];
        Check(
            bindings.Count(b => b.Key == Key.K && b.Modifiers == ModifierKeys.Control) == 1
            && bindings.Any(b => b.Key == Key.K && ReferenceEquals(b.Command, _gallery.EditSelectionCategoriesCommand))
            && bindings.Any(b => b.Key == Key.T && ReferenceEquals(b.Command, _gallery.EditSelectionTagsCommand)),
            "Strg+K → Kategorien zuweisen (Strg+T bleibt Tags)");

        if (_categoryPicker is not null)
        {
            // Auswahl NN + N → „gemischt“; Dialog setzt N für beide
            _gallery.ClearSelectionCommand.Execute(null);
            Item(0)!.IsSelected = true;
            Item(1)!.IsSelected = true;
            _categoryPicker.NextResult = new CategoryPickResult([new CategoryValueChange(xx.Id, n)]);
            Check(_gallery.EditSelectionCategoriesCommand.CanExecute(null), "Strg+K bei Auswahl bedienbar");
            await _gallery.EditSelectionCategoriesCommand.ExecuteAsync(null).ConfigureAwait(true);
            var item = _categoryPicker.LastRequest is { } request
                ? CategoryPickLogic.BuildItems(request).FirstOrDefault(i => i.Category.Id == xx.Id)
                : null;
            Check(item is { Initial.KeepsMixed: true } && _categoryPicker.LastRequest!.SelectionCount == 2, $"Strg+K-Dialog: 2 Dateien, „Smoke XX“ gemischt ({item?.Hint})");
            await WaitUntilAsync(() => Item(0)?.Categories[xx.Id] == "N", ShortWait, "Strg+K setzt N an der Kachel").ConfigureAwait(true);
            Check(await CountValueAsync(n) == 3 && await CountValueAsync(nn) == 0, "DB nach Strg+K: 3 × N, 0 × NN");

            // Zurück: targets[0] = NN (Ausgangswert im Dialog = N, nicht gemischt)
            _gallery.ClearSelectionCommand.Execute(null);
            Item(0)!.IsSelected = true;
            _categoryPicker.NextResult = new CategoryPickResult([new CategoryValueChange(xx.Id, nn)]);
            await _gallery.EditSelectionCategoriesCommand.ExecuteAsync(null).ConfigureAwait(true);
            var single = CategoryPickLogic.BuildItems(_categoryPicker.LastRequest!).First(i => i.Category.Id == xx.Id);
            Check(single.Initial.ValueId == n && !single.Initial.KeepsMixed, "Strg+K-Dialog: Ausgangswert N bei einer Datei");
            await WaitUntilAsync(() => Item(0)?.Categories[xx.Id] == "NN", ShortWait, "Strg+K setzt NN zurück").ConfigureAwait(true);
            _gallery.ClearSelectionCommand.Execute(null);
        }
        else
        {
            Check(false, "Rauchtest-Ersatz für den Kategorie-Dialog fehlt");
        }

        // Suche findet einen Kategorie-Wert (kein Dateiname enthält „Smokewert“)
        var extra = await _categories.AddValueAsync(xx.Id, "Smokewert").ConfigureAwait(true);
        await _categories.SetValueAsync([targetIds[2]], xx.Id, extra.Id).ConfigureAwait(true);
        _gallery.SearchTerm = "smokewer";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nach Kategorie-Wert").ConfigureAwait(true);
        Check(
            _gallery.ErrorMessage is null && _gallery.TotalCount == 1 && _gallery.CurrentPage.FirstOrDefault()?.Id == targetIds[2],
            $"Suche „smokewer“ findet {_gallery.TotalCount} Datei mit dem Wert (erwartet 1)");
        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);
        await _categories.SetValueAsync([targetIds[2]], xx.Id, n).ConfigureAwait(true);
        Check(_gallery.TotalCount == baseline, "nach der Suche wieder alle Dateien");

        // Serverseitige Sortierung nach der Kategorie (Kopfzeilenklick → SortByCategory)
        _gallery.ViewMode = GalleryViewMode.List;
        await Task.Delay(400).ConfigureAwait(true);
        var listView = FindVisualChild<GalleryListView>(Application.Current.MainWindow);
        var withN = new HashSet<int> { targetIds[1], targetIds[2] };
        _gallery.SortByCategory(xx.Id);
        await WaitForGalleryAsync("Sortierung nach Kategorie ↑").ConfigureAwait(true);
        var ids = _gallery.CurrentPage.Select(i => i.Id).ToList();
        Check(
            _gallery.SelectedSort.Category == new GalleryCategorySort(xx.Id, false) && ids.Count == baseline
            && withN.Contains(ids[0]) && withN.Contains(ids[1]) && ids[2] == targetIds[0],
            $"Sortierung „{_gallery.SelectedSort.Label}“: N, N, NN, dann ohne Wert");
        Check(_gallery.SortOptions.Count(o => o.Category is not null) == 1, "Sortier-Auswahl zeigt die Kategorie-Sortierung");
        Check(listView?.CategorySortDirection(xx.Id) == System.ComponentModel.ListSortDirection.Ascending, "Sortierpfeil ↑ an der Spalte „Smoke XX“");

        _gallery.SortByCategory(xx.Id);
        await WaitForGalleryAsync("Sortierung nach Kategorie ↓").ConfigureAwait(true);
        ids = _gallery.CurrentPage.Select(i => i.Id).ToList();
        Check(
            _gallery.SelectedSort.Category is { Descending: true } && ids[0] == targetIds[0] && withN.Contains(ids[1]) && withN.Contains(ids[2]),
            $"Sortierung „{_gallery.SelectedSort.Label}“: NN, N, N, dann ohne Wert");
        await SnapshotAsync("17-galerie-liste-kategorie-sortiert").ConfigureAwait(true);
        await SetSortAsync(GallerySort.ModifiedDesc).ConfigureAwait(true);
        Check(_gallery.SortOptions.All(o => o.Category is null) && listView?.CategorySortDirection(xx.Id) is null, "Standard-Sortierung ersetzt die Kategorie-Sortierung");
        _gallery.ViewMode = GalleryViewMode.Grid;
        await Task.Delay(300).ConfigureAwait(true);

        // Lightbox: Werte anzeigen und per Strg+K-Dialog zuweisen
        var target = Item(0)!;
        _gallery.OpenInLightboxCommand.Execute(target);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var lightbox = window.ViewModel;
        await WaitUntilAsync(
            () => lightbox.CurrentItem?.FileId == target.Id && lightbox.CurrentCategoriesText == "Smoke XX: NN",
            ShortWait,
            "Lightbox zeigt „Smoke XX: NN“").ConfigureAwait(true);
        Check(lightbox.HasCategories && lightbox.CategoryRows.Any(r => r.Label == "Smoke XX" && r.Value == "NN"), "Info-Panel: Zeile „Smoke XX = NN“");
        lightbox.IsInfoVisible = true;
        await Task.Delay(300).ConfigureAwait(true);
        await SnapshotAsync("18-lightbox-kategorien", window).ConfigureAwait(true);
        if (_categoryPicker is not null)
        {
            _categoryPicker.NextResult = new CategoryPickResult([new CategoryValueChange(xx.Id, null)]);
            await lightbox.EditCategoriesCommand.ExecuteAsync(null).ConfigureAwait(true);
            Check(_categoryPicker.LastRequest is { SelectionCount: 1 }, "Lightbox: Strg+K-Dialog für die aktuelle Datei");
            await WaitUntilAsync(() => !lightbox.HasCurrentCategoryValues, ShortWait, "Lightbox: „Kein Wert“ entfernt NN").ConfigureAwait(true);
            Check(await CountValueAsync(nn) == 0, "DB: NN entfernt");
            _categoryPicker.NextResult = new CategoryPickResult([new CategoryValueChange(xx.Id, nn)]);
            await lightbox.EditCategoriesCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitUntilAsync(() => lightbox.CurrentCategoriesText == "Smoke XX: NN", ShortWait, "Lightbox: NN wieder gesetzt").ConfigureAwait(true);
            await WaitUntilAsync(() => Item(0)?.Categories[xx.Id] == "NN", ShortWait, "Galerie-Kachel folgt der Lightbox").ConfigureAwait(true);
        }

        lightbox.IsInfoVisible = false;
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
        Check(await CountValueAsync(n) == 2 && await CountValueAsync(nn) == 1, "Ausgangszustand wiederhergestellt (2 × N, 1 × NN)");
    }

    private static T? FindVisualChild<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task CleanupAsync()
    {
        _navigation.NavigateTo(NavigationTags.Library);
        await _library.RunSmokeCleanupAsync(_root).ConfigureAwait(true);
        var libraries = await DbScalarAsync<int>("SELECT COUNT(*) FROM Libraries").ConfigureAwait(true);
        Check(libraries == 0, "Bibliothek entfernt");
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    private async Task StepAsync(string name, Func<Task> action)
    {
        _logger.LogInformation("Rauchtest ▶ {Step}", name);
        try
        {
            await action().ConfigureAwait(true);
            _results.Add($"OK   {name}");
            _logger.LogInformation("Rauchtest ✔ {Step}", name);
        }
        catch (Exception ex)
        {
            Failed++;
            _results.Add($"FAIL {name}: {ex.Message}");
            _logger.LogError(ex, "Rauchtest ✘ {Step}", name);
        }
    }

    private void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new SmokeCheckException(what);
        }

        _logger.LogInformation("Rauchtest   ✓ {What}", what);
    }

    /// <summary>Führt die Aktion aus und liefert die geworfene Ausnahme (null, wenn keine).</summary>
    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new SmokeCheckException($"Timeout nach {timeout.TotalSeconds:N0} s: {what}");
            }

            await Task.Delay(100).ConfigureAwait(true);
        }
    }

    /// <summary>Wartet auf einen gerade angestoßenen (ggf. 300 ms gebündelten) Reload der Galerie.</summary>
    private async Task WaitForGalleryAsync(string what)
    {
        await Task.Delay(450).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading, ShortWait, what).ConfigureAwait(true);
        await Task.Delay(100).ConfigureAwait(true);
    }

    private async Task SetSortAsync(GallerySort sort)
    {
        _gallery.SelectedSort = _gallery.SortOptions.First(o => o.Sort == sort && o.Category is null);
        await WaitForGalleryAsync($"Sortierung {sort}").ConfigureAwait(true);
        Check(_gallery.SelectedSort.Sort == sort && !_gallery.IsCategorySorted && _gallery.ErrorMessage is null, $"Sortierung {sort} angewendet");
    }

    private async Task SnapshotAsync(string name, Window? window = null)
    {
        if (_shotsDir is null)
        {
            return;
        }

        window ??= Application.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        // Layout, Render und Bild-Bindings abwarten, dann das eigene Fenster-Handle abfotografieren.
        await Task.Delay(500).ConfigureAwait(true);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var path = Path.Combine(_shotsDir, name + ".png");
        try
        {
            // Lightbox im Video-Modus: VideoView (HwndHost) per PrintWindow + LibVLC-Overlay-Fenster einblenden.
            FrameworkElement? hwndHost = null;
            Window? overlay = null;
            if (window is LightboxWindow lightbox && lightbox.ViewModel.IsVideo)
            {
                hwndHost = lightbox.FindName("VideoView") as FrameworkElement;
                overlay = WindowCapture.FindVlcForegroundWindow(lightbox);
            }

            _logger.LogInformation(
                "Rauchtest   Fenster „{Title}“: sichtbar={Visible} aktiv={Active} Zustand={State} {W:N0}×{H:N0} @ {Left:N0},{Top:N0}",
                window.Title, window.IsVisible, window.IsActive, window.WindowState, window.ActualWidth, window.ActualHeight, window.Left, window.Top);
            var method = WindowCapture.TrySave(window, path, hwndHost, overlay);
            _logger.LogInformation("Rauchtest   Screenshot {State}: {Path}", method is null ? "FEHLGESCHLAGEN" : $"gespeichert ({method})", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rauchtest   Screenshot fehlgeschlagen: {Path}", path);
        }
    }

    private IEnumerable<string> MediaFiles()
        => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(f => _formats.IsSupported(Path.GetExtension(f)));

    private int CountMediaFiles() => MediaFiles().Count();

    private async Task<int> FolderIdAsync(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var id = await DbScalarAsync<int>(
            "SELECT Id FROM Folders WHERE LibraryId = $lib AND (Path = $p OR Path = $p2) LIMIT 1",
            ("$lib", _libraryId),
            ("$p", trimmed),
            ("$p2", trimmed + Path.DirectorySeparatorChar)).ConfigureAwait(true);
        if (id == 0)
        {
            throw new SmokeCheckException($"Ordner nicht im Katalog: {path}");
        }

        return id;
    }

    private Task<T> DbScalarAsync<T>(string sql, params (string Name, object Value)[] parameters) => Task.Run(() =>
    {
        using var connection = _db.OpenSqliteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = command.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    });

    private Task<List<string>> DbRowsAsync(string sql, params (string Name, object Value)[] parameters) => Task.Run(() =>
    {
        using var connection = _db.OpenSqliteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "NULL";
            }

            rows.Add(string.Join(" | ", cells));
        }

        return rows;
    });

    private static bool PathEquals(string a, string b)
        => string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSorted<T>(IReadOnlyList<T> values, Comparison<T> comparison)
    {
        for (var i = 1; i < values.Count; i++)
        {
            if (comparison(values[i - 1], values[i]) > 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Rauchtest-Ersatz für den Tag-Auswahl-Dialog (Strg+T): merkt sich die Anfrage und liefert das vom Szenario gesetzte
/// Ergebnis — der modale Dialog würde den unbeaufsichtigten Lauf blockieren.
/// </summary>
internal sealed class SmokeTagPickerService : ITagPickerService
{
    public TagPickRequest? LastRequest { get; private set; }

    /// <summary>Nächstes Ergebnis; null = Abbruch.</summary>
    public TagPickResult? NextResult { get; set; }

    public Task<TagPickResult?> PickAsync(TagPickRequest request, Window? owner)
    {
        LastRequest = request;
        var result = NextResult;
        NextResult = null;
        return Task.FromResult(result);
    }
}

/// <summary>
/// Rauchtest-Ersatz für den Kategorie-Auswahl-Dialog (Strg+K): merkt sich die Anfrage und liefert das vom Szenario
/// gesetzte Ergebnis — der modale Dialog würde den unbeaufsichtigten Lauf blockieren.
/// </summary>
internal sealed class SmokeCategoryPickerService : ICategoryPickerService
{
    public CategoryPickRequest? LastRequest { get; private set; }

    /// <summary>Nächstes Ergebnis; null = Abbruch.</summary>
    public CategoryPickResult? NextResult { get; set; }

    public Task<CategoryPickResult?> PickAsync(CategoryPickRequest request, Window? owner)
    {
        LastRequest = request;
        var result = NextResult;
        NextResult = null;
        return Task.FromResult(result);
    }
}
