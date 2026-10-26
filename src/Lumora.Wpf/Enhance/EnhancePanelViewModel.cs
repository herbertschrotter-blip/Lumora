using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Enhance;
using Lumora.Services.ModelPackages;
using Lumora.Settings.ModelPackages;
using Lumora.Shell;
using Wpf.Ui.Controls;

namespace Lumora.Enhance;

/// <summary>Ziel für Pinselstriche aus der Bühne (relative Koordinaten 0…1 des angezeigten Bildes).</summary>
public interface IEnhanceBrushTarget
{
    void BeginStroke();

    void AddDot(double x, double y);
}

/// <summary>Ein Werkzeug in der Leiste „Verbessern“ (Mockup 13): Titel, Unterzeile, Zustand des Modells.</summary>
public sealed partial class EnhanceToolItem : ObservableObject
{
    public EnhanceToolItem(EnhancePanelViewModel owner, EnhanceTool tool, string title, string subtitle, SymbolRegular symbol)
    {
        Owner = owner;
        Tool = tool;
        Title = title;
        Subtitle = subtitle;
        Symbol = symbol;
    }

    public EnhancePanelViewModel Owner { get; }

    public EnhanceTool Tool { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public SymbolRegular Symbol { get; }

    public string AutomationName => $"{Title} ({StateText})";

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _stateText = "…";

    [ObservableProperty] private bool _isReady = true;

    /// <summary>„Laden…“ möglich (Modell fehlt bzw. defekt, oder die Gesichtserkennung fehlt).</summary>
    [ObservableProperty] private bool _canLoad;

    [ObservableProperty] private bool _isLoading;

    /// <summary>Eintrag in Modelle &amp; Daten, den „Laden…“ holt.</summary>
    public string? LoadId { get; set; }
}

/// <summary>
/// Leiste „Verbessern“ in der Lightbox (ADR-0071, Mockup 13): Werkzeuge mit Modell-Zustand, Optionen, Vorschau mit Vorher/Nachher,
/// „Als neue Datei speichern“ / Verwerfen. Das Original bleibt unverändert. Eine Instanz je Lightbox-Fenster.
/// </summary>
public sealed partial class EnhancePanelViewModel : ObservableObject, IEnhanceBrushTarget, IDisposable
{
    public static readonly IReadOnlyList<string> CropPresets = ["Original", "Quadrat", "4:3", "3:2", "16:9"];

    private readonly IPhotoEnhanceService _service;
    private readonly IEnhanceSettingsStore? _settingsStore;
    private readonly IModelPackageManager? _models;
    private readonly IDialogService? _dialogs;
    private readonly ILogger _logger;
    private readonly List<int> _strokeStarts = [];
    private CancellationTokenSource? _renderCts;
    private int _fileVersion;
    private bool _disposed;

    /// <summary>Optionen wurden während der Berechnung geändert: das Ergebnis passt nicht mehr und wird verworfen.</summary>
    private bool _staleDuringRender;

    public EnhancePanelViewModel(IPhotoEnhanceService service, IModelPackageManager? models = null, IDialogService? dialogs = null, ILogger<EnhancePanelViewModel>? logger = null, IEnhanceSettingsStore? settingsStore = null)
    {
        _service = service;
        _settingsStore = settingsStore;
        _models = models;
        _dialogs = dialogs;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Tools =
        [
            new(this, EnhanceTool.Auto, "Automatisch", "Lumora wählt je Foto: Kratzer, Gesichter, Einfärben, Größe", SymbolRegular.Wand24),
            new(this, EnhanceTool.Upscale, "Hochskalieren und schärfen", "Real-ESRGAN · 2× oder 4×", SymbolRegular.ArrowExpand24),
            new(this, EnhanceTool.Faces, "Gesichter restaurieren", "GFPGAN · Stärke einstellbar", SymbolRegular.PersonSquare24),
            new(this, EnhanceTool.Colorize, "Einfärben", "DeOldify · für Fotos ohne Farbe", SymbolRegular.Color24),
            new(this, EnhanceTool.Erase, "Entfernen", "LaMa · Pinsel über Störendes · Kratzer automatisch", SymbolRegular.Eraser24),
            new(this, EnhanceTool.Adjust, "Zuschneiden, drehen, Licht", "Helligkeit, Kontrast, Drehen, Zuschnitt", SymbolRegular.Crop24),
        ];
        Tools[0].IsSelected = true;
        if (_models is not null)
        {
            _models.PackageChanged += OnPackageChanged;
        }
    }

    public ObservableCollection<EnhanceToolItem> Tools { get; }

