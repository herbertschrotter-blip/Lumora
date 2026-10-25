using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data.RawSql;
using Lumora.Domain.Entities;
using Lumora.Gallery.Archives;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Controls;
using Lumora.Gallery.ListView;
using Lumora.Gallery.Services;
using Lumora.Gallery.Tags;
using Lumora.Lightbox;
using Lumora.Services.Archive;
using Lumora.Services.Categories;
using Lumora.Services.Tags;
using Lumora.Services.Catalog;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Thumbnails;
using Lumora.Gallery.Data;
using System.Collections.Concurrent;
using System.Windows.Threading;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Eintrag der Sortier-Auswahl in der Toolbar. <paramref name="Category"/> (nicht null) = Sortierung nach einer
/// Kategorie (Kopfzeilenklick in der Listenansicht, ADR-0027); dann gilt <paramref name="Sort"/> nicht.
/// </summary>
public sealed record GallerySortOption(GallerySort Sort, string Label, GalleryCategorySort? Category = null);

/// <summary>
/// ViewModel der Galerie (ADR-0010): hält nur die aktuelle Page (≤ 1000 Items) als
/// <see cref="GalleryItem"/>, nie die ganze Library. Alle DB-Zugriffe laufen über
/// <see cref="GalleryPageSource"/> auf dem Thread-Pool; das ViewModel selbst arbeitet auf dem
/// UI-Thread — Nachrichten und Thumbnail-Callbacks von Worker-Threads werden dorthin dispatcht.
/// </summary>
public sealed partial class GalleryViewModel : ObservableObject
{
    private const int ReloadDebounceMs = 300;

    /// <summary>So lange bleibt eine Ergebnis-Meldung (Verschieben, Papierkorb, Favorit …) stehen, bevor der Lade-Status sie ersetzt.</summary>
    private static readonly TimeSpan ResultStatusHold = TimeSpan.FromSeconds(8);

    private static readonly GallerySortOption[] s_sortOptions =
    [
        new(GallerySort.CapturedDesc, "Aufnahme neu → alt"), // Standard seit 0.2.48 (ADR-0046)
        new(GallerySort.CapturedAsc, "Aufnahme alt → neu"),
        new(GallerySort.ModifiedDesc, "Datum neu → alt"),
        new(GallerySort.ModifiedAsc, "Datum alt → neu"),
        new(GallerySort.NameAsc, "Name A → Z"),
        new(GallerySort.NameDesc, "Name Z → A"),
        new(GallerySort.SizeDesc, "Größe groß → klein"),
        new(GallerySort.SizeAsc, "Größe klein → groß"),
    ];

    private static readonly int[] s_pageSizeOptions = [200, 500, 1000];

    private readonly ILibraryContextFactory _contextFactory;
    private readonly GalleryPageSource _source;
    private readonly IThumbnailService _thumbnails;
    private readonly ICatalogUpdateService _catalog;
    private readonly IFileOperationService _fileOps;
    private readonly IArchiveMoveService _archiveMove;
    private readonly ILightboxService _lightbox;
    private readonly IGalleryDialogService _dialogs;
    private readonly IFolderPickerService _folderPicker;
    private readonly IArchiveExtractDialogService _archiveDialog;
    private readonly ITagService _tags;
    private readonly ITagPickerService _tagPicker;
    private readonly ICategoryService _categories;
    private readonly ICategoryPickerService _categoryPicker;
    private readonly IGalleryViewSettingsStore _viewSettings;
    private readonly ILogger<GalleryViewModel> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    private readonly Dictionary<int, GalleryItem> _currentItems = new();
    private readonly HashSet<int> _selectedIds = new();

    /// <summary>
    /// Unveränderlicher Schnappschuss der Ids in <see cref="_currentItems"/> für den Thumbnail-Callback,
    /// der auf Worker-Threads läuft: <see cref="_currentItems"/> wird nur auf dem UI-Thread verändert und darf
    /// dort nicht nebenläufig gelesen werden. Wird nach jeder Änderung durch eine neue Instanz ersetzt.
    /// </summary>
    private volatile HashSet<int> _currentIdsSnapshot = new();
    private readonly ConcurrentQueue<ReadyThumbnail> _readyThumbnails = new(); // ADR-0043: mit Datei und Dekodierfläche
    private int _thumbnailFlushScheduled;

    /// <summary>Vom Kachel-Panel gemeldeter sichtbarer Index-Bereich in <see cref="CurrentPage"/> (-1 = unbekannt); Thumbnails dafür zuerst.</summary>
    private int _visibleFirstIndex = -1;
    private int _visibleLastIndex = -1;

    private CancellationTokenSource? _activeLoadCts;
    private CancellationTokenSource? _reloadDebounceCts;

    /// <summary>
    /// Zähler der Bibliotheks-Ladevorgänge: der zuletzt angestoßene gewinnt. Zwei Bereichswechsel kurz hintereinander (z. B.
    /// „Lösen“ meldet verzögert die alte aktive Bibliothek, gleich danach der Wechsel) dürfen sich nicht überholen.
    /// </summary>
    private int _libraryLoadGeneration;
    private DateTime _resultStatusUntil;
    private bool _isInitialized;
    private bool _suppressReload;
    private bool _suppressSelectionEvents;
    private DateTime _startupBegin;
    private int _libraryId;
    private string? _libraryRootPath;

    public GalleryViewModel(
        ILibraryContextFactory contextFactory,
        GalleryPageSource source,
        IThumbnailService thumbnails,
        ICatalogUpdateService catalog,
        IFileOperationService fileOps,
        IArchiveMoveService archiveMove,
        ILightboxService lightbox,
        IGalleryDialogService dialogs,
        IFolderPickerService folderPicker,
        IArchiveExtractDialogService archiveDialog,
        ITagService tags,
        ITagPickerService tagPicker,
        ICategoryService categories,
        ICategoryPickerService categoryPicker,
        IGalleryViewSettingsStore viewSettings,
        ILogger<GalleryViewModel> logger,
        Lumora.Gallery.CaptureDates.CaptureDateActions? captureDateActions = null,
        Lumora.Gallery.Places.PlaceActions? placeActions = null,
        Lumora.Services.Motifs.IMotifTermService? motifTerms = null,
        Lumora.Services.Library.ILibraryScope? libraryScope = null,
        Lumora.Albums.AlbumActions? albumActions = null,
        ISharedGalleryFilter? sharedFilter = null,
        Lumora.Shell.INavigationService? navigation = null,
        Lumora.Slideshow.ISlideshowService? slideshow = null,
        Lumora.Removal.RemovalActions? removalActions = null,
        Lumora.Marks.MarkSettingsState? markSettings = null)
    {
        InitMarks(markSettings); // Schalter „Symbole auf Galerie-Kacheln“ (Mockup 16, GalleryViewModel.Marks.cs)
        _removalActions = removalActions; // „Aus der Bibliothek entfernen“ für fehlende (ADR-0065, GalleryViewModel.MissingFiles.cs)
        _slideshow = slideshow; // Diashow (ADR-0059, GalleryViewModel.Slideshow.cs)
        InitAlbums(albumActions); // Alben und Ordner-Verknüpfungen (ADR-0054, GalleryViewModel.Albums.cs)
        _libraryScope = libraryScope ?? new Lumora.Services.Library.LibraryScope(contextFactory); // ADR-0048, GalleryViewModel.Scope.cs
        InitMotifs(motifTerms); // Bildinhalt-Suche (ADR-0052, GalleryViewModel.Motifs.cs)
        _contextFactory = contextFactory;
        _source = source;
        _thumbnails = thumbnails;
        _catalog = catalog;
        _fileOps = fileOps;
        _archiveMove = archiveMove;
        _lightbox = lightbox;
        _dialogs = dialogs;
        _folderPicker = folderPicker;
        _archiveDialog = archiveDialog;
        _tags = tags;
        _tagPicker = tagPicker;
        _categories = categories;
        _categoryPicker = categoryPicker;
        _viewSettings = viewSettings;
        _logger = logger;

        CurrentPage = new ObservableCollection<GalleryItem>();
        Subfolders = new ObservableCollection<GallerySubfolderDto>();
        TagMenuEntries = new ObservableCollection<TagMenuEntry>();
        TagFilterEntries = new ObservableCollection<TagFilterEntry>();
        _thumbnails.ThumbnailReady += OnThumbnailReady;
        RegisterMessages();
        InitCaptureDate(captureDateActions); // „Datum setzen…“, Rückgängig, Neuladen (GalleryViewModel.CaptureDate.cs, ADR-0046)
        InitPlaces(placeActions); // Filter „Ort“, Kartenausschnitt, „Ort setzen…“ (GalleryViewModel.Places.cs, ADR-0050)
        InitPages(sharedFilter, navigation); // gemeinsamer Filter, seitengebundene Teile (GalleryViewModel.Pages.cs, ADR-0055)
    }

    // ---- Gebundene Daten ---------------------------------------------------------------------

    public ObservableCollection<GalleryItem> CurrentPage { get; }

    /// <summary>Unterordner-Kacheln im Container-State (Ordner ohne direkte Dateien, PLAN Kap. 16).</summary>
    public ObservableCollection<GallerySubfolderDto> Subfolders { get; }

    /// <summary>Feste Sortierungen plus höchstens eine Kategorie-Sortierung, solange sie gewählt ist (ADR-0027).</summary>
    public IReadOnlyList<GallerySortOption> SortOptions => _sortOptions;

    public IReadOnlyList<int> PageSizeOptions => s_pageSizeOptions;

