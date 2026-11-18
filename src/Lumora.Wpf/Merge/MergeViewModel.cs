using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Shell;

namespace Lumora.Merge;

/// <summary>Was das Fenster nach dem Speichern bzw. für den Filmstreifen braucht (Galerie, Lightbox, Papierkorb, Explorer).</summary>
public interface IMergeResultActions
{
    /// <summary>Ergebnis und Quellen in der Lightbox (Ergebnis zuerst).</summary>
    Task OpenResultAsync(int resultFileId, Window? owner);

    /// <summary>Galerie mit dem Ergebnis als Bedingung.</summary>
    Task ShowInGalleryAsync(int resultFileId, string name);

    /// <summary>CreateMerged-Batch zurücknehmen (Ergebnis in den Papierkorb, Verknüpfungen weg); liefert die Meldung.</summary>
    Task<string> UndoAsync(int batchId);

    /// <summary>„Danach verbessern…“: Lightbox mit dem Ergebnis und der Leiste „Verbessern“.</summary>
    Task EnhanceAsync(int resultFileId, Window? owner);

    /// <summary>„Im Explorer zeigen“ (Fehlertext oder null); in der versteckten Sitzung gesperrt.</summary>
    string? ShowInExplorer(string path);

    bool IsExplorerBlocked { get; }
}

/// <summary>Vorschaubilder des Filmstreifens (aus dem Cache der Galerie; Platzhalter nie herunterladen).</summary>
public interface IMergeThumbnailLoader
{
    Task LoadAsync(IReadOnlyList<MergeSourceItem> items, CancellationToken ct);
}

/// <summary>Phase des Fensters.</summary>
public enum MergePhase
{
    /// <summary>Verfahren, Vorschau, Optionen.</summary>
    Setup = 0,

    /// <summary>Speichern läuft (Schrittliste).</summary>
    Saving = 1,

    /// <summary>Fertig-Ansicht.</summary>
    Done = 2,
}

