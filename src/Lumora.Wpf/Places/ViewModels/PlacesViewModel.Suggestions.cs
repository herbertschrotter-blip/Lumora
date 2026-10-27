using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Places.Map;
using Lumora.Places.Suggestions;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Places;
using Lumora.Services.Places.Suggestions;
using Lumora.Shell;

namespace Lumora.Places.ViewModels;

/// <summary>
/// „Ungenaue Orte verbessern“ auf der Seite „Orte“ (ADR-0062, Mockup 10): Umschalter „Ohne Ort | Ungenau“ mit Zählern,
/// Erklärung, Filter-Chips, Liste nach Tagen/Ausflügen, Übernehmen/Ablehnen je Foto, je Ausflug und „Garmin + sichere
/// übernehmen“, Speichern „nur im Katalog“ oder „auch ins Foto schreiben (EXIF)“, Rückgängig über den Hinweis, Vorschlag
/// auf der Karte ziehen; Karte zeigt gespeicherte Punkte grau, Vorschläge in der Akzentfarbe, die Korrektur gestrichelt und
/// die Spur der Aktivität.
/// </summary>
public sealed partial class PlacesViewModel
{
    /// <summary>Höchstens so viele Spurpunkte zeichnet die Karte (gleichmäßig ausgedünnt).</summary>
    public const int MaxOverlayTrackPoints = 3000;

    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(10);

    private PlacesSuggestionContext? _suggestionContext;
    private readonly Dictionary<GeoSuggestionListKind, GeoSuggestionSet> _sets = new();
    private LibraryScopeInfo _suggestionScope = LibraryScopeInfo.None;
    private int _suggestionVersion;
    private int _overlayVersion;
    private bool _keepRowsOnce;
    private int? _toastBatchId;
    private IReadOnlyList<int>? _toastRejected;
    private CancellationTokenSource? _toastTimer;
    private Task _lastSuggestionLoad = Task.CompletedTask;

    /// <summary>Bereich „Ungenau“ verfügbar (Dienste registriert).</summary>
    public bool HasSuggestions => _suggestionContext is not null;