    /// <summary>
    /// Liefert das Owner-Window für die Lightbox; wird von der gerade geladenen View gesetzt (Galerie-Seite
    /// und Ordner-Seite teilen dieses ViewModel). Fällt auf das Hauptfenster zurück, wenn keine View geladen ist.
    /// </summary>
    public Func<Window?>? OwnerWindowResolver { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPageIndex), nameof(CanGoBack), nameof(CanGoForward))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand), nameof(NextPageCommand), nameof(FirstPageCommand), nameof(LastPageCommand))]
    private int _pageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(CanGoForward))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(LastPageCommand))]
    private int _pageSize = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(CanGoForward), nameof(IsEmpty), nameof(EmptyMessage), nameof(ResultCountText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(LastPageCommand))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private string? _searchTerm;

    [ObservableProperty]
    private GallerySortOption _selectedSort = s_sortOptions[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoForward), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand), nameof(NextPageCommand), nameof(FirstPageCommand), nameof(LastPageCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _statusText = "Bereit.";
    [ObservableProperty] private double _lastPageLoadMs;
    [ObservableProperty] private double _firstPageLoadMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _errorMessage;

    [ObservableProperty] private string _dbPathDisplay = string.Empty;
    [ObservableProperty] private string _libraryNameDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowGrid), nameof(ShowList), nameof(ShowNoLibrary), nameof(ShowCollage), nameof(ShowGalleryList))]
    private bool _hasLibrary;

    [ObservableProperty] private bool _isRootOffline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderFilter), nameof(FolderChipText), nameof(EmptyMessage))]
    private int? _folderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderChipText))]
    private string? _folderName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderChipText))]
    private bool _includeSubfolders = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowGrid), nameof(ShowList), nameof(ShowCollage), nameof(ShowGalleryList))]
    private bool _showSubfolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText), nameof(CanActOnSelection), nameof(CanExtractSelection))]
    [NotifyCanExecuteChangedFor(
        nameof(FavoriteSelectionCommand), nameof(UnfavoriteSelectionCommand),
        nameof(SetSelectionRatingCommand), nameof(ClearSelectionRatingCommand),
        nameof(MoveSelectionCommand), nameof(RecycleSelectionCommand), nameof(ArchiveSelectionCommand), nameof(ExtractSelectionCommand),
        nameof(EditSelectionTagsCommand), nameof(ToggleSelectionTagCommand), nameof(SetSelectionCategoryValueCommand), nameof(EditSelectionCategoriesCommand))]
    private int _selectedCount;

    /// <summary>Archive in der Auswahl (Aktionsleiste „Entpacken…“, ADR-0022).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExtractSelection))]
    [NotifyCanExecuteChangedFor(nameof(ExtractSelectionCommand))]
    private int _selectedArchiveCount;

    /// <summary>Gemeinsame Bewertung aller ausgewählten Dateien (null, wenn unterschiedlich oder leer).</summary>
    [ObservableProperty] private byte? _selectionRating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnSelection), nameof(CanExecuteUndo), nameof(CanExtractSelection))]
    [NotifyCanExecuteChangedFor(
        nameof(FavoriteSelectionCommand), nameof(UnfavoriteSelectionCommand),
        nameof(SetSelectionRatingCommand), nameof(ClearSelectionRatingCommand),
        nameof(MoveSelectionCommand), nameof(RecycleSelectionCommand), nameof(ArchiveSelectionCommand), nameof(UndoCommand), nameof(ExtractSelectionCommand),
        nameof(EditSelectionTagsCommand), nameof(ToggleSelectionTagCommand), nameof(SetSelectionCategoryValueCommand), nameof(EditSelectionCategoriesCommand))]
    private bool _isOperationRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public int DisplayPageIndex => PageIndex + 1;
    public bool CanGoBack => !IsLoading && PageIndex > 0;
    public bool CanGoForward => !IsLoading && PageIndex < PageCount - 1;

    public bool HasFolderFilter => FolderId is not null;
    public string FolderChipText => IncludeSubfolders
        ? $"Ordner: {FolderName} (mit Unterordnern)"
        : $"Ordner: {FolderName}";

    public bool HasSelection => SelectedCount > 0;
    public bool CanActOnSelection => SelectedCount > 0 && !IsOperationRunning;
    public bool CanExecuteUndo => CanUndo && !IsOperationRunning;
    public bool CanExtractSelection => SelectedArchiveCount > 0 && !IsOperationRunning && !IsAllLinkedView;
    public string SelectionText => SelectedCount == 1 ? "1 ausgewählt" : $"{SelectedCount:N0} ausgewählt";

    public bool ShowNoLibrary => !HasLibrary;
    public bool ShowGrid => HasLibrary && !ShowSubfolders && ViewMode == GalleryViewMode.Grid;
    public bool ShowList => HasLibrary && !ShowSubfolders && ViewMode == GalleryViewMode.List;
    public bool IsEmpty => HasLibrary && !IsLoading && !ShowSubfolders && TotalCount == 0 && ErrorMessage is null;

    public string EmptyMessage
    {
        get
        {
            if (ActiveFilterCount > 1)
            {
                return "Keine Dateien erfüllen alle gewählten Bedingungen zusammen — entferne einzelne Bedingungen unter „Es gilt“ oder setze die Filter zurück.";
            }

            if (!string.IsNullOrWhiteSpace(SearchTerm))
            {
                return $"Keine Treffer für „{SearchTerm.Trim()}“.";
            }

            if (HasTagFilter)
            {
                return TagFilterMode == GalleryTagMode.None
                    ? "Alle Dateien haben mindestens ein Tag."
                    : "Keine Dateien mit diesem Tag-Filter.";
            }

            if (HasCategoryFilter)
            {
                return "Keine Dateien mit diesem Kategorien-Filter.";
            }

            if (EmptyMessagePersons is { } personsMessage)
            {
                return personsMessage;
            }

            if (EmptyMessageMotifs is { } motifsMessage) // ADR-0052, GalleryViewModel.Motifs.cs
            {
                return motifsMessage;
            }

            if (HasAlbumFilter) // ADR-0054
            {
                return "Dieses Album ist (in diesem Bereich) noch leer — füge Fotos über das Kontextmenü „Zu Album hinzufügen“ oder Strg+L hinzu.";
            }

            if (FolderId is not null)
            {
                return "In diesem Ordner liegen keine Dateien, die zum Filter passen.";
            }

            return EmptyMessageForFileFilter(FilterState); // GalleryViewModel.Filters.cs (ADR-0041)
        }
    }

    // ---- Lebenszyklus --------------------------------------------------------------------------

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        _startupBegin = DateTime.UtcNow;
        DbPathDisplay = _contextFactory.DatabasePath;

        await LoadViewSettingsAsync();
        await LoadTagsAsync();
        await LoadCategoriesAsync();
        await ReloadLibraryAsync(null);

        FirstPageLoadMs = (DateTime.UtcNow - _startupBegin).TotalMilliseconds;
        if (HasLibrary)
        {
            _logger.LogInformation("Start Gallery → erste Page sichtbar: {Ms:N1} ms", FirstPageLoadMs);
        }
    }

    /// <summary>
    /// Setzt Ordner/Sortierung/Page zurück und lädt die (neue) aktive Library. Der gemeinsame Filter bleibt (ADR-0055); mit
    /// <paramref name="reloadTerms"/> werden Tags und Kategorien der (neuen) Gruppe vor der ersten Seite gelesen, damit Begriffe,
    /// die es im neuen Bereich nicht gibt, schon aus dem Filter gefallen sind.
    /// </summary>
    private async Task ReloadLibraryAsync(int? libraryId, bool reloadTerms = false)
    {
        var generation = ++_libraryLoadGeneration;
        _activeLoadCts?.Cancel();
        _reloadDebounceCts?.Cancel();
        ResetViewState();

        if (!await LoadLibraryAsync(libraryId, generation))
        {
            return;
        }

        try
        {
            _source.Open();
            _logger.LogInformation("Gallery-DB geöffnet: {Path}", _source.DatabasePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Datenbank konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogError(ex, "Fehler beim Öffnen der Gallery-DB");
            return;
        }

        await LoadPersonsAsync(); // Personen-Filter mit Zählern der Bibliothek (0.2.38)
        await LoadMotifsAsync(); // Motiv-Filter mit Zählern des Bereichs (ADR-0052)
        if (reloadTerms)
        {
            // Tags und Kategorien gehören der Gruppe der Bibliothek (ADR-0048); Häkchen bleiben nur für vorhandene (ADR-0055)
            await LoadTagsAsync();
            await LoadCategoriesAsync();
        }

        if (generation != _libraryLoadGeneration)
        {
            return; // ein neuerer Bereichswechsel lädt schon
        }

        if (HasAlbumFilter)
        {
            await LoadAlbumsAsync(); // gewähltes Album im neuen Bereich? sonst fällt der Filter vor der ersten Seite weg
        }
        else
        {
            FireAndForget(LoadAlbumsAsync); // Filter „Album“, Menü „Zu Album hinzufügen“ (ADR-0054) — blockiert die erste Seite nicht
        }
        await ReloadTotalAndPageAsync(restoreSelection: false);
        await RefreshCanUndoAsync();
    }

    /// <summary>
    /// Bibliothekswechsel: Ordner, Sortierung und Seite aller Seiten zurück. Der gemeinsame Filter bleibt (ADR-0055) —
    /// Dateiart, Favoriten, Bewertung, Ablage, Doppelte und Suche gelten überall; Tags, Personen, Kategorien, Motive und
    /// Album behalten nur die Häkchen, die es im neuen Bereich gibt (die Listen werden danach neu geladen). Ort und
    /// Kartenausschnitt stammen aus der Ortsliste bzw. den Dateien des alten Bereichs und fallen weg.
    /// </summary>
    private void ResetViewState()
    {
        _suppressReload = true;
        try
        {
            ResetPlaceFilterCore(); // Ort und Kartenausschnitt (ADR-0050) — bereichsbezogen
            NotifyPlaceFilterChanged();
            _contentSearchIds = null; // Bildinhalt-Suche = Datei-Ids des alten Bereichs (ADR-0052)
            _appliedContentSearch = null;
            ContentSearchText = null;
            ResetPageStates(); // Ordner/Sortierung der anderen Seite (GalleryViewModel.Pages.cs)
            FolderId = null;
            FolderName = null;
            IncludeSubfolders = true;
            SelectedSort = s_sortOptions[0];
            PageIndex = 0;
            TotalCount = 0;
            ErrorMessage = null;
            ShowSubfolders = false;
            Subfolders.Clear();
            ClearItems();
            ClearSelectionState();
        }
        finally
        {
            _suppressReload = false;
        }
    }

    /// <summary>Lädt die Library mit der Id bzw. (null) die aktive Library (ADR-0003: jüngstes LastOpenedAt).</summary>
    private async Task<bool> LoadLibraryAsync(int? libraryId, int generation)
    {
        try
        {
            await using var ctx = _contextFactory.Create();
            var lib = libraryId is { } id
                ? await SelectInfo(ctx.Libraries.Where(l => l.Id == id)).FirstOrDefaultAsync()
                : null;
            lib ??= await SelectInfo(ctx.Libraries
                    .OrderByDescending(l => l.LastOpenedAt)
                    .ThenByDescending(l => l.CreatedAt))
                .FirstOrDefaultAsync();

            if (generation != _libraryLoadGeneration)
            {
                return false; // überholt: ein neuerer Bereichswechsel setzt Bibliothek und Bereich
            }

            if (lib is null)
            {
                _libraryId = 0;
                _libraryRootPath = null;
                ClearScope();
                HasLibrary = false;
                LibraryNameDisplay = string.Empty;
                StatusText = "Keine Bibliothek vorhanden.";
                _logger.LogWarning("Keine Library in DB gefunden — Gallery bleibt leer");
                return false;
            }

            _libraryId = lib.Id;
            _libraryRootPath = lib.RootPath;
            await LoadScopeAsync(lib.Id); // Bereich: Bibliothek bzw. „Alle verknüpften“ (ADR-0048, GalleryViewModel.Scope.cs)
            if (generation != _libraryLoadGeneration)
            {
                return false;
            }

            LibraryNameDisplay = ScopeDisplayName($"{lib.Name}  ·  {lib.RootPath}");
            HasLibrary = true;

            // Offline-Check kann bei UNC-Pfaden blockieren → Thread-Pool.
            var rootPath = lib.RootPath;
            IsRootOffline = !await Task.Run(() => Directory.Exists(rootPath));
            if (IsRootOffline)
            {
                _logger.LogWarning("Library-Root nicht erreichbar (offline): {RootPath}", rootPath);
            }

            _logger.LogInformation("Aktive Library: Id={Id} Name={Name} RootPath={RootPath}", lib.Id, lib.Name, lib.RootPath);
            return true;
        }
        catch (Exception ex)
        {
            HasLibrary = false;
            ErrorMessage = $"Bibliothek konnte nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Fehler beim Lesen der Libraries-Tabelle");
            return false;
        }

        static IQueryable<LibraryInfoRow> SelectInfo(IQueryable<Library> query)
            => query.Select(l => new LibraryInfoRow(l.Id, l.Name, l.RootPath));
    }

    private sealed record LibraryInfoRow(int Id, string Name, string RootPath);

    // ---- Nachrichten (WeakReferenceMessenger, ggf. von Worker-Threads) ------------------------

    private void RegisterMessages()
    {
        _messenger.Register<GalleryViewModel, ActiveLibraryChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleActiveLibraryChangedAsync(m)));
        _messenger.Register<GalleryViewModel, ScanCompletedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleScanCompletedAsync(m)));
        _messenger.Register<GalleryViewModel, FolderSelectedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleFolderSelectedAsync(m)));
        _messenger.Register<GalleryViewModel, FilesChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleFilesChangedAsync(m)));
        _messenger.Register<GalleryViewModel, ExtractArchiveRequestedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.ExtractArchivesAsync([new ArchiveExtractTarget(m.FileId, m.Path, m.Name)], ActiveWindow())));
        _messenger.Register<GalleryViewModel, LibraryRootsChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleLibraryRootsChangedAsync(m)));
        _messenger.Register<GalleryViewModel, FoldersChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleFoldersChangedAsync(m)));
        _messenger.Register<GalleryViewModel, TagsChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleTagsChangedAsync(m)));
        _messenger.Register<GalleryViewModel, CategoriesChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleCategoriesChangedAsync(m)));
        RegisterPersonMessages(); // Personen (0.2.38, GalleryViewModel.Persons.cs)
        RegisterMotifMessages(); // Motive (ADR-0052, GalleryViewModel.Motifs.cs)
    }

    /// <summary>Wurzeln geändert (ADR-0020): Archiv-Wurzel bzw. Anzeigepfad kann wechseln, Status-Filter liefert andere Treffer → Library neu laden.</summary>
    private Task HandleLibraryRootsChangedAsync(LibraryRootsChangedMessage m)
    {
        if (!HasLibrary || !InScope(m.LibraryId))
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("LibraryRootsChangedMessage: LibraryId={LibraryId}", m.LibraryId);
        return ReloadLibraryAsync(_libraryId); // in „Alle verknüpften“ bleibt die aktive Bibliothek (ADR-0048)
    }

    /// <summary>Ordner angelegt/umbenannt: Pfade der Kacheln und Unterordner-Kacheln nachziehen (gebündelt).</summary>
    private Task HandleFoldersChangedAsync(FoldersChangedMessage m)
    {
        if (HasLibrary && InScope(m.LibraryId))
        {
            ScheduleReload();
        }

        return Task.CompletedTask;
    }

    private async Task HandleActiveLibraryChangedAsync(ActiveLibraryChangedMessage m)
    {
        _logger.LogInformation("ActiveLibraryChangedMessage: LibraryId={LibraryId}", m.LibraryId);
        // Tags und Kategorien gehören der Gruppe der Bibliothek (ADR-0048) — nach jedem Wechsel neu lesen, und zwar vor
        // der ersten Seite: der gemeinsame Filter bleibt (ADR-0055), Begriffe des alten Bereichs fallen dabei heraus.
        await ReloadLibraryAsync(m.LibraryId, reloadTerms: true);
    }

    private Task HandleScanCompletedAsync(ScanCompletedMessage m)
    {
        var r = m.Result;
        _logger.LogInformation(
            "ScanCompletedMessage: LibraryId={LibraryId} Modus={Mode} Ergebnis={Outcome} Neu={Added} Geändert={Modified} Fehlend={Missing} Wieder={Reappeared}",
            m.LibraryId, r.Mode, r.Outcome, r.Added, r.Modified, r.MarkedMissing, r.Reappeared);
        if (!HasLibrary)
        {
            return ReloadLibraryAsync(m.LibraryId);
        }

        if (!InScope(m.LibraryId))
        {
            return Task.CompletedTask;
        }

        // ClearAndRescan vergibt FileIds neu (ggf. wiederverwendet), Incremental ändert Dateien oder bringt sie zurück —
        // auch auf anderen Pages (z. B. Failed „Quelle fehlt“, solange das Laufwerk weg war). Deshalb den gesamten
        // In-Memory-Zustand des ThumbnailService verwerfen: der nächste Request prüft wieder gegen DB und Cache;
        // Wiederholungen bleiben über Thumbnails.AttemptCount begrenzt.
        _thumbnails.InvalidateAll();
        return ReloadTotalAndPageAsync(restoreSelection: true);
    }

    private Task HandleFolderSelectedAsync(FolderSelectedMessage m)
    {
        if (!HasLibrary || !InScope(m.LibraryId))
        {
            return Task.CompletedTask;
        }

        if (m.FolderId == FolderId && m.IncludeSubfolders == IncludeSubfolders)
        {
            return Task.CompletedTask; // bereits aktiv (z. B. von uns selbst gesendet)
        }

        _logger.LogInformation("FolderSelectedMessage: FolderId={FolderId} Rekursiv={Rek}", m.FolderId, m.IncludeSubfolders);
        return ApplyFolderFilterAsync(m.FolderId, m.IncludeSubfolders);
    }

    private async Task HandleFilesChangedAsync(FilesChangedMessage m)
    {
        if (!HasLibrary || !InScope(m.LibraryId))
        {
            return;
        }

        _logger.LogDebug("FilesChangedMessage: Kind={Kind} Count={Count}", m.Kind, m.FileIds.Count);
        // Favoriten- oder Bewertungs-Filter aktiv: eine Metadaten-Änderung kann die Trefferliste ändern → neu laden.
        if (m.Kind == FilesChangeKind.Metadata && !FileFilterDependsOnMetadata)
        {
            await RefreshMetadataAsync(m.FileIds);
        }
        else
        {
            // Removed: Thumbnails-Zeile und Cache-Datei sind weg, die FileId kann der Scanner neu vergeben;
            // Added: Undo/Incremental bringt Dateien (zurück), deren alter In-Memory-Zustand nicht mehr gilt.
            if (m.Kind is FilesChangeKind.Removed or FilesChangeKind.Added)
            {
                _thumbnails.Invalidate(m.FileIds);
            }

            // Moved/Removed/Added (oder Favoriten-Filter aktiv): Page neu laden, gebündelt.
            ScheduleReload();
        }

        // Favorit/Bewertung ändern nichts an den OperationBatches — Undo-Status nur bei Datei-Operationen prüfen.
        if (m.Kind != FilesChangeKind.Metadata)
        {
            await RefreshCanUndoAsync();
        }
    }

    private async Task RefreshMetadataAsync(IReadOnlyList<int> fileIds)
    {
        var inPage = fileIds.Where(_currentItems.ContainsKey).ToList();
        if (inPage.Count == 0)
        {
            return;
        }

        try
        {
            var rows = await _source.GetMetadataAsync(inPage, CancellationToken.None);
            foreach (var row in rows)
            {
                if (_currentItems.TryGetValue(row.Id, out var item))
                {
                    item.UpdateMetadata(row.IsFavorite, row.Rating, row.Missing);
                }
            }

            await ApplyTagsToItemsAsync(inPage, CancellationToken.None);
            await ApplyCategoriesToItemsAsync(inPage, CancellationToken.None);
            UpdateSelectionSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metadaten-Refresh fehlgeschlagen");
        }
    }

    /// <summary>Debounced (300 ms) Reload der aktuellen Page mit Erhalt der Auswahl.</summary>
    private void ScheduleReload()
    {
        _reloadDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadDebounceCts = cts;
        FireAndForget(async () =>
        {
            try
            {
                await Task.Delay(ReloadDebounceMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ReloadTotalAndPageAsync(restoreSelection: true);
        });
    }

    // ---- Laden ---------------------------------------------------------------------------------

    private CancellationToken BeginLoad()
    {
        _activeLoadCts?.Cancel();
        _activeLoadCts = new CancellationTokenSource();
        return _activeLoadCts.Token;
    }

    /// <summary>Zählt neu und lädt die aktuelle Page (inkl. Container-State-Prüfung).</summary>
    private async Task ReloadTotalAndPageAsync(bool restoreSelection)
    {
        if (!HasLibrary || !_source.IsOpen)
        {
            return;
        }

        var ct = BeginLoad();
        IsLoading = true;
        try
        {
            ErrorMessage = null;
            var sw = Stopwatch.StartNew();
            var query = BuildQuery();
            PublishSharedFilter(query); // Ordner- und Orte-Seite sehen denselben Filter (ADR-0055, GalleryViewModel.Pages.cs)
            var total = await _source.GetTotalCountAsync(query, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            TotalCount = total;
            await UpdateFilterSummaryAsync(query, total, ct); // Trefferzeile + „Es gilt“ (GalleryViewModel.Filters.cs)
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (PageIndex > PageCount - 1)
            {
                PageIndex = PageCount - 1;
            }

            if (total == 0 && query.HasFolder && !query.IncludeSubfolders && !query.HasSearch)
            {
                var subfolders = await _source.GetSubfoldersAsync(_libraryId, query.FolderId!.Value, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (subfolders.Count > 0)
                {
                    ClearItems();
                    ClearSelectionState();
                    Subfolders.Clear();
                    foreach (var sub in subfolders)
                    {
                        Subfolders.Add(sub);
                    }

                    ShowSubfolders = true;
                    SetLoadStatus($"Ordner „{FolderName}“ enthält keine Dateien, aber {subfolders.Count} Unterordner.");
                    _logger.LogInformation("Container-State: FolderId={FolderId} Unterordner={Count}", query.FolderId, subfolders.Count);
                    return;
                }
            }

            if (ShowSubfolders)
            {
                ShowSubfolders = false;
                Subfolders.Clear();
            }

            await LoadPageCoreAsync(query, ct, restoreSelection);
            sw.Stop();
            _logger.LogInformation(
                "Reload: Total={Total} Seite={Page}/{Pages} in {Ms:N1} ms",
                TotalCount, DisplayPageIndex, PageCount, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Fehler beim Reload");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Pager: nur die Page wechseln (Count bleibt), Auswahl wird verworfen.</summary>
    private async Task NavigateToPageAsync(int pageIndex)
    {
        if (!HasLibrary || !_source.IsOpen || ShowSubfolders)
        {
            return;
        }

        PageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        var ct = BeginLoad();
        IsLoading = true;
        try
        {
            ErrorMessage = null;
            ClearSelectionState();
            await LoadPageCoreAsync(BuildQuery(), ct, restoreSelection: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Fehler beim Page-Load");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadPageCoreAsync(GalleryQuery query, CancellationToken ct, bool restoreSelection)
    {
        var sw = Stopwatch.StartNew();
        var rows = await _source.LoadPageAsync(query, PageIndex, PageSize, TotalCount, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        sw.Stop();
        LastPageLoadMs = sw.Elapsed.TotalMilliseconds;

        List<GalleryItem> newItems = [];
        await DispatchAsync(() => newItems = ApplyRows(rows, restoreSelection));

        // Tags der Page in einem Batch nachladen (ADR-0021) — unabhängig von den Thumbnails.
        FireAndForget(() => ApplyTagsToItemsAsync(rows.Select(r => r.Id).ToList(), ct));
        FireAndForget(() => ApplyCategoriesToItemsAsync(rows.Select(r => r.Id).ToList(), ct)); // ADR-0027
        FireAndForget(() => ApplyPersonsToItemsAsync(rows.Select(r => r.Id).ToList(), ct)); // Personen (0.2.38)
        FireAndForget(() => ApplyPlacesToItemsAsync(rows.Select(r => r.Id).ToList(), ct)); // Orte (ADR-0050,
        FireAndForget(() => ApplyMotifsToItemsAsync(rows.Select(r => r.Id).ToList(), ct)); // Motive (ADR-0052)
        FireAndForget(() => ApplyFolderLinkMarksAsync(query, rows.Select(r => r.Id).ToList(), ct)); // Verknüpft (ADR-0054)

        SetLoadStatus($"{TotalCount:N0} Treffer · Seite {DisplayPageIndex} von {PageCount:N0} · {LastPageLoadMs:N1} ms");
        _logger.LogInformation(
            "Page geladen: Idx={Idx} Size={Size} Rows={Rows} Time={Ms:N1}ms Filter={Filter} Sort={Sort} Folder={Folder} Rekursiv={Rek} Search={Search}",
            PageIndex, PageSize, rows.Count, LastPageLoadMs, FilterState, SelectedSort.Sort,
            FolderId?.ToString() ?? "-", IncludeSubfolders, SearchTerm ?? "-");

        // Thumbnails nur anstoßen (Service dedupliziert, ist thread-sicher) — sichtbare Kacheln zuerst.
        FireAndForget(() => RequestThumbnailsVisibleFirstAsync(newItems));
    }

    /// <summary>
    /// Die View meldet den sichtbaren Index-Bereich des Kachel-Grids (VirtualizingTilePanel). Die Thumbnail-Queue arbeitet
    /// FIFO; deshalb werden die Anforderungen einer Seite nach Abstand zu diesem Bereich sortiert eingereiht.
    /// </summary>
    public void ReportVisibleRange(int firstIndex, int lastIndex)
    {
        _visibleFirstIndex = firstIndex;
        _visibleLastIndex = lastIndex;
    }

    /// <summary>
    /// Wartet den Layoutdurchlauf der neuen Seite ab (Loaded liegt unter Render), damit das Panel den sichtbaren Bereich
    /// gemeldet hat, und reiht dann sichtbare Kacheln zuerst, den Rest nach Abstand ein. Der synchrone Vorlauf des
    /// Services (DB-Batch-Check) läuft auf dem Thread-Pool, damit er den UI-Thread nicht blockiert.
    /// </summary>
    private async Task RequestThumbnailsVisibleFirstAsync(List<GalleryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            // Nach dem Layout: Dekodierfläche aus Kachelgröße × Bildschirm-Skalierung (ADR-0043) auf dem UI-Thread lesen.
            await dispatcher.InvokeAsync(UpdateThumbnailDecodeBox, DispatcherPriority.Loaded);
        }

        var ordered = OrderByVisibility(items, CurrentPage, _visibleFirstIndex, _visibleLastIndex);
        await Task.Run(() => RequestThumbnailsAsync(ordered));
    }

    /// <summary>Sortiert <paramref name="items"/> nach Abstand ihres Index in <paramref name="page"/> zum sichtbaren Bereich [first, last]; ohne bekannten Bereich unverändert.</summary>
    internal static List<GalleryItem> OrderByVisibility(List<GalleryItem> items, IList<GalleryItem> page, int first, int last)
    {
        if (first < 0 || last < first)
        {
            return items;
        }

        var indexById = new Dictionary<int, int>(page.Count);
        for (var i = 0; i < page.Count; i++)
        {
            indexById[page[i].Id] = i;
        }

        // OrderBy ist stabil: innerhalb gleicher Distanz bleibt die Seitenreihenfolge.
        return items
            .OrderBy(it => indexById.TryGetValue(it.Id, out var i) ? DistanceToRange(i, first, last) : int.MaxValue)
            .ToList();

        static int DistanceToRange(int index, int first, int last)
            => index < first ? first - index : index > last ? index - last : 0;
    }

    private GalleryQuery BuildQuery()
    {
        var term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim();
        return WithAlbums(WithMotifs(new GalleryQuery(_libraryId, FilterState, term, FolderId, IncludeSubfolders, SelectedSort.Sort, SelectedTagIds(), TagFilterMode, SelectedCategoryFilters(), SelectedSort.Category, SelectedPersonFilter())
        {
            LibraryIds = ScopeLibraryIdsForQuery, // ADR-0048: null = nur die aktive Bibliothek
        })); // Motive (ADR-0052), Album/Verknüpfungen (ADR-0054)
    }

    /// <summary>
    /// Übernimmt die geladenen Zeilen in <see cref="CurrentPage"/>, möglichst ohne die Items
    /// neu zu erzeugen: unveränderte Dateien behalten Instanz und Thumbnail (kein Flackern),
    /// geänderte werden ersetzt (Thumbnail wird bei gleichem Änderungsdatum übernommen).
    /// Gibt die Items zurück, für die noch ein Thumbnail angefordert werden muss.
    /// </summary>
    private List<GalleryItem> ApplyRows(IReadOnlyList<GalleryFileDto> rows, bool restoreSelection)
    {
        _suppressSelectionEvents = true;
        try
        {
            var target = new List<GalleryItem>(rows.Count);
            var replacedById = new Dictionary<int, GalleryItem>();
            var newIds = new HashSet<int>(rows.Count);

            foreach (var dto in rows)
            {
                newIds.Add(dto.Id);
                if (_currentItems.TryGetValue(dto.Id, out var existing) && existing.CoreMatches(dto))
                {
                    existing.UpdateMetadata(dto.IsFavorite, dto.Rating, dto.Missing);
                    existing.UpdateMediaSize(dto); // nachgetragene Bildmaße → Collage bricht neu um (ADR-0044)
                    existing.CaptureDate = dto.CaptureDate; // Aufnahmedatum (ADR-0046)
                    target.Add(existing);
                    continue;
                }

                var item = new GalleryItem(dto);
                if (existing is not null)
                {
                    if (existing.Thumbnail is not null && existing.ModifiedAtUnix == dto.ModifiedAtUnix)
                    {
                        item.Thumbnail = existing.Thumbnail;
                    }

                    replacedById[dto.Id] = item;
                }

                target.Add(item);
            }

            // 1) Verschwundene entfernen, ersetzte Instanzen austauschen
            for (var i = CurrentPage.Count - 1; i >= 0; i--)
            {
                var current = CurrentPage[i];
                if (!newIds.Contains(current.Id))
                {
                    DetachItem(current);
                    CurrentPage.RemoveAt(i);
                }
                else if (replacedById.TryGetValue(current.Id, out var replacement))
                {
                    DetachItem(current);
                    CurrentPage[i] = replacement;
                    AttachItem(replacement);
                    replacedById.Remove(current.Id);
                }
            }

            // 2) Reihenfolge herstellen, fehlende einfügen
            for (var i = 0; i < target.Count; i++)
            {
                var wanted = target[i];
                if (i < CurrentPage.Count && ReferenceEquals(CurrentPage[i], wanted))
                {
                    continue;
                }

                var j = IndexOfFrom(CurrentPage, wanted, i + 1);
                if (j >= 0)
                {
                    CurrentPage.Move(j, i);
                }
                else
                {
                    CurrentPage.Insert(i, wanted);
                    AttachItem(wanted);
                }
            }

            while (CurrentPage.Count > target.Count)
            {
                DetachItem(CurrentPage[^1]);
                CurrentPage.RemoveAt(CurrentPage.Count - 1);
            }

            _currentItems.Clear();
            foreach (var item in target)
            {
                _currentItems[item.Id] = item;
            }

            PublishCurrentIds();

            // 3) Auswahl: nach Ids wiederherstellen oder verwerfen
            if (restoreSelection)
            {
                _selectedIds.IntersectWith(newIds);
            }
            else
            {
                _selectedIds.Clear();
            }

            foreach (var item in target)
            {
                item.IsSelected = _selectedIds.Contains(item.Id);
            }

            UpdateSelectionSummary();
            return target.Where(it => it.Thumbnail is null).ToList();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private static int IndexOfFrom(ObservableCollection<GalleryItem> list, GalleryItem item, int start)
    {
        for (var i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private void ClearItems()
    {
        foreach (var item in CurrentPage)
        {
            DetachItem(item);
        }

        CurrentPage.Clear();
        _currentItems.Clear();
        PublishCurrentIds();
    }

    private void PublishCurrentIds() => _currentIdsSnapshot = new HashSet<int>(_currentItems.Keys);

    private async Task RequestThumbnailsAsync(IReadOnlyList<GalleryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var requests = new List<ThumbnailRequest>(items.Count);
        foreach (var it in items)
        {
            // Cache je Bibliothek der Datei (ADR-0048: in „Alle verknüpften“ nicht immer die aktive)
            requests.Add(new ThumbnailRequest(it.LibraryId != 0 ? it.LibraryId : _libraryId, it.Id, it.SourcePath, it.MediaType, it.SourceModifiedAtUtc));
        }

        try
        {
            await _thumbnails.RequestThumbnailsAsync(requests);
        }
        catch (Exception ex)
        {
            // Root offline / IO-Fehler: nur loggen, Platzhalter bleiben stehen.
            _logger.LogWarning(ex, "Thumbnail-Anforderung fehlgeschlagen");
        }
    }

    /// <summary>Läuft auf einem Worker-Thread des ThumbnailService — nur den unveränderlichen Id-Schnappschuss lesen.</summary>
    private void OnThumbnailReady(ThumbnailReadyEventArgs args)
    {
        if (!InScope(args.LibraryId) || !_currentIdsSnapshot.Contains(args.FileId))
        {
            return;
        }

        // Bitmap auf dem Worker-Thread laden + freezen (hält den Decode vom UI-Thread fern), passend zur Kachelfläche
        // (ADR-0043), dann gebündelt und mit Background-Priorität auf den UI-Thread: beim Erstaufbau treffen hunderte
        // Thumbnails in Sekunden ein; je ein Dispatcher-Aufruf pro Bild ließ Eingabe und Rendering verhungern.
        var box = ThumbnailDecodeBox;
        var image = TryLoadThumbnail(args.ThumbnailPath, box);
        if (image is null)
        {
            return;
        }

        EnqueueReadyThumbnail(new ReadyThumbnail(args.FileId, image, args.ThumbnailPath, box));
    }

    private void FlushReadyThumbnails()
    {
        Interlocked.Exchange(ref _thumbnailFlushScheduled, 0);
        while (_readyThumbnails.TryDequeue(out var entry))
        {
            if (_currentItems.TryGetValue(entry.FileId, out var item) && !IsOutdatedThumbnail(entry))
            {
                item.Thumbnail = entry.Image;
                RememberThumbnailSource(entry);
            }
        }
    }

    // ---- Auswahl -------------------------------------------------------------------------------

    private void AttachItem(GalleryItem item) => item.PropertyChanged += OnItemPropertyChanged;
    private void DetachItem(GalleryItem item) => item.PropertyChanged -= OnItemPropertyChanged;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryItem.LayoutAspectRatio))
        {
            ScheduleCollageRelayout(); // GalleryViewModel.Collage.cs (ADR-0044)
            return;
        }

        if (_suppressSelectionEvents || sender is not GalleryItem item)
        {
            return;
        }

        if (e.PropertyName == nameof(GalleryItem.IsSelected))
        {
            if (item.IsSelected)
            {
                _selectedIds.Add(item.Id);
            }
            else
            {
                _selectedIds.Remove(item.Id);
            }

            UpdateSelectionSummary();
        }
        else if (e.PropertyName == nameof(GalleryItem.Rating) && item.IsSelected)
        {
            UpdateSelectionSummary();
        }
    }

    private void UpdateSelectionSummary()
    {
        SelectedCount = _selectedIds.Count;
        SelectedArchiveCount = _selectedIds.Count(id => _currentItems.TryGetValue(id, out var it) && it.IsArchive);
        SelectedMissingCount = _selectedIds.Count(id => _currentItems.TryGetValue(id, out var it) && it.Missing); // ADR-0065

        byte? common = null;
        var first = true;
        var uniform = true;
        foreach (var id in _selectedIds)
        {
            if (!_currentItems.TryGetValue(id, out var item))
            {
                continue;
            }

            if (first)
            {
                common = item.Rating;
                first = false;
            }
            else if (item.Rating != common)
            {
                uniform = false;
                break;
            }
        }

        SelectionRating = uniform ? common : null;
        UpdateTagMenuStates();
        UpdateCategoryMenuStates();
    }

    private void ClearSelectionState()
    {
        _suppressSelectionEvents = true;
        try
        {
            foreach (var item in CurrentPage)
            {
                item.IsSelected = false;
            }

            _selectedIds.Clear();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        UpdateSelectionSummary();
    }

    private IReadOnlyList<int> SelectedIdsSnapshot() => _selectedIds.ToArray();

    [RelayCommand]
    private void SelectAll()
    {
        _suppressSelectionEvents = true;
        try
        {
            _selectedIds.Clear();
            foreach (var item in CurrentPage)
            {
                item.IsSelected = true;
                _selectedIds.Add(item.Id);
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void ClearSelection() => ClearSelectionState();

    // ---- Navigation, Filter, Suche, Sortierung -------------------------------------------------

    [RelayCommand]
    private Task ReloadAsync()
        => HasLibrary ? ReloadTotalAndPageAsync(restoreSelection: true) : ReloadLibraryAsync(null);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PrevPageAsync() => NavigateToPageAsync(PageIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task NextPageAsync() => NavigateToPageAsync(PageIndex + 1);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task FirstPageAsync() => NavigateToPageAsync(0);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task LastPageAsync() => NavigateToPageAsync(PageCount - 1);

    [RelayCommand]
    private Task SearchAsync()
    {
        _searchDebounceCts?.Cancel(); // Enter sucht sofort (GalleryViewModel.Filters.cs)
        // Nur Sonderzeichen (z. B. „---“, „***“) ergeben keinen FTS-Ausdruck (FtsQueryBuilder → null): Feld leeren und
        // sagen warum, statt den Text stehen zu lassen und still die ganze Bibliothek zu zeigen.
        if (!string.IsNullOrWhiteSpace(SearchTerm) && FtsQueryBuilder.FromUserInput(SearchTerm) is null)
        {
            var ignored = SearchTerm.Trim();
            SearchTerm = null;
            SetResultStatus($"Suche nach „{ignored}“ ignoriert — der Text enthält keine Buchstaben oder Ziffern.");
        }

        return ChangeQueryAsync();
    }

    [RelayCommand]
    private Task ClearSearchAsync()
    {
        SearchTerm = null;
        _searchDebounceCts?.Cancel();
        return ChangeQueryAsync();
    }

    [RelayCommand]
    private Task ClearFolderFilterAsync()
    {
        // Lokal zurücksetzen — bewusst keine FolderSelectedMessage (der Baum behält seine Auswahl).
        _suppressReload = true;
        try
        {
            FolderId = null;
            FolderName = null;
            IncludeSubfolders = true;
        }
        finally
        {
            _suppressReload = false;
        }

        return ChangeQueryAsync();
    }

    /// <summary>Klick auf eine Unterordner-Kachel: Filter lokal setzen und den Ordnerbaum per Nachricht nachziehen.</summary>
    [RelayCommand]
    private async Task OpenSubfolderAsync(GallerySubfolderDto? folder)
    {
        if (folder is null || !HasLibrary)
        {
            return;
        }

        await ApplyFolderFilterAsync(folder.Id, includeSubfolders: false, knownName: folder.Name);
        _messenger.Send(new FolderSelectedMessage(_libraryId, folder.Id, false));
    }

    private async Task ApplyFolderFilterAsync(int? folderId, bool includeSubfolders, string? knownName = null)
    {
        _suppressReload = true;
        try
        {
            FolderId = folderId;
            IncludeSubfolders = includeSubfolders;
            FolderName = knownName;
            PageIndex = 0;
        }
        finally
        {
            _suppressReload = false;
        }

        if (folderId is { } id && knownName is null)
        {
            try
            {
                var info = await _source.GetFolderInfoAsync(id, CancellationToken.None);
                FolderName = info?.Name ?? $"#{id}";
            }
            catch (Exception ex)
            {
                FolderName = $"#{id}";
                _logger.LogWarning(ex, "Ordnername konnte nicht geladen werden: {FolderId}", id);
            }
        }

        ClearSelectionState();
        await ReloadTotalAndPageAsync(restoreSelection: false);
    }

    private Task ChangeQueryAsync()
    {
        if (!HasLibrary)
        {
            return Task.CompletedTask;
        }

        PageIndex = 0;
        ClearSelectionState();
        return ReloadTotalAndPageAsync(restoreSelection: false);
    }

    partial void OnSelectedSortChanged(GallerySortOption value)
    {
        NotifySortBarChanged(); // Sortierleiste: Feld + Richtung (GalleryViewModel.SortBar.cs)
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    partial void OnPageSizeChanged(int value)
    {
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    // ---- Favorit / Bewertung (ICatalogUpdateService) -------------------------------------------

    [RelayCommand]
    private async Task ToggleFavoriteAsync(GalleryItem? item)
    {
        if (item is null)
        {
            return;
        }

        var target = !item.IsFavorite;
        item.IsFavorite = target; // optimistisch; FilesChangedMessage(Metadata) bestätigt aus der DB
        await RunServiceAsync(
            "Favorit",
            () => _catalog.SetFavoriteAsync([item.Id], target),
            onError: () => item.IsFavorite = !target);
    }

    [RelayCommand]
    private async Task SetItemRatingAsync(RatingRequest? request)
    {
        if (request?.Parameter is not GalleryItem item)
        {
            return;
        }

        var previous = item.Rating;
        item.Rating = request.Rating;
        await RunServiceAsync(
            "Bewertung",
            () => _catalog.SetRatingAsync([item.Id], request.Rating),
            onError: () => item.Rating = previous);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task FavoriteSelectionAsync() => SetSelectionFavoriteAsync(true);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task UnfavoriteSelectionAsync() => SetSelectionFavoriteAsync(false);

    private Task SetSelectionFavoriteAsync(bool isFavorite)
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync("Favorit", async () =>
        {
            await _catalog.SetFavoriteAsync(ids, isFavorite);
            foreach (var id in ids)
            {
                if (_currentItems.TryGetValue(id, out var item))
                {
                    item.IsFavorite = isFavorite;
                }
            }

            SetResultStatus(isFavorite
                ? $"{ids.Count:N0} Datei(en) als Favorit markiert."
                : $"Favorit bei {ids.Count:N0} Datei(en) entfernt.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task SetSelectionRatingAsync(RatingRequest? request) => ApplySelectionRatingAsync(request?.Rating);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task ClearSelectionRatingAsync() => ApplySelectionRatingAsync(null);

    private Task ApplySelectionRatingAsync(byte? rating)
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync("Bewertung", async () =>
        {
            await _catalog.SetRatingAsync(ids, rating);
            foreach (var id in ids)
            {
                if (_currentItems.TryGetValue(id, out var item))
                {
                    item.Rating = rating;
                }
            }

            UpdateSelectionSummary();
            SetResultStatus(rating is { } r
                ? $"{ids.Count:N0} Datei(en) mit {r} Stern(en) bewertet."
                : $"Bewertung bei {ids.Count:N0} Datei(en) entfernt.");
        });
    }

    // ---- Datei-Operationen (IFileOperationService) ---------------------------------------------

    [RelayCommand(CanExecute = nameof(CanActOnLibrarySelection))]
    private async Task MoveSelectionAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0 || await RejectInAllLinkedViewAsync("Verschieben"))
        {
            return;
        }

        var target = _folderPicker.PickFolder($"{ids.Count:N0} Datei(en) verschieben nach…", _libraryRootPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await RunOperationAsync("Verschieben", async () =>
        {
            var result = await _fileOps.MoveAsync(ids, target);
            await ReportBatchResultAsync("Verschieben", result);
            ScheduleReload();
        });
    }

    // ---- „Ins Archiv…“ (ADR-0020) ---------------------------------------------------------------

    /// <summary>Aktionsleiste / Strg+M: Auswahl in den Archiv-Ordner verschieben (Zieldialog im IArchiveMoveService).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnLibrarySelection))]
    private Task ArchiveSelectionAsync() => ArchiveIdsAsync(SelectedIdsSnapshot());

    /// <summary>Kontextmenü der Kachel: ist die Kachel Teil der Auswahl, wird die ganze Auswahl archiviert, sonst nur sie.</summary>
    [RelayCommand]
    private Task ArchiveItemAsync(GalleryItem? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var ids = item.IsSelected ? SelectedIdsSnapshot() : new[] { item.Id };
        return ArchiveIdsAsync(ids);
    }

    private async Task ArchiveIdsAsync(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0 || await RejectInAllLinkedViewAsync("In die Bibliothek"))
        {
            return;
        }

        await ArchiveIdsCoreAsync(ids);
    }

    private Task ArchiveIdsCoreAsync(IReadOnlyList<int> ids)
    {
        // Die aktuelle Ansicht bleibt (z. B. Filter „Nicht archiviert“): der Reload zeigt die restlichen Dateien.
        return RunOperationAsync("In die Bibliothek", async () =>
        {
            var result = await _archiveMove.ArchiveAsync(ids);
            if (result is null)
            {
                return; // abgebrochen
            }

            await ReportBatchResultAsync("In die Bibliothek", result);
            ScheduleReload();
        });
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task RecycleSelectionAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return;
        }

        var message = ids.Count == 1
            ? "1 Datei in den Windows-Papierkorb verschieben?"
            : $"{ids.Count:N0} Dateien in den Windows-Papierkorb verschieben?";
        var confirmed = await _dialogs.ConfirmAsync("In den Papierkorb verschieben", message, "In den Papierkorb", isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync("Papierkorb", async () =>
        {
            var result = await _fileOps.RecycleAsync(ids);
            await ReportBatchResultAsync("Papierkorb", result);
            ScheduleReload();
        });
    }

    [RelayCommand(CanExecute = nameof(CanExecuteUndo))]
    private Task UndoAsync()
        => RunOperationAsync("Rückgängig", async () =>
        {
            if (await TryUndoGeoTagAsync())
            {
                return; // „Ort setzen“ war der jüngste Eintrag (ADR-0050)
            }

            if (await TryUndoCaptureDateAsync())
            {
                return; // „Datum setzen“ war der jüngste Eintrag (ADR-0046)
            }

            var result = await _fileOps.UndoLastAsync();
            if (result is null)
            {
                SetResultStatus("Nichts rückgängig zu machen.");
                return;
            }

            await ReportBatchResultAsync("Rückgängig", result);
            ScheduleReload();
        });

    private async Task ReportBatchResultAsync(string verb, FileOperationBatchResult result)
    {
        var total = result.Succeeded + result.Failed;
        SetResultStatus(result.Failed == 0
            ? $"{verb}: {result.Succeeded:N0} Datei(en) erfolgreich."
            : $"{verb}: {result.Succeeded:N0} erfolgreich, {result.Failed:N0} fehlgeschlagen.");
        _logger.LogInformation(
            "{Verb}: Batch={BatchId} Typ={Type} OK={Succeeded} Fehler={Failed}",
            verb, result.BatchId, result.Type, result.Succeeded, result.Failed);

        if (result.Failed > 0)
        {
            var lines = result.Items
                .Where(i => !i.Success)
                .Select(i => $"{Path.GetFileName(i.SourcePath)}: {i.Error ?? "unbekannter Fehler"}")
                .ToList();
            await _dialogs.ShowErrorsAsync(
                $"{verb}: {result.Failed:N0} Fehler",
                $"{result.Failed:N0} von {total:N0} Datei(en) konnten nicht verarbeitet werden.",
                lines);
        }
    }

    private async Task RefreshCanUndoAsync()
    {
        try
        {
            var batches = await _fileOps.GetRecentBatchesAsync(UndoBatchWindow); // 50 statt 10: „Datum setzen“-Batches füllen das Fenster (ADR-0046)
            CanUndo = batches.Any(b => b.CanUndo) || await HasCaptureDateUndoAsync() || await HasGeoTagUndoAsync(); // + „Datum setzen“ (ADR-0046), „Ort setzen“ (ADR-0050)
        }
        catch (Exception ex)
        {
            CanUndo = false;
            _logger.LogDebug(ex, "Undo-Status konnte nicht ermittelt werden");
        }
    }

    /// <summary>Einzelaktion ohne Sperre der Aktionsleiste (Herz/Sterne auf der Kachel).</summary>
    private async Task RunServiceAsync(string what, Func<Task> action, Action? onError = null)
    {
        try
        {
            ErrorMessage = null;
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            onError?.Invoke();
            ErrorMessage = $"{what} fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "{What} fehlgeschlagen", what);
        }
    }

    /// <summary>Aktion auf der Auswahl: sperrt die Aktionsleiste, meldet Fehler, prüft danach Undo.</summary>
    private async Task RunOperationAsync(string what, Func<Task> action)
    {
        if (IsOperationRunning)
        {
            return;
        }

        IsOperationRunning = true;
        try
        {
            ErrorMessage = null;
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{what} fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "{What} fehlgeschlagen", what);
        }
        finally
        {
            IsOperationRunning = false;
        }

        await RefreshCanUndoAsync();
    }

    // ---- Archive (ADR-0022): Entpacken über den Dialog, Inhaltsansicht in der Lightbox ---------

    /// <summary>Aktionsleiste: alle ausgewählten Archive entpacken (Dialog mit Ziel, Optionen, Fortschritt).</summary>
    [RelayCommand(CanExecute = nameof(CanExtractSelection))]
    private Task ExtractSelectionAsync()
        => ExtractArchivesAsync(SelectedIdsSnapshot()
            .Select(id => _currentItems.GetValueOrDefault(id))
            .Where(it => it is { IsArchive: true })
            .Select(it => new ArchiveExtractTarget(it!.Id, it.SourcePath, it.Name))
            .ToList());

    /// <summary>Kontextmenü der Kachel: dieses Archiv entpacken.</summary>
    [RelayCommand]
    private Task ExtractItemAsync(GalleryItem? item)
        => item is { IsArchive: true }
            ? ExtractArchivesAsync([new ArchiveExtractTarget(item.Id, item.SourcePath, item.Name)])
            : Task.CompletedTask;

    /// <param name="owner">Besitzer des Dialogs; null = Galerie-Fenster. Aus der Lightbox kommt deren Fenster, damit der Dialog davor liegt.</param>
    private async Task ExtractArchivesAsync(IReadOnlyList<ArchiveExtractTarget> targets, Window? owner = null)
    {
        if (targets.Count == 0 || !HasLibrary || IsOperationRunning || await RejectInAllLinkedViewAsync("Entpacken"))
        {
            return;
        }

        await RunOperationAsync("Entpacken", async () =>
        {
            owner ??= OwnerWindowResolver?.Invoke() ?? Application.Current?.MainWindow;
            var results = await _archiveDialog.ShowAsync(new ArchiveExtractDialogRequest(targets, _libraryId, _libraryRootPath), owner);
            ReportExtractResults(results);
        });
    }

    /// <summary>
    /// Rauchtest/Tests: Archive ohne Dialog mit festen Optionen entpacken (derselbe Service, dieselbe Ergebnisauswertung).
    /// <paramref name="options"/> ohne LibraryId/ArchiveFileId wird je Archiv ergänzt.
    /// </summary>
    internal async Task<IReadOnlyList<ArchiveExtractResult>> ExtractArchivesHeadlessAsync(IReadOnlyList<int> fileIds, ArchiveExtractOptions options, IArchiveService archives)
    {
        var results = new List<ArchiveExtractResult>();
        await RunOperationAsync("Entpacken", async () =>
        {
            foreach (var id in fileIds)
            {
                if (!_currentItems.TryGetValue(id, out var item) || !item.IsArchive)
                {
                    continue;
                }

                var effective = options with { LibraryId = options.LibraryId ?? _libraryId, ArchiveFileId = options.ArchiveFileId ?? item.Id };
                results.Add(await archives.ExtractAsync(item.SourcePath, effective));
            }

            ReportExtractResults(results);
        });
        return results;
    }

    /// <summary>Kontextmenü: Datei im Explorer markieren.</summary>
    [RelayCommand]
    private void ShowInExplorer(GalleryItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            Lumora.Shell.HiddenLibraries.HiddenSessionPrivacy.ThrowIfShellBlocked(); // versteckte Sitzung: keine Shell-Spuren (ADR-0049)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.SourcePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetResultStatus($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    private static Window? ActiveWindow()
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    private void ReportExtractResults(IReadOnlyList<ArchiveExtractResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        var files = results.Sum(r => r.FilesExtracted);
        var errors = results.Sum(r => r.Errors.Count);
        var canceled = results.Any(r => r.Canceled);
        SetResultStatus(canceled
            ? $"Entpacken abgebrochen: {files:N0} Datei(en) entpackt."
            : errors == 0
                ? $"Entpacken: {files:N0} Datei(en) aus {results.Count:N0} Archiv(en) entpackt."
                : $"Entpacken: {files:N0} Datei(en) entpackt, {errors:N0} Fehler.");
        _logger.LogInformation("Entpacken: {Archives} Archive, {Files} Dateien, {Errors} Fehler, abgebrochen={Canceled}", results.Count, files, errors, canceled);
        ScheduleReload();
    }

    // ---- Lightbox ------------------------------------------------------------------------------

    /// <summary>Enter/Doppelklick: Lightbox mit allen Items der aktuellen Page, Start = Item.</summary>
    [RelayCommand]
    private void OpenInLightbox(GalleryItem? item)
    {
        if (item is null || CurrentPage.Count == 0)
        {
            return;
        }

        // Start per Referenz, sonst per FileId; fehlt das Item ganz, nur dieses eine öffnen (0.2.42).
        var (entries, startIndex, match) = BuildLightboxSequence(CurrentPage, item);
        if (match == LightboxStartMatch.NotInPage)
        {
            _logger.LogWarning("Lightbox: File={FileId} steht nicht in der aktuellen Page ({Count} Einträge) — öffne nur diese Datei", item.Id, CurrentPage.Count);
        }
        else if (match == LightboxStartMatch.Id)
        {
            _logger.LogDebug("Lightbox: File={FileId} per Id statt per Referenz in der Page gefunden (Index {Index})", item.Id, startIndex);
        }

        try
        {
            var owner = OwnerWindowResolver?.Invoke() ?? Application.Current?.MainWindow;
            _lightbox.Open(entries, startIndex, owner);
        }
        catch (Exception ex)
        {
            // z. B. DirectoryNotFound bei Library-Root offline: melden, nicht abstürzen.
            ErrorMessage = $"Lightbox konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogError(ex, "Lightbox-Open fehlgeschlagen für File={FileId}", item.Id);
        }
    }

    // ---- Tags (ADR-0021) -----------------------------------------------------------------------

    private readonly Dictionary<int, TagInfo> _tagById = new();

    /// <summary>Alle Tags als Menüeinträge mit Zustand bezogen auf die Auswahl (Aktionsleiste „Tags“, Kontextmenü).</summary>
    public ObservableCollection<TagMenuEntry> TagMenuEntries { get; }

    /// <summary>Tags im Filter-Dropdown (Häkchen + Zähler).</summary>
    public ObservableCollection<TagFilterEntry> TagFilterEntries { get; }

    public bool HasNoTags => TagMenuEntries.Count == 0;

    /// <summary>Verknüpfung der gewählten Tags im Filter (ODER / UND / ohne Tag).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTagFilter), nameof(TagFilterText), nameof(EmptyMessage))]
    private GalleryTagMode _tagFilterMode = GalleryTagMode.Any;

    public bool HasTagFilter => TagFilterMode == GalleryTagMode.None || TagFilterEntries.Any(e => e.IsSelected);

    /// <summary>Beschriftung des Filter-Knopfs: „Tags“ oder „Tags: Urlaub, Familie (UND)“.</summary>
    public string TagFilterText
    {
        get
        {
            if (TagFilterMode == GalleryTagMode.None)
            {
                return "Tags: ohne Tag";
            }

            var names = TagFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList();
            if (names.Count == 0)
            {
                return "Tags";
            }

            var mode = names.Count > 1 ? (TagFilterMode == GalleryTagMode.All ? " (UND)" : " (ODER)") : string.Empty;
            return $"Tags: {string.Join(", ", names)}{mode}";
        }
    }

    partial void OnTagFilterModeChanged(GalleryTagMode value)
    {
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    private IReadOnlyList<int>? SelectedTagIds()
    {
        var ids = TagFilterEntries.Where(e => e.IsSelected).Select(e => e.Tag.Id).ToList();
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>Häkchen im Filter-Dropdown geändert → Filter anwenden.</summary>
    [RelayCommand]
    private Task ApplyTagFilterAsync()
    {
        NotifyTagFilterChanged();
        return _suppressReload || !_isInitialized ? Task.CompletedTask : ChangeQueryAsync();
    }

    [RelayCommand]
    private Task ClearTagFilterAsync()
    {
        if (!HasTagFilter)
        {
            return Task.CompletedTask;
        }

        ResetTagFilter();
        return ChangeQueryAsync();
    }

    private void ResetTagFilter()
    {
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            foreach (var entry in TagFilterEntries)
            {
                entry.IsSelected = false;
            }

            TagFilterMode = GalleryTagMode.Any;
        }
        finally
        {
            _suppressReload = suppress;
        }

        NotifyTagFilterChanged();
    }

    private void NotifyTagFilterChanged()
    {
        OnPropertyChanged(nameof(HasTagFilter));
        OnPropertyChanged(nameof(TagFilterText));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>Tag-Liste und Zähler laden; Filter-Häkchen bleiben je TagId erhalten.</summary>
    private async Task LoadTagsAsync()
    {
        try
        {
            var tags = await _tags.GetAllAsync();
            var counts = await _tags.GetFileCountsAsync();
            var selected = TagFilterEntries.Where(e => e.IsSelected).Select(e => e.Tag.Id).ToHashSet();

            _tagById.Clear();
            TagMenuEntries.Clear();
            TagFilterEntries.Clear();
            foreach (var tag in tags)
            {
                _tagById[tag.Id] = tag;
                TagMenuEntries.Add(new TagMenuEntry(tag));
                TagFilterEntries.Add(new TagFilterEntry(tag, counts.GetValueOrDefault(tag.Id)) { IsSelected = selected.Contains(tag.Id) });
            }

            OnPropertyChanged(nameof(HasNoTags));
            NotifyTagFilterChanged();
            UpdateTagMenuStates();
            _logger.LogDebug("Tags geladen: {Count}", tags.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tags konnten nicht geladen werden");
        }
    }

    /// <summary>Stammdaten geändert (Einstellungen, Dialog „Neues Tag“): Liste neu laden, Chips der Page neu abbilden.</summary>
    private async Task HandleTagsChangedAsync(TagsChangedMessage m)
    {
        var hadFilter = HasTagFilter;
        await LoadTagsAsync();
        foreach (var item in CurrentPage)
        {
            if (item.Tags.Count > 0)
            {
                item.Tags = MapTags(item.Tags.Select(t => t.Id));
            }
        }

        // Gelöschtes Tag war Teil des Filters → Trefferliste ändert sich.
        if (m.Kind == TagsChangeKind.Deleted && hadFilter && HasLibrary)
        {
            NotifyTagFilterChanged();
            ScheduleReload();
        }
    }

    /// <summary>Tags der Dateien (Batch) laden und den Items der Page zuordnen.</summary>
    private async Task ApplyTagsToItemsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        try
        {
            var map = await _tags.GetTagIdsForFilesAsync(fileIds, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (_currentItems.TryGetValue(id, out var item))
                    {
                        item.Tags = map.TryGetValue(id, out var tagIds) ? MapTags(tagIds) : Array.Empty<TagInfo>();
                    }
                }

                UpdateTagMenuStates();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tags der Seite konnten nicht geladen werden");
        }
    }

    private IReadOnlyList<TagInfo> MapTags(IEnumerable<int> tagIds)
    {
        var list = new List<TagInfo>();
        foreach (var id in tagIds)
        {
            if (_tagById.TryGetValue(id, out var tag))
            {
                list.Add(tag);
            }
        }

        list.Sort(CompareTags);
        return list;
    }

    private static int CompareTags(TagInfo a, TagInfo b)
    {
        var order = a.SortOrder.CompareTo(b.SortOrder);
        return order != 0 ? order : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Zustand je Tag-Menüeintrag: alle / keine / ein Teil der ausgewählten Dateien hat das Tag.</summary>
    private void UpdateTagMenuStates()
    {
        if (TagMenuEntries.Count == 0)
        {
            return;
        }

        var selected = new List<GalleryItem>(_selectedIds.Count);
        foreach (var id in _selectedIds)
        {
            if (_currentItems.TryGetValue(id, out var item))
            {
                selected.Add(item);
            }
        }

        foreach (var entry in TagMenuEntries)
        {
            if (selected.Count == 0)
            {
                entry.State = false;
                continue;
            }

            var count = 0;
            foreach (var item in selected)
            {
                if (item.HasTag(entry.Tag.Id))
                {
                    count++;
                }
            }

            entry.State = count == 0 ? false : count == selected.Count ? true : null;
        }
    }

    /// <summary>Menüeintrag geklickt: haben alle ausgewählten Dateien das Tag, wird es entfernt, sonst allen zugewiesen.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task ToggleSelectionTagAsync(TagMenuEntry? entry)
    {
        var ids = SelectedIdsSnapshot();
        if (entry is null || ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        var assign = entry.State != true;
        var tag = entry.Tag;
        return RunOperationAsync("Tag", async () =>
        {
            var changed = assign
                ? await _tags.AssignAsync(ids, [tag.Id])
                : await _tags.RemoveAsync(ids, [tag.Id]);
            foreach (var id in ids)
            {
                if (_currentItems.TryGetValue(id, out var item))
                {
                    item.Tags = assign
                        ? (item.HasTag(tag.Id) ? item.Tags : MapTags(item.Tags.Select(t => t.Id).Append(tag.Id)))
                        : item.Tags.Where(t => t.Id != tag.Id).ToList();
                }
            }

            UpdateTagMenuStates();
            SetResultStatus(assign
                ? $"Tag „{tag.Name}“ bei {ids.Count:N0} Datei(en) gesetzt ({changed:N0} neu)."
                : $"Tag „{tag.Name}“ bei {ids.Count:N0} Datei(en) entfernt.");
            if (HasTagFilter)
            {
                ScheduleReload();
            }
        });
    }

    /// <summary>Strg+T: Tag-Auswahl-Dialog für die Auswahl (dreistufig bei gemischtem Zustand, „Neues Tag“).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task EditSelectionTagsAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return;
        }

        var counts = new Dictionary<int, int>();
        foreach (var id in ids)
        {
            if (!_currentItems.TryGetValue(id, out var item))
            {
                continue;
            }

            foreach (var tag in item.Tags)
            {
                counts[tag.Id] = counts.GetValueOrDefault(tag.Id) + 1;
            }
        }

        var request = new TagPickRequest(
            _tagById.Values.OrderBy(t => t, Comparer<TagInfo>.Create(CompareTags)).ToList(),
            counts,
            ids.Count,
            name => _tags.CreateAsync(name));
        var owner = OwnerWindowResolver?.Invoke() ?? Application.Current?.MainWindow;
        var result = await _tagPicker.PickAsync(request, owner);
        if (result is null || result.IsEmpty)
        {
            return;
        }

        await RunOperationAsync("Tags", async () =>
        {
            var assigned = result.AssignTagIds.Count > 0 ? await _tags.AssignAsync(ids, result.AssignTagIds) : 0;
            var removed = result.RemoveTagIds.Count > 0 ? await _tags.RemoveAsync(ids, result.RemoveTagIds) : 0;
            await ApplyTagsToItemsAsync(ids.Where(_currentItems.ContainsKey).ToList(), CancellationToken.None);
            SetResultStatus($"Tags bei {ids.Count:N0} Datei(en) geändert: {assigned:N0} zugewiesen, {removed:N0} entfernt.");
            if (HasTagFilter)
            {
                ScheduleReload();
            }
        });
    }

    // ---- Ansicht: Kachel-Grid / Liste ----------------------------------------------------------

    private bool _suppressViewSettings;
    private string? _listColumnSettings;

    /// <summary>Kachel-Grid oder Listenansicht; die Wahl wird in der Settings-Tabelle gemerkt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridView), nameof(IsListView), nameof(ShowGrid), nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(IsCollageView), nameof(ShowCollage), nameof(ShowGalleryList), nameof(IsTileSizeAdjustable))] // Collage (ADR-0044)
    private GalleryViewMode _viewMode = GalleryViewMode.Grid;

    public bool IsGridView => ViewMode == GalleryViewMode.Grid;
    public bool IsListView => ViewMode == GalleryViewMode.List;

    /// <summary>Sichtbare Spalten der Listenansicht in Reihenfolge (serialisiert, siehe <see cref="GalleryListColumnSettings"/>); null = Standard.</summary>
    public string? ListColumnSettings
    {
        get => _listColumnSettings;
        set
        {
            if (SetProperty(ref _listColumnSettings, value) && _isInitialized && !_suppressViewSettings)
            {
                FireAndForget(() => _viewSettings.SetAsync(GalleryViewSettingsStore.ListColumnsKey, value));
            }
        }
    }

    // ---- Vorschaugröße (Kachelbreite, 0.2.19) -----------------------------------------------------

    /// <summary>Kleinste, größte und Standard-Kachelbreite in px; Schrittweite für Regler und Strg+Mausrad.</summary>
    public const int MinTileSize = 120;
    public const int MaxTileSize = 320;
    public const int DefaultTileSize = 180;
    public const int TileSizeStep = 20;

    /// <summary>Höhe der Namenszeile unter dem Bild + Rand: bleibt fest, nur der Bildbereich wächst (Seitenverhältnis wie bei 180 × 160).</summary>
    private const double TileHeightAt180 = 160;
    private const double TileMediaRatio = 0.64;

    /// <summary>Kachelbreite in DIP (120–320); wird gemerkt. Vorschaubilder werden passend dazu dekodiert (ADR-0043).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileWidth), nameof(TileHeight))]
    private int _tileSize = DefaultTileSize;

    public double TileWidth => TileSize;

    public double TileHeight => Math.Round(TileHeightAt180 + (TileSize - DefaultTileSize) * TileMediaRatio);

    public int MinTileSizeValue => MinTileSize;

    public int MaxTileSizeValue => MaxTileSize;

    public int TileSizeStepValue => TileSizeStep;

    /// <summary>Begrenzt auf 120–320 und rastet auf die Schrittweite ein (Regler liefert Zwischenwerte).</summary>
    public static int NormalizeTileSize(double value)
    {
        var snapped = (int)(Math.Round(value / TileSizeStep) * TileSizeStep);
        return Math.Clamp(snapped, MinTileSize, MaxTileSize);
    }

    /// <summary>Strg+Mausrad / Strg+Plus/Minus: eine Stufe größer (+1) oder kleiner (−1).</summary>
    public void StepTileSize(int direction) => TileSize = NormalizeTileSize(TileSize + Math.Sign(direction) * TileSizeStep);

    partial void OnTileSizeChanged(int value)
    {
        var normalized = NormalizeTileSize(value);
        if (normalized != value)
        {
            TileSize = normalized; // löst den Handler erneut mit dem gültigen Wert aus
            return;
        }

        if (_isInitialized && !_suppressViewSettings)
        {
            FireAndForget(() => _viewSettings.SetAsync(GalleryViewSettingsStore.TileSizeKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        UpdateThumbnailDecodeBox(); // größere Kacheln → zu kleine Vorschaubilder neu dekodieren (ADR-0043)
    }

    partial void OnViewModeChanged(GalleryViewMode value)
    {
        _logger.LogInformation("Galerie-Ansicht: {Mode}", value);
        UpdateThumbnailDecodeBox(); // Collage dekodiert auf die Reihenhöhe, Kacheln auf den Bildbereich (ADR-0044)
        if (_isInitialized && !_suppressViewSettings)
        {
            var key = ViewModeSettingKey; // je Seite (Galerie/Ordner, ADR-0055)
            FireAndForget(() => _viewSettings.SetAsync(key, value.ToString()));
        }
    }

    private async Task LoadViewSettingsAsync()
    {
        _suppressViewSettings = true;
        try
        {
            var mode = await _viewSettings.GetAsync(GalleryViewSettingsStore.ViewModeKey);
            await LoadPageViewSettingsAsync(); // Ansicht der Ordner-Seite (ADR-0055)
            if (Enum.TryParse<GalleryViewMode>(mode, ignoreCase: true, out var parsed))
            {
                ApplyStoredGalleryViewMode(parsed); // je nach aktiver Seite (GalleryViewModel.Pages.cs)
            }

            ListColumnSettings = await _viewSettings.GetAsync(GalleryViewSettingsStore.ListColumnsKey);

            var tileSize = await _viewSettings.GetAsync(GalleryViewSettingsStore.TileSizeKey);
            if (int.TryParse(tileSize, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var size))
            {
                TileSize = NormalizeTileSize(size);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ansichts-Einstellungen konnten nicht geladen werden");
        }
        finally
        {
            _suppressViewSettings = false;
        }
    }

    // ---- Statuszeile ---------------------------------------------------------------------------

    /// <summary>Ergebnis einer Aktion; bleibt <see cref="ResultStatusHold"/> stehen, auch wenn der Reload danach den Lade-Status setzen will.</summary>
    private void SetResultStatus(string text)
    {
        StatusText = text;
        _resultStatusUntil = DateTime.UtcNow + ResultStatusHold;
    }

    /// <summary>Lade-Status („n Treffer · Seite x von y“); überschreibt eine frische Ergebnis-Meldung nicht.</summary>
    private void SetLoadStatus(string text)
    {
        if (DateTime.UtcNow < _resultStatusUntil)
        {
            return;
        }

        StatusText = text;
    }

    // ---- Threading-Helfer ----------------------------------------------------------------------

    private void RunOnUi(Func<Task> work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            FireAndForget(work);
            return;
        }

        dispatcher.InvokeAsync(() => FireAndForget(work));
    }

    private async void FireAndForget(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hintergrundaktion der Galerie fehlgeschlagen");
        }
    }

    private static Task DispatchAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