/// <summary>
/// Fenster „Zusammenfügen“ (ADR-0078 Punkt 8, Mockup 28 Ansichten 5 und 11; Stufe 1 Teil B): links die Verfahren (nur Panorama
/// gebaut) mit „Warum dieser Vorschlag“, Mitte die Bühne (Vorschau mit Zuschnitt-Rahmen und schraffierten Lücken, Fortschritt),
/// rechts die Optionen (Aufnahmeart als Kacheln, Kugel als Streifen oder 2 : 1, Zuschnitt, Belichtung, „Farbe kräftiger“ 0–150 %,
/// Größe), unten der Filmstreifen der Quellen. Speichern mit Schätzung, Schrittliste, „Im Hintergrund weiterrechnen“ und
/// Fertig-Ansicht. Rechnet nur über <see cref="IMergeService"/> / <see cref="IMergePreviewSession"/> (Teil A); Texte aus
/// <see cref="MergeTexts"/>. Vorrang hält die Sitzung nur, solange gerechnet wird.
/// </summary>
public sealed partial class MergeViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Haken im Filmstreifen: kurz warten, bis mehrere Klicks durch sind, dann neu ausrichten.</summary>
    internal static TimeSpan SourceDebounce { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>Regler „Farbe kräftiger“: nach dem letzten Schritt neu tonen.</summary>
    internal static TimeSpan BoostDebounce { get; set; } = TimeSpan.FromMilliseconds(120);

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly IMergeService _service;
    private readonly MergeStart _start;
    private readonly IDialogService? _dialogs;
    private readonly IMergeThumbnailLoader? _thumbnails;
    private readonly IMergeResultActions? _results;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _sourceTimer;
    private readonly DispatcherTimer _boostTimer;
    private readonly CancellationTokenSource _life = new();
    private MergeSettings _settings = MergeSettings.Default;
    private MergeAnalysis? _analysis;
    private IMergePreviewSession? _session;
    private bool _allowOnlineOnly;
    private bool _loaded;
    private bool _suppress;
    private int _sessionVersion;
    private MergeOptions? _wanted;
    private MergeOptions? _applied;
    private int _updatingFor = -1;
    private bool _ready;
    private bool _selectionDirty;
    private int _disposed;
    private int _cameraCount;

    public MergeViewModel(
        IMergeService service,
        MergeStart start,
        IDialogService? dialogs = null,
        IMergeThumbnailLoader? thumbnails = null,
        IMergeResultActions? results = null,
        ILogger<MergeViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(start);
        _service = service;
        _start = start;
        _dialogs = dialogs;
        _thumbnails = thumbnails;
        _results = results;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sourceTimer = new DispatcherTimer(SourceDebounce, DispatcherPriority.Background, (_, _) => { _sourceTimer!.Stop(); _ = RestartPreviewAsync(); }, _dispatcher);
        _sourceTimer.Stop();
        _boostTimer = new DispatcherTimer(BoostDebounce, DispatcherPriority.Background, (_, _) => { _boostTimer!.Stop(); RequestUpdate(); _ = RememberBoostAsync(); }, _dispatcher);
        _boostTimer.Stop();
        foreach (var method in Enum.GetValues<MergeMethod>())
        {
            Methods.Add(new MergeMethodItem(method, MergeTexts.Method(method)) { IsSelected = method == MergeMethod.Panorama, FitText = method == MergeMethod.Panorama ? "…" : "kommt später" });
        }

        foreach (var p in new[] { PanoramaProjection.Auto, PanoramaProjection.Spherical360, PanoramaProjection.Cylindrical, PanoramaProjection.Perspective, PanoramaProjection.Vertical, PanoramaProjection.ShiftOnly })
        {
            Projections.Add(new MergeProjectionItem(p) { IsSelected = p == PanoramaProjection.Auto });
        }

        Sources.CollectionChanged += (_, _) => UpdateSourceSummary();
    }

    /// <summary>Fenster schließen (Verwerfen, Im Hintergrund weiterrechnen, Fertig).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Fenster für Dialoge und Lightbox (vom Fenster gesetzt).</summary>
    public Func<Window?>? Owner { get; set; }

    public MergeStart Start => _start;

    public ObservableCollection<MergeMethodItem> Methods { get; } = [];

    public ObservableCollection<string> WhyLines { get; } = [];

    public ObservableCollection<MergeSourceItem> Sources { get; } = [];

    public ObservableCollection<MergeNoteItem> Notes { get; } = [];

    public ObservableCollection<MergeProjectionItem> Projections { get; } = [];

    public ObservableCollection<MergeSizeItem> Sizes { get; } = [];

    public ObservableCollection<MergeStepItem> Steps { get; } = [];

    public ObservableCollection<MergeInfoRow> DoneRows { get; } = [];

    // ------------------------------------------------------------------ Zustand

    [ObservableProperty] private string _title = "Zusammenfügen · Panorama";

    /// <summary>Kasten oben links: „25 Fotos · Sa, 01.08.2020 · 27 s“ und Kamera.</summary>
    [ObservableProperty] private string _sourceInfo = string.Empty;

    [ObservableProperty] private string _sourceInfoDetail = string.Empty;

    [ObservableProperty] private string _sourceSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsSaving), nameof(IsDone), nameof(OptionsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    private MergePhase _phase;

    public bool IsSetup => Phase == MergePhase.Setup;

    public bool IsSaving => Phase == MergePhase.Saving;

    public bool IsDone => Phase == MergePhase.Done;

    /// <summary>Optionen und Filmstreifen bedienbar (nicht beim Speichern, nicht gesperrt).</summary>
    public bool OptionsEnabled => Phase == MergePhase.Setup && !IsLocked;

    /// <summary>Gesperrt (gemischte Bibliotheken, OpenCV fehlt).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isLocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isComputing;

    [ObservableProperty] private string _progressText = string.Empty;

    [ObservableProperty] private double _progressFraction;

    /// <summary>Text auf der leeren Bühne („Mindestens 2 Fotos anhaken“, Fehler).</summary>
    [ObservableProperty] private string? _stageMessage = "Vorschau wird vorbereitet…";

    [ObservableProperty] private string _stageLabel = string.Empty;

    /// <summary>Bild der Bühne: ungeschnittenes Panorama (mit Rahmen) bzw. die Vorschau selbst.</summary>
    [ObservableProperty] private BitmapSource? _stageImage;

    /// <summary>Getönte Vorschau im Zuschnitt-Rahmen (nur mit Rahmen).</summary>
    [ObservableProperty] private BitmapSource? _stageCroppedImage;

    [ObservableProperty] private Int32Rect _stageCrop;

    /// <summary>Breite des Rahmenbilds in Pixeln (Maßstab der Ebene).</summary>
    [ObservableProperty] private int _stageFrameWidth;

    [ObservableProperty] private bool _hasStageFrame;

    /// <summary>Rahmen und Schraffur zeigen (Schalter „Zuschnitt“ unter der Bühne).</summary>
    [ObservableProperty] private bool _showFrame = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private MergePreview? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSphere))]
    private PanoramaProjection _effectiveProjection = PanoramaProjection.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSphere))]
    private PanoramaProjection _selectedProjection = PanoramaProjection.Auto;

    /// <summary>Wahl „Horizontstreifen | 2 : 1“ zeigen (Kugel 360° gewählt bzw. vorgeschlagen).</summary>
    public bool IsSphere => SelectedProjection == PanoramaProjection.Spherical360
                            || (SelectedProjection == PanoramaProjection.Auto && EffectiveProjection == PanoramaProjection.Spherical360);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSphereBand), nameof(IsSphereFull))]
    private MergeSphereForm _sphereForm = MergeSphereForm.Band;

    public bool IsSphereBand
    {
        get => SphereForm == MergeSphereForm.Band;
        set
        {
            if (value)
            {
                SphereForm = MergeSphereForm.Band;
            }
        }
    }

    public bool IsSphereFull
    {
        get => SphereForm == MergeSphereForm.Full;
        set
        {
            if (value)
            {
                SphereForm = MergeSphereForm.Full;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCropStraight), nameof(IsCropFill), nameof(IsCropNone))]
    private MergeCrop _crop = MergeCrop.Straight;

    public bool IsCropStraight
    {
        get => Crop == MergeCrop.Straight;
        set
        {
            if (value)
            {
                Crop = MergeCrop.Straight;
            }
        }
    }

    public bool IsCropFill
    {
        get => Crop == MergeCrop.Fill;
        set
        {
            if (value)
            {
                Crop = MergeCrop.Fill;
            }
        }
    }

    public bool IsCropNone
    {
        get => Crop == MergeCrop.None;
        set
        {
            if (value)
            {
                Crop = MergeCrop.None;
            }
        }
    }

    [ObservableProperty] private bool _exposure = true;

    /// <summary>„Farbe kräftiger“ in Prozent: 0 = aus, 100 = Standard, bis 150 (Herbert 2026-11-18).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBoostText), nameof(IsColorBoostOff))]
    private int _colorBoost = MergeLimits.ColorBoostDefault;

    public string ColorBoostText => MergeTexts.ColorBoost(ColorBoost);

    public bool IsColorBoostOff => ColorBoost <= MergeLimits.ColorBoostOff;

    public int ColorBoostMax => MergeLimits.ColorBoostMax;

    // ------------------------------------------------------------------ Fuß

    [ObservableProperty] private string _targetName = string.Empty;

    [ObservableProperty] private string _placeText = "neben dem ersten Einzelbild";

    public bool HasAlbum => _start.AlbumId is not null;

    public string AlbumText => $"auch ins Album „{_start.AlbumName ?? "?"}“";

    [ObservableProperty] private bool _alsoAlbum = true;

    [ObservableProperty] private bool _copyTags = true;

    [ObservableProperty] private bool _copyRating = true;

    [ObservableProperty] private string _estimateText = string.Empty;

    [ObservableProperty] private string _estimateDetail = string.Empty;

    // ------------------------------------------------------------------ Laden

    /// <summary>Beim Öffnen: Verfügbarkeit, Einstellungen, Analyse der Auswahl, OneDrive-Rückfrage, erste Vorschau ab 2 Haken.</summary>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            var availability = await _service.GetAvailabilityAsync(_life.Token).ConfigureAwait(true);
            if (!availability.Available)
            {
                Lock(availability.Reason ?? "„Zusammenfügen“ ist nicht verfügbar.");
                return;
            }

            _settings = await _service.LoadSettingsAsync(_life.Token).ConfigureAwait(true);
            ApplyStartOptions();
            var analysis = await _service.AnalyzeAsync(_start.FileIds, _life.Token).ConfigureAwait(true);
            _analysis = analysis;
            FillSources(analysis);
            UpdateMethods(analysis);
            UpdateWhy();
            ShowAnalysisNotes(analysis);
            UpdateTarget();
            if (analysis.MixedLibraries)
            {
                Lock(MergeTexts.BlockedMixedLibraries);
                return;
            }

            if (_thumbnails is not null)
            {
                _ = LoadThumbnailsAsync();
            }

            if (analysis.OnlineOnlyCount > 0 && !await AskOnlineOnlyAsync(analysis).ConfigureAwait(true))
            {
                return;
            }

            await RestartPreviewAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is MergeException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Zusammenfügen: Auswahl nicht lesbar");
            StageMessage = ex.Message;
        }
    }

    private void ApplyStartOptions()
    {
        _suppress = true;
        try
        {
            var o = _start.Options;
            ColorBoost = Math.Clamp(o?.ColorBoost ?? _settings.ColorBoost, MergeLimits.ColorBoostOff, MergeLimits.ColorBoostMax);
            SphereForm = o?.SphereForm ?? _settings.SphereForm;
            Crop = o?.Crop ?? MergeCrop.Straight;
            Exposure = o?.Exposure ?? true;
            SelectProjection(o?.Projection ?? PanoramaProjection.Auto);
            CopyTags = _settings.CopyCommonTags;
            CopyRating = _settings.CopyRating;
        }
        finally
        {
            _suppress = false;
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        try
        {
            await _thumbnails!.LoadAsync(Sources.ToList(), _life.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Zusammenfügen: Vorschaubilder des Filmstreifens nicht geladen");
        }
    }

    private void FillSources(MergeAnalysis analysis)
    {
        _suppress = true;
        try
        {
            Sources.Clear();
            var single = analysis.Neighbors.Count > 0;
            var reference = analysis.Sources.FirstOrDefault(s => s.IsUsable)?.CapturedAt;
            var all = analysis.Sources.Select(s => (Info: s, Neighbor: false))
                .Concat(analysis.Neighbors.Select(n => (Info: n, Neighbor: true)))
                .OrderBy(x => x.Info.CapturedAt ?? DateTime.MaxValue).ThenBy(x => x.Info.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var (info, neighbor) in all)
            {
                var usable = info.IsUsable || info.State == MergeSourceState.OnlineOnly;
                var item = new MergeSourceItem(info.FileId, info.Name)
                {
                    IsUsable = usable,
                    IsChecked = usable && !neighbor,
                    IsNeighbor = neighbor,
                    IsReference = single && !neighbor,
                    State = info.State,
                    CapturedAt = info.CapturedAt,
                    Mark = info.ReplacesRawFileId is not null ? "statt RAW" : MergeTexts.SourceState(info.State),
                };
                item.Label = single && !neighbor ? "gestartet"
                    : single && reference is { } r && info.CapturedAt is { } t ? Offset(t - r)
                    : info.CapturedAt is { } c ? c.ToString("HH:mm:ss", De) : string.Empty;
                item.PropertyChanged += OnSourcePropertyChanged;
                Sources.Add(item);
            }
        }
        finally
        {
            _suppress = false;
        }

        UpdateSourceSummary();
    }

    private static string Offset(TimeSpan d)
    {
        var sign = d < TimeSpan.Zero ? "−" : "+";
        var a = d.Duration();
        return string.Create(De, $"{sign}{(int)a.TotalMinutes}:{a.Seconds:00}");
    }

    private void OnSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MergeSourceItem.IsChecked) || _suppress)
        {
            return;
        }

        UpdateSourceSummary();
        UpdateMethodsForChecked();
        // Auswahl geändert: die Vorschau passt nicht mehr (Speichern gesperrt), nach kurzer Pause neu ausrichten
        _selectionDirty = true;
        SaveCommand.NotifyCanExecuteChanged();
        _sourceTimer.Stop();
        _sourceTimer.Interval = SourceDebounce;
        _sourceTimer.Start();
    }

    public IReadOnlyList<int> CheckedIds => Sources.Where(s => s.IsChecked && s.IsUsable).Select(s => s.FileId).ToList();

    public int UncheckedNeighbors => Sources.Count(s => s.IsNeighbor && !s.IsChecked && s.IsUsable);

    public bool HasUncheckedNeighbors => UncheckedNeighbors > 0;

    public string CheckAllText => string.Create(De, $"Alle {Sources.Count(s => s.IsUsable):N0} anhaken");

    private void UpdateSourceSummary()
    {
        var total = Sources.Count;
        var chosen = Sources.Count(s => s.IsChecked && s.IsUsable);
        SourceSummary = string.Create(De, $"{chosen:N0} von {total:N0} angehakt");
        OnPropertyChanged(nameof(CheckedIds));
        OnPropertyChanged(nameof(UncheckedNeighbors));
        OnPropertyChanged(nameof(HasUncheckedNeighbors));
        OnPropertyChanged(nameof(CheckAllText));
        var checkedSources = Sources.Where(s => s.IsChecked && s.IsUsable).ToList();
        if (checkedSources.Count == 0)
        {
            SourceInfo = "Keine Fotos angehakt";
            SourceInfoDetail = string.Empty;
            return;
        }

        var times = checkedSources.Where(s => s.CapturedAt is not null).Select(s => s.CapturedAt!.Value).OrderBy(t => t).ToList();
        var span = times.Count > 1 ? times[^1] - times[0] : TimeSpan.Zero;
        var when = times.Count > 0 ? times[0].ToString("ddd, dd.MM.yyyy", De) : "ohne Aufnahmezeit";
        SourceInfo = string.Create(De, $"{checkedSources.Count:N0} {(checkedSources.Count == 1 ? "Foto" : "Fotos")} · {when}") + (times.Count > 1 ? " · " + Span(span) : string.Empty);
        var cameras = _analysis?.Sources.Concat(_analysis.Neighbors).Where(s => checkedSources.Any(c => c.FileId == s.FileId)).Select(s => s.Camera).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList() ?? [];
        _cameraCount = cameras.Count;
        SourceInfoDetail = cameras.Count switch
        {
            0 => "Kamera unbekannt (kein EXIF)",
            1 => cameras[0]!,
            _ => string.Create(De, $"{cameras.Count} verschiedene Kameras"),
        };
    }

    private static string Span(TimeSpan span)
        => span.TotalSeconds < 90 ? string.Create(De, $"{Math.Round(span.TotalSeconds):0} s") : span.TotalMinutes < 90 ? string.Create(De, $"{span.TotalMinutes:0} min") : string.Create(De, $"{span.TotalHours:0.#} h");

    [RelayCommand]
    private void CheckAll()
    {
        _suppress = true;
        try
        {
            foreach (var s in Sources.Where(s => s.IsUsable))
            {
                s.IsChecked = true;
            }
        }
        finally
        {
            _suppress = false;
        }

        UpdateSourceSummary();
        UpdateMethodsForChecked();
        _ = RestartPreviewAsync();
    }

    private void UpdateMethods(MergeAnalysis analysis)
    {
        foreach (var item in Methods)
        {
            var info = analysis.Methods.FirstOrDefault(m => m.Method == item.Method);
            item.IsBuilt = info?.Built ?? false;
            item.Suits = info?.Suits ?? false;
            item.IsSuggested = item.Method == analysis.SuggestedMethod && item.IsBuilt;
            item.Reason = info is null ? string.Empty : info.Built ? (info.Reason.Split(" · ").LastOrDefault() ?? info.Reason) : StageOf(item.Method);
            item.FitText = !item.IsBuilt ? "kommt später" : item.Suits ? "passt" : "eher nicht";
        }

        UpdateMethodsForChecked();
    }

    /// <summary>Stufe laut ADR-0078 Punkt 19 („kommt in Stufe 4“).</summary>
    private static string StageOf(MergeMethod method) => method switch
    {
        MergeMethod.ExposureFusion or MergeMethod.Denoise or MergeMethod.RemovePeople or MergeMethod.LongExposure => "kommt in Stufe 4",
        MergeMethod.FocusStack or MergeMethod.Timelapse or MergeMethod.HdrPanorama => "kommt in Stufe 5",
        MergeMethod.GroupShot => "kommt in Stufe 6",
        _ => "kommt später",
    };

    /// <summary>Panorama: „eher nicht · nur 1 Foto angehakt“ unter 2 Haken (ADR-0078 Punkt 8).</summary>
    private void UpdateMethodsForChecked()
    {
        var pano = Methods.First(m => m.Method == MergeMethod.Panorama);
        if (IsLocked || _analysis is null)
        {
            return;
        }

        var n = CheckedIds.Count;
        if (n < MergeLimits.MinSources)
        {
            pano.Suits = false;
            pano.FitText = "eher nicht";
            pano.Reason = n == 1 ? "nur 1 Foto angehakt" : "keine Fotos angehakt";
        }
        else if (n > MergeLimits.MaxPanoramaSources)
        {
            pano.Suits = false;
            pano.FitText = "eher nicht";
            pano.Reason = $"höchstens {MergeLimits.MaxPanoramaSources} Fotos";
        }
        else
        {
            pano.Suits = true;
            pano.FitText = "passt";
            pano.Reason = "überlappende Fotos einer Szene";
        }
    }

    /// <summary>„Warum dieser Vorschlag“: Belege aus der Auswahl und — nach der Vorschau — aus der Ausrichtung.</summary>
    private void UpdateWhy()
    {
        WhyLines.Clear();
        var chosen = Sources.Where(s => s.IsChecked && s.IsUsable).ToList();
        if (chosen.Count < MergeLimits.MinSources)
        {
            WhyLines.Add(chosen.Count == 1 ? "Nur 1 Foto angehakt — die Zeit-Nachbarn im Filmstreifen anhaken." : "Keine Fotos angehakt.");
            return;
        }

        var times = chosen.Where(s => s.CapturedAt is not null).Select(s => s.CapturedAt!.Value).OrderBy(t => t).ToList();
        WhyLines.Add(times.Count > 1
            ? string.Create(De, $"{chosen.Count:N0} Fotos in {Span(times[^1] - times[0])}")
            : string.Create(De, $"{chosen.Count:N0} Fotos"));
        WhyLines.Add(_cameraCount switch
        {
            0 => "Kamera unbekannt (kein EXIF)",
            1 => "gleiche Kamera (EXIF)",
            _ => SourceInfoDetail + " — Farben können abweichen",
        });
        if (Preview is { } p)
        {
            var used = p.Quality.UsedImages;
            WhyLines.Add(used >= p.Quality.TotalImages
                ? "alle Fotos überlappen und sind eingebunden"
                : string.Create(De, $"{used:N0} von {p.Quality.TotalImages:N0} Fotos überlappen und sind eingebunden"));
            if (p.SuggestedProjection != PanoramaProjection.Auto)
            {
                WhyLines.Add($"Ausrichtung schlägt „{MergeTexts.Projection(p.SuggestedProjection)}“ vor");
            }

            foreach (var note in p.Notes.Where(n => n.Code is "gimbal" or "focal-estimated" or "photometric"))
            {
                WhyLines.Add(note.Text.TrimEnd('.'));
            }
        }
        else
        {
            WhyLines.Add("Überlappung prüft die Vorschau");
        }
    }

    private void ShowAnalysisNotes(MergeAnalysis analysis)
    {
        Notes.Clear();
        foreach (var note in analysis.Notes.Where(n => !n.Code.StartsWith("online-only", StringComparison.Ordinal) && n.Code != "mixed-libraries"))
        {
            Notes.Add(new MergeNoteItem(note.Text, MergeNoteKind.Info));
        }
    }

    private void Lock(string reason)
    {
        IsLocked = true;
        StageMessage = reason;
        Notes.Clear();
        Notes.Add(new MergeNoteItem(reason, MergeNoteKind.Error));
        foreach (var m in Methods.Where(m => m.IsBuilt))
        {
            m.Suits = false;
            m.FitText = "eher nicht";
            m.Reason = reason.TrimEnd('.');
        }
    }

    /// <summary>OneDrive-Platzhalter (ADR-0078 Punkt 14): herunterladen und fortfahren, ohne diese Fotos, oder abbrechen.</summary>
    private async Task<bool> AskOnlineOnlyAsync(MergeAnalysis analysis)
    {
        var text = analysis.Notes.FirstOrDefault(n => n.Code.StartsWith("online-only", StringComparison.Ordinal))?.Text
                   ?? $"{analysis.OnlineOnlyCount} Fotos liegen nur in OneDrive — zum Zusammenfügen herunterladen?";
        var choice = _dialogs is null
            ? DialogChoice.Secondary
            : await _dialogs.ChooseAsync("Fotos nur in OneDrive",
                text + "\n\nZum Zusammenfügen braucht Lumora die Pixel. Ohne Bestätigung lädt Lumora nichts herunter.",
                "Herunterladen und fortfahren", "Ohne diese Fotos").ConfigureAwait(true);
        switch (choice)
        {
            case DialogChoice.Primary:
                _allowOnlineOnly = true;
                Notes.Add(new MergeNoteItem(string.Create(De, $"{analysis.OnlineOnlyCount:N0} Fotos werden aus OneDrive geladen ({MergeTexts.Bytes(analysis.OnlineOnlyBytes)})."), MergeNoteKind.Info));
                return true;
            case DialogChoice.Secondary:
                _suppress = true;
                foreach (var s in Sources.Where(s => s.State == MergeSourceState.OnlineOnly))
                {
                    s.IsChecked = false;
                    s.IsUsable = false;
                }

                _suppress = false;
                UpdateSourceSummary();
                UpdateMethodsForChecked();
                Notes.Add(new MergeNoteItem(string.Create(De, $"{analysis.OnlineOnlyCount:N0} Fotos nur in OneDrive bleiben draußen."), MergeNoteKind.Info));
                return true;
            default:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return false;
        }
    }

    // ------------------------------------------------------------------ Vorschau

    /// <summary>Optionen aus der Oberfläche.</summary>
    public MergeOptions CurrentOptions => new(SelectedProjection, SphereForm, Crop, Exposure, ColorBoost: ColorBoost);

    /// <summary>Neue Vorschau-Sitzung (Auswahl geändert bzw. Start): richtet neu aus.</summary>
    internal async Task RestartPreviewAsync()
    {
        if (IsLocked || Volatile.Read(ref _disposed) != 0 || Phase != MergePhase.Setup)
        {
            return;
        }

        var version = ++_sessionVersion;
        var old = _session;
        _session = null;
        _applied = null;
        _wanted = null;
        _ready = false;
        _selectionDirty = false;
        _sourceTimer.Stop();
        SaveCommand.NotifyCanExecuteChanged();
        if (old is not null)
        {
            old.ProgressChanged -= OnProgress;
            _ = old.DisposeAsync().AsTask(); // bricht eine laufende Vorschau ab
        }

        UpdateWhy();
        var ids = CheckedIds;
        if (ids.Count < MergeLimits.MinSources)
        {
            ClearStage(ids.Count == 1 ? "Mindestens 2 Fotos anhaken — die Zeit-Nachbarn unten im Filmstreifen." : "Mindestens 2 Fotos anhaken.");
            return;
        }

        if (ids.Count > MergeLimits.MaxPanoramaSources)
        {
            ClearStage($"Panorama: höchstens {MergeLimits.MaxPanoramaSources} Fotos.");
            return;
        }

        var options = CurrentOptions;
        var session = _service.OpenPreviewSession(new MergeRequest(ids, MergeMethod.Panorama, options, _start.SeriesId, _allowOnlineOnly));
        _session = session;
        session.ProgressChanged += OnProgress;
        IsComputing = true;
        ProgressFraction = 0;
        ProgressText = "Fotos lesen";
        StageMessage = Preview is null ? null : StageMessage;
        try
        {
            var preview = await session.StartAsync(_life.Token).ConfigureAwait(true);
            if (version != _sessionVersion)
            {
                return;
            }

            _applied = options;
            _ready = true;
            ApplyPreview(preview);
        }
        catch (OperationCanceledException)
        {
        }
        catch (MergeException ex)
        {
            if (version == _sessionVersion)
            {
                _applied = options;
                ShowFailure(ex.Message);
            }
        }
        finally
        {
            if (version == _sessionVersion)
            {
                IsComputing = false;
            }
        }

        // während der ersten Vorschau geänderte Optionen nachziehen
        if (version == _sessionVersion && _session is not null && CurrentOptions != _applied)
        {
            RequestUpdate();
        }
    }

    private void ClearStage(string message)
    {
        Preview = null;
        StageImage = null;
        StageCroppedImage = null;
        HasStageFrame = false;
        StageMessage = message;
        StageLabel = string.Empty;
        IsComputing = false;
        EstimateText = string.Empty;
        EstimateDetail = string.Empty;
        Sizes.Clear();
        RemoveNotes(MergeNoteKind.Error);
    }

    private void ShowFailure(string message)
    {
        Preview = null;
        StageImage = null;
        StageCroppedImage = null;
        HasStageFrame = false;
        StageMessage = message;
        RemoveNotes(MergeNoteKind.Error);
        Notes.Add(new MergeNoteItem(message, MergeNoteKind.Error));
    }

    private void RemoveNotes(MergeNoteKind kind)
    {
        foreach (var n in Notes.Where(n => n.Kind == kind).ToList())
        {
            Notes.Remove(n);
        }
    }

    /// <summary>Optionen geändert: dieselbe Sitzung setzt neu zusammen bzw. tont nur neu (jüngster Wunsch gewinnt).</summary>
    private void RequestUpdate()
    {
        if (_suppress || _session is null || IsLocked || Phase != MergePhase.Setup || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _wanted = CurrentOptions;
        if (!_ready)
        {
            // erste Vorschau läuft noch (sie zieht danach nach) bzw. ist gescheitert: mit den neuen Optionen neu starten
            if (!IsComputing)
            {
                _ = RestartPreviewAsync();
            }

            return;
        }

        if (_updatingFor == _sessionVersion)
        {
            return; // läuft schon; der jüngste Wunsch wird danach gerechnet
        }

        _ = RunUpdatesAsync();
    }

    private async Task RunUpdatesAsync()
    {
        var version = _sessionVersion;
        _updatingFor = version;
        try
        {
            while (version == _sessionVersion && _wanted is { } wanted && wanted != _applied && _session is { } session)
            {
                var lookOnly = _applied is { } a && wanted.SameGeometry(a) && Preview is not null;
                IsComputing = !lookOnly;
                if (!lookOnly)
                {
                    ProgressFraction = 0;
                    ProgressText = "Zusammensetzen";
                }

                try
                {
                    var preview = await session.UpdateAsync(wanted, _life.Token).ConfigureAwait(true);
                    if (version != _sessionVersion)
                    {
                        return;
                    }

                    _applied = wanted;
                    ApplyPreview(preview);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (MergeException ex)
                {
                    _applied = wanted;
                    ShowFailure(ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        finally
        {
            if (_updatingFor == version)
            {
                _updatingFor = -1;
            }

            if (version == _sessionVersion)
            {
                IsComputing = false; // eine inzwischen neue Sitzung behält ihren Zustand
            }
        }
    }

    private void OnProgress(MergeProgress progress)
    {
        if (progress.Saving)
        {
            _dispatcher.BeginInvoke(() => OnSaveProgress(progress), DispatcherPriority.Background);
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            ProgressText = progress.Text;
            ProgressFraction = progress.Fraction;
        }, DispatcherPriority.Background);
    }

    /// <summary>Neue Vorschau auf die Bühne, Prüfung, Schätzung und Marken der Quellen.</summary>
    private void ApplyPreview(MergePreview preview)
    {
        Preview = preview;
        var image = LoadImage(preview.ImagePath);
        var frame = preview.Frame is { } f ? LoadImage(f.ImagePath) : null;
        if (frame is not null && preview.Frame is { } pf)
        {
            StageImage = frame;
            StageCroppedImage = image;
            StageCrop = new Int32Rect(pf.CropX, pf.CropY, pf.CropWidth, pf.CropHeight);
            StageFrameWidth = frame.PixelWidth;
            HasStageFrame = true;
        }
        else
        {
            StageImage = image;
            StageCroppedImage = null;
            HasStageFrame = false;
        }

        StageMessage = image is null ? "Vorschau nicht lesbar." : null;
        EffectiveProjection = preview.Projection;
        foreach (var p in Projections)
        {
            p.Suggested = preview.SuggestedProjection;
        }

        var mp = preview.Width * (double)preview.Height / 1e6;
        StageLabel = string.Create(De, $"{MergeTexts.Projection(preview.Projection)}{(SelectedProjection == PanoramaProjection.Auto ? " (automatisch)" : string.Empty)}")
                     + (preview.Projection == PanoramaProjection.Spherical360 ? " · " + MergeTexts.SphereForm(preview.SphereForm) : string.Empty)
                     + string.Create(De, $" · Vorschau {mp:0.0} MP · {preview.Elapsed.TotalSeconds:0.0} s");

        // Marken der Quellen: „nicht eingebunden“
        var dropped = preview.DroppedFileIds.ToHashSet();
        foreach (var s in Sources.Where(s => s.IsUsable))
        {
            var mark = s.IsChecked && dropped.Contains(s.FileId) ? MergeTexts.NotIncluded
                : s.State == MergeSourceState.OnlineOnly ? MergeTexts.SourceState(s.State)
                : s.Mark == MergeTexts.NotIncluded ? string.Empty : s.Mark;
            s.Mark = mark;
        }

        // Prüfung vor dem Speichern (Abnahme Nr. 7) und Hinweise der Rechnung
        RemoveNotes(MergeNoteKind.Error);
        RemoveNotes(MergeNoteKind.Warning);
        if (!preview.Quality.Passed)
        {
            Notes.Add(new MergeNoteItem("Nicht speicherbar — " + string.Join(" ", preview.Quality.Problems.Select(p => p.Text)) + " Andere Aufnahmeart oder Auswahl versuchen.", MergeNoteKind.Error));
        }

        foreach (var note in preview.Notes.Where(n => n.Code is "sphere-not-closed" or "shift-unavailable" || n.Code.StartsWith("dropped", StringComparison.Ordinal)))
        {
            Notes.Add(new MergeNoteItem(note.Text, MergeNoteKind.Warning));
        }

        UpdateEstimate();
        UpdateWhy();
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Schätzung (Dauer, Größe, Speicher) und Größenstufen; volle Auflösung Standard, sonst größte passende Stufe (Abnahme Nr. 11).</summary>
    private void UpdateEstimate()
    {
        Sizes.Clear();
        if (_session is null || Preview is null)
        {
            EstimateText = string.Empty;
            EstimateDetail = string.Empty;
            return;
        }

        MergeEstimate e;
        try
        {
            e = _session.Estimate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        var p = Preview;
        var full = string.Create(De, $"{p.FullWidth:N0} × {p.FullHeight:N0} · {MergeTexts.Megapixels(p.FullWidth, p.FullHeight)}");
        if (e.Scale >= 0.999)
        {
            Sizes.Add(new MergeSizeItem("Volle Auflösung", full, true, true));
        }
        else
        {
            Sizes.Add(new MergeSizeItem("Volle Auflösung", full + " — " + (e.Reduction?.Text ?? "geht nicht"), false, false));
            Sizes.Add(new MergeSizeItem(string.Create(De, $"Größte passende · {Math.Round(e.Scale * 100):0} %"),
                string.Create(De, $"{e.Width:N0} × {e.Height:N0} · {MergeTexts.Megapixels(e.Width, e.Height)}"), true, true));
        }

        EstimateText = string.Create(De, $"{MergeTexts.Duration(e.Duration)} mit Vorrang · {e.Width:N0} × {e.Height:N0} · {MergeTexts.Megapixels(e.Width, e.Height)} · {MergeTexts.Bytes(e.DiskBytes)}");
        EstimateDetail = e.AvailableBytes > 0
            ? $"Arbeitsspeicher {MergeTexts.Bytes(e.PeakBytes)} von {MergeTexts.Bytes(e.AvailableBytes)} frei"
            : $"Arbeitsspeicher {MergeTexts.Bytes(e.PeakBytes)}";
    }

    /// <summary>Bild ohne Dateisperre laden (die Sitzung löscht ältere Vorschauen).</summary>
    internal static BitmapSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void UpdateTarget()
    {
        var first = Sources.FirstOrDefault(s => s.IsChecked && s.IsUsable) ?? Sources.FirstOrDefault();
        TargetName = first is null ? string.Empty : Path.GetFileNameWithoutExtension(first.Name) + MergeNaming.CleanSuffix(_settings.PanoramaSuffix) + ".jpg";
        PlaceText = _settings.Location == MergeSaveLocation.Subfolder
            ? $"im Unterordner „{MergeNaming.CleanFolderName(_settings.SubfolderName)}“ neben dem ersten Einzelbild"
            : _settings.DroneFolderOneUp ? "neben dem ersten Einzelbild (Drohnen-Ordner: eine Ebene höher)" : "neben dem ersten Einzelbild";
    }

    // ------------------------------------------------------------------ Optionen

    /// <summary>Kachel der Aufnahmeart (Parameter: <see cref="PanoramaProjection"/> bzw. sein Name).</summary>
    [RelayCommand]
    private void ChooseProjection(object? value)
    {
        if (value is PanoramaProjection p)
        {
            SelectProjection(p);
        }
        else if (value is string text && Enum.TryParse<PanoramaProjection>(text, out var parsed))
        {
            SelectProjection(parsed);
        }
    }

    internal void SelectProjection(PanoramaProjection projection)
    {
        foreach (var p in Projections)
        {
            p.IsSelected = p.Projection == projection;
        }

        SelectedProjection = projection;
    }

    partial void OnSelectedProjectionChanged(PanoramaProjection value) => RequestUpdate();

    partial void OnSphereFormChanged(MergeSphereForm value)
    {
        RequestUpdate();
        if (!_suppress && _loaded)
        {
            _settings = _settings with { SphereForm = value };
            _ = SaveSettingsQuietlyAsync(); // zuletzt gewählte Form vorausgewählt (Abnahme Nr. 4)
        }
    }

    partial void OnCropChanged(MergeCrop value) => RequestUpdate();

    partial void OnExposureChanged(bool value) => RequestUpdate();

    /// <summary>Regler „Farbe kräftiger“: nur neu tonen (kein Ausrichten, kein Zusammensetzen), Stärke merken.</summary>
    partial void OnColorBoostChanged(int value)
    {
        if (_suppress)
        {
            return;
        }

        _boostTimer.Stop();
        _boostTimer.Interval = BoostDebounce;
        _boostTimer.Start();
    }

    [RelayCommand]
    private void SetColorBoost(object? value)
    {
        if (value is not null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
        {
            ColorBoost = Math.Clamp(percent, MergeLimits.ColorBoostOff, MergeLimits.ColorBoostMax);
        }
    }

    private async Task RememberBoostAsync()
    {
        if (_settings.ColorBoost == ColorBoost)
        {
            return;
        }

        _settings = _settings with { ColorBoost = ColorBoost };
        await SaveSettingsQuietlyAsync().ConfigureAwait(true);
    }

    private async Task SaveSettingsQuietlyAsync()
    {
        try
        {
            await _service.SaveSettingsAsync(_settings, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Zusammenfügen: Einstellung nicht gemerkt");
        }
    }

    [RelayCommand]
    private void ToggleFrame() => ShowFrame = !ShowFrame;

    // ------------------------------------------------------------------ Schließen

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private bool CanDiscard() => Phase != MergePhase.Saving;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sourceTimer.Stop();
        _boostTimer.Stop();
        if (_settings.ColorBoost != ColorBoost)
        {
            await RememberBoostAsync().ConfigureAwait(true);
        }

        await _life.CancelAsync().ConfigureAwait(true);
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.ProgressChanged -= OnProgress;
            await session.DisposeAsync().ConfigureAwait(true); // Vorschau bricht ab, ein Speichern rechnet zu Ende
        }

        _life.Dispose();
    }
}