    /// <summary>Zuletzt gestartetes Laden der Vorschläge (Tests, Rauchtest).</summary>
    public Task LastSuggestionLoad => _lastSuggestionLoad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocatedMode), nameof(IsInaccurateMode), nameof(IsSuggestionMode), nameof(MapMarkers), nameof(MapOverlay), nameof(SidePanelWidth))]
    private PlacesSideMode _sideMode = PlacesSideMode.Unlocated;

    public bool IsUnlocatedMode => SideMode == PlacesSideMode.Unlocated;

    /// <summary>Breite der Seitenleiste: Kacheln 300, Vorschlagsliste 400 (Mockup 10).</summary>
    public GridLength SidePanelWidth => new(IsSuggestionMode ? 400 : 300);

    public bool IsInaccurateMode => SideMode == PlacesSideMode.Inaccurate;

    /// <summary>Liste mit Vorschlägen sichtbar („Ungenau“ bzw. „Ohne Ort“ mit Vorschlag).</summary>
    public bool IsSuggestionMode => SideMode != PlacesSideMode.Unlocated;

    /// <summary>Marker der Karte — in der Vorschlagsliste ausgeblendet (dort zeigt die Überlagerung die Korrekturen).</summary>
    public IReadOnlyList<MapMarker> MapMarkers => IsSuggestionMode ? [] : Markers;

    partial void OnMarkersChanged(IReadOnlyList<MapMarker> value) => OnPropertyChanged(nameof(MapMarkers));

    /// <summary>Überlagerung der Karte (nur in der Vorschlagsliste).</summary>
    public MapOverlay? MapOverlay => IsSuggestionMode ? Overlay : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapOverlay))]
    private MapOverlay? _overlay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedText), nameof(HasEstimated), nameof(InaccurateButtonText))]
    private int _estimatedCount;

    /// <summary>Fotos ohne Ort mit Vorschlag (Hinweis unter „Ohne Ort“).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnlocatedHint), nameof(HasUnlocatedHint))]
    private int _unlocatedSuggestionCount;

    [ObservableProperty] private int _activityCount;

    public bool HasEstimated => EstimatedCount > 0;

    /// <summary>Kopfzeile: „158 mit geschätzter Position“.</summary>
    public string EstimatedText => EstimatedCount > 0 ? $"{PlacesText.N(EstimatedCount)} mit geschätzter Position" : string.Empty;

    public string UnlocatedButtonText => $"Ohne Ort {PlacesText.N(UnlocatedCount)}";

    public string InaccurateButtonText => $"Ungenau {PlacesText.N(EstimatedCount)}";

    partial void OnUnlocatedCountChanged(int value) => OnPropertyChanged(nameof(UnlocatedButtonText));

    public bool HasUnlocatedHint => UnlocatedSuggestionCount > 0;

    public string UnlocatedHint => UnlocatedSuggestionCount == 0
        ? string.Empty
        : $"{PlacesText.N(UnlocatedSuggestionCount)} {(UnlocatedSuggestionCount == 1 ? "Foto" : "Fotos")} ohne Ort {(UnlocatedSuggestionCount == 1 ? "liegt" : "liegen")} in einer GPS-Spur bzw. nahe an Fotos mit echtem GPS — mit Vorschlag.";

    public ObservableCollection<SuggestionFilterChip> FilterChips { get; } =
    [
        new(SuggestionFilter.All, "Alle") { IsSelected = true },
        new(SuggestionFilter.Track, "Garmin-Spur"),
        new(SuggestionFilter.Sure, "Sicher"),
        new(SuggestionFilter.Likely, "Wahrscheinlich"),
        new(SuggestionFilter.None, "Ohne Vorschlag"),
    ];

    [ObservableProperty] private SuggestionFilter _currentSuggestionFilter = SuggestionFilter.All;

    /// <summary>Zeilen der Liste: <see cref="SuggestionGroupRow"/> und <see cref="SuggestionItemRow"/> (virtualisiert).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestionRows))]
    private IReadOnlyList<object> _suggestionRows = [];

    public bool HasSuggestionRows => SuggestionRows.Count > 0;

    /// <summary>Leere Liste: je Filter ein passender Satz.</summary>
    public string EmptySuggestionText => IsSuggestionsLoading
        ? "Vorschläge werden berechnet …"
        : CurrentSuggestionFilter == SuggestionFilter.All
            ? (IsInaccurateMode ? "Keine Fotos mit geschätzter Position." : "Keine Fotos ohne Ort mit Aufnahmezeit.")
            : "Keine Fotos in diesem Filter.";

    /// <summary>Hinweis „noch keine GPS-Spuren“ in der Liste.</summary>
    public bool HasNoActivities => ActivityCount == 0;

    partial void OnActivityCountChanged(int value) => OnPropertyChanged(nameof(HasNoActivities));

    partial void OnIsSuggestionsLoadingChanged(bool value) => OnPropertyChanged(nameof(EmptySuggestionText));

    partial void OnCurrentSuggestionFilterChanged(SuggestionFilter value) => OnPropertyChanged(nameof(EmptySuggestionText));

    /// <summary>Legende: „GPS-Spur „Wandern“ 13:55–16:30“ der ausgewählten Zeile, sonst allgemein.</summary>
    public string LegendTrackText => SelectedRow?.Item.Track is { } track
        ? $"GPS-Spur „{track.Title}“ {Clock(track.StartUnix, SelectedRow.DisplayOffsetMinutes)}–{Clock(track.EndUnix, SelectedRow.DisplayOffsetMinutes)}"
        : "GPS-Spur der Aktivität";

    private static string Clock(long unix, int offsetMinutes)
        => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.AddMinutes(offsetMinutes).ToString("HH:mm", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));

    [ObservableProperty] private string _suggestionSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyGroupText), nameof(CanApplyGroup), nameof(LegendTrackText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGroupCommand))]
    private SuggestionItemRow? _selectedRow;

    [ObservableProperty] private bool _isSuggestionsLoading;

    /// <summary>Speichern: false = nur im Katalog, true = auch ins Foto schreiben (EXIF bzw. XMP).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveToCatalogOnly))]
    private bool _saveToFile;

    public bool SaveToCatalogOnly
    {
        get => !SaveToFile;
        set => SaveToFile = !value;
    }

    partial void OnSaveToFileChanged(bool value) => _ = PersistSaveToFileAsync(value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyTrackAndSureText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyTrackAndSureCommand))]
    private int _trackAndSureOpen;

    public string ApplyTrackAndSureText => $"Garmin + sichere übernehmen ({PlacesText.N(TrackAndSureOpen)})";

    public bool CanApplyGroup => SelectedRow?.Group is { CanApply: true };

    public string ApplyGroupText => SelectedRow?.Group is { } g ? $"Ausflug ({PlacesText.N(g.OpenSuggestions)})" : "Ausflug";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    private string? _toastText;

    public bool HasToast => !string.IsNullOrEmpty(ToastText);

    // ---- Lebenszyklus --------------------------------------------------------------------------------------------------

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>PlacesViewModel.cs</c>).</summary>
    private void InitSuggestions(PlacesSuggestionContext? context)
    {
        _suggestionContext = context;
        if (context is null)
        {
            return;
        }

        _messenger.Register<PlacesViewModel, GeoAccuracyChangedMessage>(this, static (r, m) => UiThread.Post(() => r.OnSuggestionsInvalidated(m.LibraryIds)));
        _messenger.Register<PlacesViewModel, GpsTracksChangedMessage>(this, static (r, _) => UiThread.Post(() => r.OnSuggestionsInvalidated(null)));
        _messenger.Register<PlacesViewModel, ShowFileSuggestionOnMapMessage>(this, static (r, m) => UiThread.Post(() => _ = r.ShowFileSuggestionAsync(m.FileId)));
        _messenger.Register<PlacesViewModel, ShowAreaOnMapMessage>(this, static (r, m) => UiThread.Post(() => _ = r.ShowAreaAsync(m.Bounds)));
        _ = LoadSaveToFileAsync(context);
    }

    private async Task LoadSaveToFileAsync(PlacesSuggestionContext context)
    {
        try
        {
            var settings = await context.Settings.LoadAsync().ConfigureAwait(true);
            _loadingSaveToFile = true;
            SaveToFile = settings.SaveToFile;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orte: Einstellung „Speichern“ nicht gelesen");
        }
        finally
        {
            _loadingSaveToFile = false;
        }
    }

    private bool _loadingSaveToFile;

    private async Task PersistSaveToFileAsync(bool value)
    {
        if (_loadingSaveToFile || _suggestionContext is not { } context)
        {
            return;
        }

        try
        {
            var settings = await context.Settings.LoadAsync().ConfigureAwait(true);
            await context.Settings.SaveAsync(settings with { SaveToFile = value }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orte: Einstellung „Speichern“ nicht gespeichert");
        }
    }

    private void OnSuggestionsInvalidated(IReadOnlyList<int>? libraryIds)
    {
        if (libraryIds is not null && _libraryIds.Count > 0 && !libraryIds.Any(_libraryIds.Contains))
        {
            return;
        }

        if (_isVisible)
        {
            RefreshSuggestions();
        }
        else
        {
            _dirty = true;
        }
    }

    /// <summary>Nach jedem Laden der Karte: Zähler und (je nach Bereich) Vorschläge neu.</summary>
    public Task RefreshSuggestions()
    {
        if (_suggestionContext is null)
        {
            return Task.CompletedTask;
        }

        _lastSuggestionLoad = LoadSuggestionsAsync();
        return _lastSuggestionLoad;
    }

    private async Task LoadSuggestionsAsync()
    {
        var context = _suggestionContext!;
        var version = ++_suggestionVersion;
        IsSuggestionsLoading = true;
        // Touren verbundener Apps (Stufe 7): höchstens alle 10 Minuten, die App darf dafür im Hintergrund starten; neue Spuren
        // melden sich mit GpsTracksChangedMessage und laden die Vorschläge neu
        context.ExternalTracks?.Request(mayStartApps: true);
        try
        {
            var scope = await context.Scope.GetCurrentAsync().ConfigureAwait(true);
            _suggestionScope = scope;
            var counts = await context.Suggestions.CountAsync(scope).ConfigureAwait(true);
            if (version != _suggestionVersion)
            {
                return;
            }

            EstimatedCount = counts.Estimated;
            ActivityCount = counts.Activities;
            var unlocatedTask = context.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Unlocated);
            var inaccurateTask = context.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Inaccurate);
            await Task.WhenAll(unlocatedTask, inaccurateTask).ConfigureAwait(true);
            if (version != _suggestionVersion)
            {
                return;
            }

            _sets[GeoSuggestionListKind.Unlocated] = unlocatedTask.Result;
            _sets[GeoSuggestionListKind.Inaccurate] = inaccurateTask.Result;
            UnlocatedSuggestionCount = unlocatedTask.Result.WithSuggestion;
            PublishSummary();
            if (_keepRowsOnce)
            {
                _keepRowsOnce = false; // nach dem Übernehmen: Zeilen mit „übernommen“ stehen lassen
                UpdateCountsInRows();
                return;
            }

            BuildRows(keepSelection: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Vorschläge nicht geladen");
        }
        finally
        {
            if (version == _suggestionVersion)
            {
                IsSuggestionsLoading = false;
            }
        }
    }

    private void PublishSummary()
    {
        try
        {
            var suggestions = _sets.Values.Sum(s => s.WithSuggestion);
            _messenger.Send(new GeoPlacesSummaryMessage(EstimatedCount, suggestions, ActivityCount));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orte: Zusammenfassung nicht gesendet");
        }
    }

    // ---- Bereich und Filter --------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowUnlocatedMode() => SideMode = PlacesSideMode.Unlocated;

    [RelayCommand]
    private void ShowInaccurateMode() => SwitchMode(PlacesSideMode.Inaccurate);

    [RelayCommand]
    private void ShowUnlocatedSuggestions() => SwitchMode(PlacesSideMode.UnlocatedSuggestions);

    private void SwitchMode(PlacesSideMode mode)
    {
        if (SideMode == mode)
        {
            return;
        }

        SideMode = mode;
        SelectedRow = null;
        BuildRows(keepSelection: false);
    }

    [RelayCommand]
    private void SelectFilter(SuggestionFilterChip? chip)
    {
        if (chip is null)
        {
            return;
        }

        CurrentSuggestionFilter = chip.Filter;
        foreach (var c in FilterChips)
        {
            c.IsSelected = ReferenceEquals(c, chip);
        }

        BuildRows(keepSelection: true);
    }

    private GeoSuggestionSet? CurrentSet => SideMode switch
    {
        PlacesSideMode.Inaccurate => _sets.GetValueOrDefault(GeoSuggestionListKind.Inaccurate),
        PlacesSideMode.UnlocatedSuggestions => _sets.GetValueOrDefault(GeoSuggestionListKind.Unlocated),
        _ => null,
    };

    /// <summary>Zeilen aus dem aktuellen Satz und Filter bauen (Gruppen je Tag, neueste zuerst).</summary>
    private void BuildRows(bool keepSelection)
    {
        var selectedId = keepSelection ? SelectedRow?.FileId : null;
        if (CurrentSet is not { } set)
        {
            SuggestionRows = [];
            SuggestionSummary = string.Empty;
            Overlay = null;
            return;
        }

        var items = SideMode == PlacesSideMode.UnlocatedSuggestions
            ? set.Items.Where(i => i.Utc is not null).ToList() // ohne Aufnahmezeit gibt es nie einen Vorschlag
            : set.Items.ToList();
        var counts = new Dictionary<SuggestionFilter, int>
        {
            [SuggestionFilter.All] = items.Count,
            [SuggestionFilter.Track] = items.Count(i => i.Level == GeoSuggestionLevel.Track),
            [SuggestionFilter.Sure] = items.Count(i => i.Level == GeoSuggestionLevel.Sure),
            [SuggestionFilter.Likely] = items.Count(i => i.Level == GeoSuggestionLevel.Likely),
            [SuggestionFilter.None] = items.Count(i => i.Level == GeoSuggestionLevel.None),
        };
        foreach (var chip in FilterChips)
        {
            chip.Count = counts[chip.Filter];
        }

        SuggestionSummary = PlacesSuggestionText.Summary(SideMode, items.Count, counts[SuggestionFilter.Track], counts[SuggestionFilter.Sure], counts[SuggestionFilter.Likely], counts[SuggestionFilter.None]);
        var rows = new List<object>();
        SuggestionItemRow? selected = null;
        foreach (var (key, title, groupItems) in PlacesSuggestionText.GroupByDay(items.Where(i => PlacesSuggestionText.Matches(CurrentSuggestionFilter, i.Level))))
        {
            var group = new SuggestionGroupRow(key, title);
            rows.Add(group);
            foreach (var item in groupItems)
            {
                var row = new SuggestionItemRow(item, group, DisplayOffset(item)) { Image = _images.TryGet(item.FileId) };
                group.Items.Add(row);
                rows.Add(row);
                if (item.FileId == selectedId)
                {
                    selected = row;
                }
            }

            UpdateGroup(group);
        }

        SuggestionRows = rows;
        UpdateTrackAndSure();
        SelectRowCore(selected ?? rows.OfType<SuggestionItemRow>().FirstOrDefault(), fit: selected is null);
        RequestRowImages(rows.OfType<SuggestionItemRow>().Select(r => r.FileId).ToList());
    }

    private static int DisplayOffset(GeoSuggestionItem item)
        => item.LocalTime is { } local && item.Utc is { } utc
            ? (int)Math.Round((local - DateTimeOffset.FromUnixTimeSeconds(utc).UtcDateTime).TotalMinutes)
            : 0;

    private void UpdateGroup(SuggestionGroupRow group)
    {
        var place = group.Items.Select(r => r.Item.Track?.Name).FirstOrDefault(n => n is not null)
                    ?? group.Items.Where(r => r.HasSuggestion).Select(r => SafeNearest(r.Latitude!.Value, r.Longitude!.Value)?.Name).FirstOrDefault(n => n is not null)
                    ?? group.Items.Where(r => r.HasStored).Select(r => SafeNearest(r.Item.StoredLatitude!.Value, r.Item.StoredLongitude!.Value)?.Name).FirstOrDefault(n => n is not null);
        group.OpenSuggestions = group.Items.Count(r => r.CanApply);
        group.Detail = PlacesSuggestionText.GroupDetail(place, group.Items.Count, group.Items.Count(r => r.HasSuggestion));
    }

    private void UpdateCountsInRows()
    {
        foreach (var group in SuggestionRows.OfType<SuggestionGroupRow>())
        {
            group.OpenSuggestions = group.Items.Count(r => r.CanApply);
        }

        UpdateTrackAndSure();
        OnPropertyChanged(nameof(ApplyGroupText));
        OnPropertyChanged(nameof(CanApplyGroup));
        ApplyGroupCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTrackAndSure()
        => TrackAndSureOpen = SuggestionRows.OfType<SuggestionItemRow>().Count(r => r.CanApply && r.Level is GeoSuggestionLevel.Track or GeoSuggestionLevel.Sure);

    private void RequestRowImages(IReadOnlyList<int> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        _ = RequestRowImagesAsync(fileIds.Take(PlacesViewModel.LightboxLimit).ToList());
    }

    private async Task RequestRowImagesAsync(IReadOnlyList<int> fileIds)
    {
        try
        {
            var infos = await EnsureInfosAsync(fileIds).ConfigureAwait(true);
            _images.Request(infos.Where(i => _images.TryGet(i.FileId) is null).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orte: Vorschaubilder der Vorschläge nicht angefordert");
        }
    }

    /// <summary>Angekommene Vorschaubilder in die Zeilen übernehmen (aus <c>ApplyImages</c>).</summary>
    private void ApplyRowImages(IReadOnlyList<int> fileIds)
    {
        if (SuggestionRows.Count == 0)
        {
            return;
        }

        var ids = fileIds.ToHashSet();
        foreach (var row in SuggestionRows.OfType<SuggestionItemRow>())
        {
            if (row.Image is null && ids.Contains(row.FileId))
            {
                row.Image = _images.TryGet(row.FileId);
            }
        }
    }

    // ---- Auswahl und Karte ---------------------------------------------------------------------------------------------

    /// <summary>Zeile auswählen: Karte zeigt den Ausflug mit Spur und rückt den Vorschlag ins Bild.</summary>
    public void SelectRow(SuggestionItemRow? row) => SelectRowCore(row, fit: true);

    private void SelectRowCore(SuggestionItemRow? row, bool fit)
    {
        if (SelectedRow is { } old)
        {
            old.IsSelected = false;
        }

        SelectedRow = row;
        if (row is not null)
        {
            row.IsSelected = true;
        }

        _ = BuildOverlayAsync(fit);
    }

    /// <summary>Klick auf einen Punkt der Überlagerung.</summary>
    public void SelectRowByFileId(int fileId)
    {
        if (SuggestionRows.OfType<SuggestionItemRow>().FirstOrDefault(r => r.FileId == fileId) is { } row)
        {
            SelectRowCore(row, fit: false);
        }
    }

    /// <summary>Vorschlag auf der Karte gezogen: neuer Punkt (wird als „manuell“ gespeichert).</summary>
    public void MoveSuggestion(int fileId, double latitude, double longitude)
    {
        if (SuggestionRows.OfType<SuggestionItemRow>().FirstOrDefault(r => r.FileId == fileId) is not { IsApplied: false } row
            || !new Services.Metadata.GeoCoordinate(latitude, longitude).IsValid)
        {
            return;
        }

        row.Latitude = latitude;
        row.Longitude = longitude;
        row.IsMoved = true;
        UpdateGroup(row.Group);
        UpdateTrackAndSure();
        _ = BuildOverlayAsync(fit: false);
    }

    private async Task BuildOverlayAsync(bool fit)
    {
        var version = ++_overlayVersion;
        var row = SelectedRow;
        if (row is null || _suggestionContext is not { } context)
        {
            Overlay = null;
            return;
        }

        var corrections = new List<MapCorrection>();
        var bounds = new List<Point>();
        foreach (var r in row.Group.Items.Where(r => !r.IsApplied))
        {
            Point? stored = r.Item.HasStored ? Map.MapOverlay.ToUnit(r.Item.StoredLatitude!.Value, r.Item.StoredLongitude!.Value) : null;
            Point? suggested = r.HasSuggestion ? Map.MapOverlay.ToUnit(r.Latitude!.Value, r.Longitude!.Value) : null;
            if (stored is null && suggested is null)
            {
                continue;
            }

            corrections.Add(new MapCorrection(
                r.FileId,
                stored,
                suggested,
                ReferenceEquals(r, row),
                $"{r.Title}\nGespeicherte Position (geschätzt): {Services.Places.Accuracy.GeoAccuracyRules.Describe(r.Item.Reason)}",
                $"Vorschlag für {r.Title}\nQuelle: {r.SourceText}" + (r.Item.CorrectionMeters is { } m ? $" · {GeoMath.FormatDistance(m)} vom gespeicherten Punkt" : string.Empty) + (ReferenceEquals(r, row) ? " · ziehen zum Verschieben" : string.Empty)));
            if (ReferenceEquals(r, row))
            {
                bounds.AddRange(new[] { stored, suggested }.Where(p => p is not null).Select(p => p!.Value));
            }
        }

        var anchors = new List<Point>();
        if (row.Item.Suggestion is { } s)
        {
            foreach (var a in new[] { s.AnchorBefore, s.AnchorAfter })
            {
                if (a is { } anchor)
                {
                    anchors.Add(Map.MapOverlay.ToUnit(anchor.Latitude, anchor.Longitude));
                }
            }
        }

        var track = new List<Point>();
        if (row.Item.Track is { } info)
        {
            try
            {
                var points = await context.Tracks.LoadPointsAsync(info.Id).ConfigureAwait(true);
                var step = Math.Max(1, points.Length / MaxOverlayTrackPoints);
                for (var i = 0; i < points.Length; i += step)
                {
                    track.Add(Map.MapOverlay.ToUnit(points[i].Latitude, points[i].Longitude));
                }

                if (points.Length > 0 && (points.Length - 1) % step != 0)
                {
                    track.Add(Map.MapOverlay.ToUnit(points[^1].Latitude, points[^1].Longitude));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Orte: Spur für die Karte nicht geladen");
            }
        }

        if (version != _overlayVersion)
        {
            return;
        }

        Overlay = new MapOverlay(track, anchors, corrections);
        if (fit && bounds.Count > 0)
        {
            FitUnitPoints(bounds);
        }
    }

    /// <summary>Ausschnitt auf die Punkte (mit Rand; ein Punkt allein → Zoom 14).</summary>
    private void FitUnitPoints(IReadOnlyList<Point> points)
    {
        var lats = points.Select(p => Services.Places.Clustering.MercatorProjection.YToLatitude(p.Y)).ToList();
        var lons = points.Select(p => Services.Places.Clustering.MercatorProjection.XToLongitude(p.X)).ToList();
        if (lats.Max() - lats.Min() < 0.002 && lons.Max() - lons.Min() < 0.002)
        {
            Viewport.CenterOn(lats.Average(), lons.Average(), 14);
            return;
        }

        Viewport.FitBounds(new GeoBounds(lats.Min(), lons.Min(), lats.Max(), lons.Max()));
    }

    // ---- Übernehmen, Ablehnen, Rückgängig ------------------------------------------------------------------------------

    [RelayCommand]
    private Task ApplyRowAsync(SuggestionItemRow? row)
        => row is { CanApply: true } ? ApplyRowsAsync([row], "1 Ort übernommen") : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanApplyGroup))]
    private Task ApplyGroupAsync(SuggestionGroupRow? group)
    {
        group ??= SelectedRow?.Group;
        var rows = group?.Items.Where(r => r.CanApply).ToList() ?? [];
        return rows.Count == 0 ? Task.CompletedTask : ApplyRowsAsync(rows, $"Ausflug: {PlacesText.N(rows.Count)} {(rows.Count == 1 ? "Ort" : "Orte")} übernommen");
    }

    [RelayCommand(CanExecute = nameof(CanApplyTrackAndSure))]
    private Task ApplyTrackAndSureAsync()
    {
        var rows = SuggestionRows.OfType<SuggestionItemRow>().Where(r => r.CanApply && r.Level is GeoSuggestionLevel.Track or GeoSuggestionLevel.Sure).ToList();
        return rows.Count == 0 ? Task.CompletedTask : ApplyRowsAsync(rows, $"{PlacesText.N(rows.Count)} {(rows.Count == 1 ? "Ort" : "Orte")} übernommen (Garmin-Spur und sicher)");
    }

    private bool CanApplyTrackAndSure() => TrackAndSureOpen > 0;

    /// <summary>Zeilen übernehmen (gemeinsamer Weg für Zeile, Ausflug, „Garmin + sichere“ und den Rauchtest).</summary>
    public async Task ApplyRowsAsync(IReadOnlyList<SuggestionItemRow> rows, string label)
    {
        if (_suggestionContext is not { } context || rows.Count == 0 || IsBusy)
        {
            return;
        }

        var items = rows
            .Where(r => r.HasSuggestion && _suggestionScope.Contains(r.Item.LibraryId)) // Bibliotheks-Trennung: nur Dateien des Bereichs
            .Select(r => new GeoSuggestionApply(r.FileId, r.Latitude!.Value, r.Longitude!.Value, r.IsMoved ? GeoSource.Manual : r.Item.SourceOnApply))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _ownChangeUntil = DateTime.UtcNow.AddSeconds(3);
            var result = await context.Suggestions.ApplyAsync(items, SaveToFile).ConfigureAwait(true);
            _ownChangeUntil = DateTime.UtcNow.AddSeconds(2);
            var done = (result.AppliedFileIds ?? items.Select(i => i.FileId).ToList()).ToHashSet(); // fehlgeschlagene bleiben offen
            var failedNames = result.Errors;
            foreach (var row in rows.Where(r => done.Contains(r.FileId)))
            {
                row.IsApplied = true;
                row.BatchId = result.BatchId > 0 ? result.BatchId : null;
            }

            if (result.Failed > 0)
            {
                ShowMessage($"{PlacesText.N(result.Written + result.CatalogOnly)} übernommen, {PlacesText.N(result.Failed)} fehlgeschlagen — {failedNames.FirstOrDefault()}", isError: true);
            }

            _logger.LogInformation("Orte: {Count} Vorschläge übernommen ({Written} in Datei, {CatalogOnly} nur Katalog, {Failed} Fehler)",
                items.Count, result.Written, result.CatalogOnly, result.Failed);
            if (result.BatchId > 0)
            {
                ShowToast($"{label} — {(SaveToFile ? "ins Foto geschrieben" : "nur im Katalog")}", result.BatchId, null);
            }

            UpdateCountsInRows();
            _keepRowsOnce = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Vorschläge nicht übernommen");
            ShowMessage("Übernehmen fehlgeschlagen: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }

        await BuildOverlayAsync(fit: false).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true); // Karte und Zähler; die Zeilen bleiben einmal stehen
    }

    [RelayCommand]
    private async Task RejectRowAsync(SuggestionItemRow? row)
    {
        if (row is null || _suggestionContext is not { } context)
        {
            return;
        }

        try
        {
            await context.Suggestions.SetRejectedAsync([row.FileId], rejected: true).ConfigureAwait(true);
            ShowToast("Vorschlag abgelehnt — das Foto bleibt, wie es ist", null, [row.FileId]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Vorschlag nicht abgelehnt");
            ShowMessage("Ablehnen fehlgeschlagen: " + ex.Message, isError: true);
        }
    }

    /// <summary>Zeile „übernommen“: Rückgängig über ihren Batch.</summary>
    [RelayCommand]
    private Task UndoToastAsync() => UndoCoreAsync();

    private async Task UndoCoreAsync()
    {
        if (_suggestionContext is not { } context)
        {
            return;
        }

        var batch = _toastBatchId;
        var rejected = _toastRejected;
        HideToast();
        try
        {
            if (batch is { } id)
            {
                var result = await context.Undo.UndoAsync(id).ConfigureAwait(true);
                if (result is { Failed: > 0 })
                {
                    ShowMessage($"Rückgängig: {PlacesText.N(result.Restored)} zurückgesetzt, {PlacesText.N(result.Failed)} nicht möglich — {result.Errors.FirstOrDefault()}", isError: true);
                }
            }
            else if (rejected is { Count: > 0 })
            {
                await context.Suggestions.SetRejectedAsync(rejected, rejected: false).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Rückgängig fehlgeschlagen");
            ShowMessage("Rückgängig fehlgeschlagen: " + ex.Message, isError: true);
        }

        _keepRowsOnce = false;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissToast() => HideToast();

    /// <summary>Zeile „übernommen“: ihren Batch zurücknehmen (alle Fotos, die mit ihr übernommen wurden).</summary>
    [RelayCommand]
    private async Task UndoRowAsync(SuggestionItemRow? row)
    {
        if (row?.BatchId is not { } batch || _suggestionContext is null)
        {
            return;
        }

        _toastBatchId = batch;
        _toastRejected = null;
        await UndoCoreAsync().ConfigureAwait(true);
    }

    /// <summary>Doppelklick auf eine Zeile: Lightbox mit den Fotos der Liste.</summary>
    public Task OpenSuggestionLightboxAsync(SuggestionItemRow row)
    {
        var ids = SuggestionRows.OfType<SuggestionItemRow>().Select(r => r.FileId).Take(LightboxLimit).ToList();
        return OpenLightboxAsync(ids, row.FileId);
    }

    private void ShowToast(string text, int? batchId, IReadOnlyList<int>? rejected)
    {
        _toastTimer?.Cancel();
        _toastBatchId = batchId;
        _toastRejected = rejected;
        ToastText = text;
        var cts = new CancellationTokenSource();
        _toastTimer = cts;
        _ = HideToastLaterAsync(cts.Token);
    }

    private async Task HideToastLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(ToastDuration, ct).ConfigureAwait(true);
            HideToast();
        }
        catch (OperationCanceledException)
        {
            // neuer Hinweis
        }
    }

    private void HideToast()
    {
        _toastTimer?.Cancel();
        _toastTimer = null;
        ToastText = null;
    }

    /// <summary>Jüngster Rückgängig-Batch des Hinweises (Tests, Rauchtest).</summary>
    public int? ToastBatchId => _toastBatchId;

    /// <summary>„Ort setzen…“ für ein Foto ohne Vorschlag (Dialog der Galerie).</summary>
    [RelayCommand]
    private async Task SetPlaceForRowAsync(SuggestionItemRow? row)
    {
        if (row is null || _suggestionContext?.PlaceActions is not { } actions)
        {
            return;
        }

        try
        {
            var result = await actions.SetForFilesAsync([row.FileId], _libraryIds, row.Item.MediaType == 1, Application.Current?.MainWindow).ConfigureAwait(true);
            if (result is not null)
            {
                ShowMessage(result.Text, isError: result.Result.Failed > 0);
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Ort setzen fehlgeschlagen");
            ShowMessage("Ort setzen fehlgeschlagen: " + ex.Message, isError: true);
        }
    }

    /// <summary>Galerie › Tour-Leiste „Auf der Karte“ (Mockup 24): Seite „Orte“ mit diesem Gebiet.</summary>
    public async Task ShowAreaAsync(GeoBounds bounds)
    {
        _navigation.NavigateTo(NavigationTags.Places);
        await _lastLoad.ConfigureAwait(true);
        Viewport.FitBounds(bounds);
    }

    /// <summary>Lightbox › „Auf der Karte zeigen“: Seite „Orte“ mit dem Foto im passenden Bereich.</summary>
    public async Task ShowFileSuggestionAsync(int fileId)
    {
        _navigation.NavigateTo(NavigationTags.Places);
        await _lastLoad.ConfigureAwait(true);
        await RefreshSuggestions().ConfigureAwait(true);
        var mode = _sets.GetValueOrDefault(GeoSuggestionListKind.Inaccurate)?.Items.Any(i => i.FileId == fileId) == true
            ? PlacesSideMode.Inaccurate
            : _sets.GetValueOrDefault(GeoSuggestionListKind.Unlocated)?.Items.Any(i => i.FileId == fileId) == true
                ? PlacesSideMode.UnlocatedSuggestions
                : (PlacesSideMode?)null;
        if (mode is not { } m)
        {
            return;
        }

        SideMode = m;
        if (CurrentSuggestionFilter != SuggestionFilter.All)
        {
            SelectFilter(FilterChips[0]);
        }
        else
        {
            BuildRows(keepSelection: false);
        }

        if (SuggestionRows.OfType<SuggestionItemRow>().FirstOrDefault(r => r.FileId == fileId) is { } row)
        {
            SelectRowCore(row, fit: true);
        }
    }
}