    public ObservableCollection<EnhanceBrushDot> BrushDots { get; } = [];

    public IReadOnlyList<string> CropChoices => CropPresets;

    /// <summary>Gespeichert (Lightbox zeigt die Meldung).</summary>
    public event EventHandler<EnhanceSaveResult>? Saved;

    // ------------------------------------------------------------------ Zustand

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOverlay), nameof(IsBrushMode))]
    private bool _isOpen;

    /// <summary>Aktuelles Foto (null bei Video/Archiv).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCommand))]
    private int? _fileId;

    public bool HasPhoto => FileId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowsOverlay), nameof(IsBrushMode), nameof(CanCalculate), nameof(PreviewInfo))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(CalculateCommand))]
    private EnhancePreview? _preview;

    [ObservableProperty] private BitmapSource? _afterImage;

    /// <summary>Lage des Vorher/Nachher-Reglers (0 = nur Nachher, 1 = nur Vorher; links Vorher).</summary>
    [ObservableProperty] private double _comparePosition = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalculate))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCommand), nameof(SaveCommand), nameof(DiscardCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _progressText;

    [ObservableProperty] private double _progress;

    [ObservableProperty] private string? _message;

    [ObservableProperty] private bool _messageIsError;

    [ObservableProperty] private string? _targetName;

    [ObservableProperty] private string _gpuText = "Grafikkarte · geteilt mit Gesichtern und Motiven";

    [ObservableProperty] private bool _isLoaded;

    // Optionen
    [ObservableProperty] private int _upscaleFactor = 2;

    /// <summary>Natürlichkeit beim Hochskalieren 0…0,8 (Anteil Original); gemerkt in Einstellungen › Verbessern.</summary>
    [ObservableProperty] private double _naturalness = EnhanceSettings.DefaultNaturalness;

    /// <summary>Körnung beim Hochskalieren 0…1; gemerkt in Einstellungen › Verbessern.</summary>
    [ObservableProperty] private double _grain = EnhanceSettings.DefaultGrain;

    /// <summary>Gelesene Einstellungen (zum Merken geänderter Regler).</summary>
    private EnhanceSettings? _loadedSettings;

    [ObservableProperty] private double _faceStrength = 0.8;

    /// <summary>Pinselgröße als Radius relativ zur Bildbreite.</summary>
    [ObservableProperty] private double _brushSize = 0.03;

    [ObservableProperty] private bool _scratches;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RotationText))]
    private int _rotation;

    [ObservableProperty] private string _cropPreset = CropPresets[0];

    [ObservableProperty] private double _brightness;

    [ObservableProperty] private double _contrast;

    public bool HasPreview => Preview is not null;

    public bool CanCalculate => HasPhoto && !IsBusy && !HasPreview;

    /// <summary>Bühne zeigt die Vorher/Nachher-Ansicht bzw. den Pinsel.</summary>
    public bool ShowsOverlay => IsOpen && (HasPreview || IsBrushMode);

    /// <summary>Pinsel aktiv: „Entfernen“ gewählt, noch keine Vorschau.</summary>
    public bool IsBrushMode => IsOpen && !HasPreview && Tool(EnhanceTool.Erase).IsSelected;

    public bool IsUpscale4 => UpscaleFactor == 4;

    public string RotationText => Rotation == 0 ? "nicht gedreht" : $"{Rotation}° im Uhrzeigersinn";

    public string PreviewInfo => Preview is { } p
        ? $"{p.ToolsText} · {p.Width:N0} × {p.Height:N0} px · {p.Duration.TotalSeconds:N1} s" + (p.Notes.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, p.Notes) : string.Empty)
        : string.Empty;

    public EnhanceToolItem Tool(EnhanceTool tool) => Tools.First(t => t.Tool == tool);

    partial void OnUpscaleFactorChanged(int value)
    {
        OnPropertyChanged(nameof(IsUpscale4));
        Stale();
    }

    partial void OnFaceStrengthChanged(double value) => Stale();

    partial void OnNaturalnessChanged(double value) => Stale();

    partial void OnGrainChanged(double value) => Stale();

    /// <summary>Regler „Natürlichkeit“ sichtbar: beim Hochskalieren und bei „Automatisch“ (kann hochskalieren).</summary>
    public bool ShowNaturalness => Tool(EnhanceTool.Upscale).IsSelected || Tool(EnhanceTool.Auto).IsSelected;

    partial void OnScratchesChanged(bool value) => Stale();

    partial void OnRotationChanged(int value) => Stale();

    partial void OnCropPresetChanged(string value) => Stale();

    partial void OnBrightnessChanged(double value) => Stale();

    partial void OnContrastChanged(double value) => Stale();

    // ------------------------------------------------------------------ Öffnen, Blättern

    /// <summary>Leiste öffnen/schließen; beim Schließen wird eine Vorschau verworfen und Modelle freigegeben.</summary>
    public async Task SetOpenAsync(bool open)
    {
        IsOpen = open;
        if (open)
        {
            await LoadNaturalnessAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        else
        {
            DiscardPreview();
            _renderCts?.Cancel();
            _service.ReleaseModels();
        }
    }

    /// <summary>Anderes Foto in der Lightbox: Vorschau und Pinselstriche verwerfen, Dateiname neu.</summary>
    public async Task SetFileAsync(int? fileId, bool isImage)
    {
        var id = isImage ? fileId : null;
        if (id == FileId)
        {
            return;
        }

        var version = ++_fileVersion;
        _renderCts?.Cancel();
        DiscardPreview();
        ClearStrokes();
        Message = null;
        FileId = id;
        TargetName = null;
        if (id is int file)
        {
            try
            {
                var name = await _service.SuggestFileNameAsync(file).ConfigureAwait(true);
                if (version == _fileVersion)
                {
                    TargetName = name;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Foto verbessern: Dateiname nicht ermittelt");
            }
        }
    }

    /// <summary>Natürlichkeit und Körnung aus Einstellungen › Verbessern (einmal je Leiste).</summary>
    private async Task LoadNaturalnessAsync()
    {
        if (_settingsStore is null || _loadedSettings is not null)
        {
            return;
        }

        try
        {
            _loadedSettings = await _settingsStore.LoadAsync().ConfigureAwait(true);
            Naturalness = _loadedSettings.Naturalness;
            Grain = _loadedSettings.Grain;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Foto verbessern: Natürlichkeit nicht gelesen");
        }
    }

    /// <summary>Geänderte Regler merken (beim Berechnen), damit die nächste Leiste und der Stapel sie übernehmen.</summary>
    private async Task SaveNaturalnessAsync()
    {
        if (_settingsStore is null || _loadedSettings is not { } loaded
            || (Math.Abs(loaded.Naturalness - Naturalness) < 0.005 && Math.Abs(loaded.Grain - Grain) < 0.005))
        {
            return;
        }

        try
        {
            var current = await _settingsStore.LoadAsync().ConfigureAwait(true);
            _loadedSettings = current with { Naturalness = Naturalness, Grain = Grain };
            await _settingsStore.SaveAsync(_loadedSettings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Foto verbessern: Natürlichkeit nicht gemerkt");
        }
    }

    /// <summary>Zustände der Werkzeuge lesen (Modell geladen / fehlt / Quelle folgt).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var status = await _service.GetToolStatusAsync().ConfigureAwait(true);
            foreach (var s in status)
            {
                var item = Tool(s.Tool);
                item.IsReady = s.Ready;
                item.StateText = s.StateText;
                var needsFaces = s.StateText == "braucht Gesichtserkennung";
                item.LoadId = needsFaces ? ModelPackageCatalog.FacesId : s.ModelId;
                item.CanLoad = _models is not null && _dialogs is not null && (s.CanDownload || needsFaces);
            }

            IsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foto verbessern: Zustand der Werkzeuge nicht lesbar");
        }
    }

    private void OnPackageChanged(string id)
    {
        if (id.StartsWith("enhance.", StringComparison.Ordinal) || id == ModelPackageCatalog.FacesId)
        {
            UiThread.Post(() => _ = RefreshAsync());
        }
    }

    // ------------------------------------------------------------------ Werkzeuge

    /// <summary>Werkzeug an/aus: „Automatisch“ schließt die Modell-Werkzeuge aus und umgekehrt; Zuschneiden/Licht geht immer dazu.</summary>
    [RelayCommand]
    private void ToggleTool(EnhanceToolItem? item)
    {
        if (item is null)
        {
            return;
        }

        var select = !item.IsSelected;
        item.IsSelected = select;
        if (select && item.Tool == EnhanceTool.Auto)
        {
            foreach (var other in Tools.Where(t => t.Tool is not (EnhanceTool.Auto or EnhanceTool.Adjust)))
            {
                other.IsSelected = false;
            }
        }
        else if (select && item.Tool != EnhanceTool.Adjust)
        {
            Tool(EnhanceTool.Auto).IsSelected = false;
        }

        Stale();
        OnPropertyChanged(nameof(IsBrushMode));
        OnPropertyChanged(nameof(ShowsOverlay));
        OnPropertyChanged(nameof(ShowNaturalness));
    }

    [RelayCommand]
    private void SetUpscaleFactor(string? factor) => UpscaleFactor = factor == "4" ? 4 : 2;

    [RelayCommand]
    private void RotateLeft() => Rotation = (Rotation + 270) % 360;

    [RelayCommand]
    private void RotateRight() => Rotation = (Rotation + 90) % 360;

    [RelayCommand]
    private void ResetAdjust()
    {
        Rotation = 0;
        CropPreset = CropPresets[0];
        Brightness = 0;
        Contrast = 0;
    }

    /// <summary>Modell laden: Bestätigung (Name, Quelle, Größe, Lizenz) und Laden über den Manager (Fortschritt in den Aktivitäten).</summary>
    [RelayCommand]
    private async Task LoadModelAsync(EnhanceToolItem? item)
    {
        if (item?.LoadId is not { } id || _models is null || _dialogs is null || item.IsLoading)
        {
            return;
        }

        ModelDownloadRequest? request;
        try
        {
            request = await ModelDownloadDialog.AskAsync(_dialogs, _models, id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ModelPackageException)
        {
            ShowMessage(ex.Message, error: true);
            return;
        }

        if (request is null)
        {
            return;
        }

        item.IsLoading = true;
        try
        {
            await _models.InstallAsync(request).ConfigureAwait(true);
            ShowMessage($"{request.Definition.Name} geladen und geprüft.", error: false);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Laden abgebrochen.", error: false);
        }
        catch (Exception ex) when (ex is ModelPackageException or IOException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            ShowMessage(ex.Message, error: true);
        }
        finally
        {
            item.IsLoading = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ Pinsel

    public void BeginStroke()
    {
        _strokeStarts.Add(BrushDots.Count);
        UndoStrokeCommand.NotifyCanExecuteChanged();
        ClearStrokesCommand.NotifyCanExecuteChanged();
    }

    public void AddDot(double x, double y)
    {
        if (!IsBrushMode || x < 0 || y < 0 || x > 1 || y > 1)
        {
            return;
        }

        if (_strokeStarts.Count == 0)
        {
            BeginStroke();
        }

        BrushDots.Add(new EnhanceBrushDot(x, y, BrushSize));
        OnPropertyChanged(nameof(BrushSummary));
    }

    public string BrushSummary => _strokeStarts.Count == 0 ? "Noch nichts markiert." : _strokeStarts.Count == 1 ? "1 Strich markiert." : $"{_strokeStarts.Count} Striche markiert.";

    [RelayCommand(CanExecute = nameof(HasStrokes))]
    private void UndoStroke()
    {
        if (_strokeStarts.Count == 0)
        {
            return;
        }

        var start = _strokeStarts[^1];
        _strokeStarts.RemoveAt(_strokeStarts.Count - 1);
        while (BrushDots.Count > start)
        {
            BrushDots.RemoveAt(BrushDots.Count - 1);
        }

        OnPropertyChanged(nameof(BrushSummary));
        UndoStrokeCommand.NotifyCanExecuteChanged();
        ClearStrokesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasStrokes))]
    private void ClearStrokes()
    {
        _strokeStarts.Clear();
        BrushDots.Clear();
        OnPropertyChanged(nameof(BrushSummary));
        UndoStrokeCommand.NotifyCanExecuteChanged();
        ClearStrokesCommand.NotifyCanExecuteChanged();
    }

    private bool HasStrokes() => _strokeStarts.Count > 0;

    // ------------------------------------------------------------------ Vorschau, Speichern, Verwerfen

    /// <summary>Gewählte Werkzeuge als Optionen für den Dienst.</summary>
    public EnhanceOptions BuildOptions()
    {
        var adjust = Tool(EnhanceTool.Adjust).IsSelected;
        return new EnhanceOptions
        {
            Auto = Tool(EnhanceTool.Auto).IsSelected,
            Upscale = Tool(EnhanceTool.Upscale).IsSelected,
            UpscaleFactor = UpscaleFactor,
            Naturalness = Naturalness,
            Grain = Grain,
            Faces = Tool(EnhanceTool.Faces).IsSelected,
            FaceStrength = FaceStrength,
            Colorize = Tool(EnhanceTool.Colorize).IsSelected,
            Erase = Tool(EnhanceTool.Erase).IsSelected,
            BrushDots = BrushDots.ToList(),
            Scratches = Scratches,
            Rotation = adjust ? Rotation : 0,
            CropAspect = adjust ? AspectOf(CropPreset) : null,
            Brightness = adjust ? Brightness : 0,
            Contrast = adjust ? Contrast : 0,
        };
    }

    private static double? AspectOf(string preset) => preset switch
    {
        "Quadrat" => 1.0,
        "4:3" => 4.0 / 3,
        "3:2" => 3.0 / 2,
        "16:9" => 16.0 / 9,
        _ => null,
    };

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculateAsync()
    {
        if (FileId is not int fileId)
        {
            return;
        }

        var options = BuildOptions();
        if (options.IsEmpty)
        {
            ShowMessage(Tool(EnhanceTool.Erase).IsSelected ? "Erst Störendes mit dem Pinsel markieren oder „Kratzer automatisch“ wählen." : "Bitte ein Werkzeug wählen.", error: true);
            return;
        }

        if (ShowNaturalness)
        {
            _ = SaveNaturalnessAsync();
        }

        _renderCts?.Cancel();
        var cts = _renderCts = new CancellationTokenSource();
        var version = _fileVersion;
        _staleDuringRender = false;
        IsBusy = true;
        Message = null;
        Progress = 0;
        ProgressText = "Vorschau wird berechnet …";
        var progress = new Progress<EnhanceProgress>(p =>
        {
            ProgressText = p.Stage + " …";
            Progress = p.Fraction;
        });
        try
        {
            var preview = await _service.RenderAsync(fileId, options, progress, cts.Token).ConfigureAwait(true);
            if (version != _fileVersion || _disposed || !IsOpen)
            {
                _service.Discard(preview);
                return;
            }

            if (_staleDuringRender)
            {
                _service.Discard(preview);
                ShowMessage("Optionen während der Berechnung geändert — bitte die Vorschau neu berechnen.", error: false);
                return;
            }

            AfterImage = LoadBitmap(preview.ResultPath, preview.Width);
            Preview = preview;
            ComparePosition = 0.5;
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Abgebrochen.", error: false);
        }
        catch (EnhanceNothingToDoException ex)
        {
            ShowMessage(ex.Message, error: false);
        }
        catch (Exception ex) when (ex is EnhanceException or EnhanceModelMissingException or IOException or UnauthorizedAccessException)
        {
            ShowMessage(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            ProgressText = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _renderCts?.Cancel();

    private bool CanSave() => HasPreview && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Preview is not { } preview)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _service.SaveAsync(preview).ConfigureAwait(true);
            if (result.Success)
            {
                Preview = null;
                AfterImage = null;
                ClearStrokes();
                ShowMessage($"Gespeichert als „{Path.GetFileName(result.Path)}“ — das Original bleibt unverändert (Rückgängig in den Aktivitäten).", error: false);
                Saved?.Invoke(this, result);
                if (FileId is int id)
                {
                    TargetName = await _service.SuggestFileNameAsync(id).ConfigureAwait(true);
                }
            }
            else
            {
                ShowMessage(result.Error ?? "Speichern fehlgeschlagen.", error: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Services.Scanning.ScanGateWaitCanceledException)
        {
            ShowMessage($"Speichern fehlgeschlagen: {ex.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Discard()
    {
        DiscardPreview();
        ShowMessage("Verworfen — es wurde nichts gespeichert.", error: false);
    }

    /// <summary>Optionen geändert: eine vorhandene Vorschau passt nicht mehr und wird verworfen.</summary>
    private void Stale()
    {
        if (IsBusy)
        {
            _staleDuringRender = true;
            return;
        }

        if (Preview is not null)
        {
            DiscardPreview();
        }
    }

    private void DiscardPreview()
    {
        if (Preview is { } preview)
        {
            _service.Discard(preview);
        }

        Preview = null;
        AfterImage = null;
    }

    private void ShowMessage(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }

    private static BitmapSource? LoadBitmap(string path, int width)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bitmap = new BitmapImage();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (width > 3840)
            {
                bitmap.DecodePixelWidth = 3840; // Anzeige: höchstens 4K, die Datei behält die volle Größe
            }
            bitmap.EndInit();
        }

        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_models is not null)
        {
            _models.PackageChanged -= OnPackageChanged;
        }

        _renderCts?.Cancel();
        DiscardPreview();
        _service.ReleaseModels();
    }
}

/// <summary>Erzeugt je Lightbox-Fenster eine Leiste (das Fenster besitzt und entsorgt sie).</summary>
public sealed class EnhancePanelFactory(IPhotoEnhanceService service, IModelPackageManager? models = null, IDialogService? dialogs = null, ILogger<EnhancePanelViewModel>? logger = null, IEnhanceSettingsStore? settingsStore = null)
{
    public EnhancePanelViewModel Create() => new(service, models, dialogs, logger, settingsStore);
}
