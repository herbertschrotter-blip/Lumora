using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Faces;
using Lumora.Services.Archive;
using Lumora.Services.Database;
using Lumora.Services.Database.Relocation;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Media;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;
using Lumora.Shell;

namespace Lumora.Settings;

/// <summary>
/// Einstellungen: Farbschema, Speicherorte (DB, Logs; DB samt Thumbnails umziehen, ADR-0026), Thumbnail-Cache (Größe, Leeren), FFmpeg-Status,
/// unterstützte Formate, Ordnerüberwachung (Schalter, Ruhezeit), automatisches Entpacken von Archiven (ADR-0032, je aktiver Bibliothek ADR-0033) und App-Version.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string FfmpegInstallCommand = "winget install Gyan.FFmpeg";

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IVideoThumbnailRenderer _videoRenderer;
    private readonly IMediaFormatRegistry _formats;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly ILibraryWatchService _watch;
    private readonly IDatabaseRelocationService _relocation;
    private readonly IAppRestartService _restart;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IArchiveAutoExtractService? _autoExtract;
    private readonly ILibraryService? _libraries;
    private readonly IThumbnailCacheCleaner _cacheCleaner;

    /// <param name="autoExtract">Automatisches Entpacken (ADR-0032); optional, damit ältere Tests ohne den Dienst auskommen.</param>
    /// <param name="libraries">Aktive Bibliothek für die Einstellung je Bibliothek (ADR-0033); optional wie <paramref name="autoExtract"/>.</param>
    /// <param name="cacheCleaner">„Cache leeren“ (0.5.40, aus <c>AddThumbnailStock</c>); ohne Registrierung einer mit denselben Diensten.</param>
    public SettingsViewModel(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IVideoThumbnailRenderer videoRenderer,
        IThumbnailService thumbnails,
        IMediaFormatRegistry formats,
        IThemeService theme,
        IDialogService dialogs,
        ILibraryWatchService watch,
        IDatabaseRelocationService relocation,
        IAppRestartService restart,
        ILogger<SettingsViewModel> logger,
        IArchiveAutoExtractService? autoExtract = null,
        ILibraryService? libraries = null,
        FaceSettingsViewModel? faces = null,
        Lumora.Settings.FaceData.FaceDataSettingsViewModel? faceData = null,
        Lumora.Settings.PersonRelations.PersonRelationsSettingsViewModel? relations = null,
        IThumbnailCacheCleaner? cacheCleaner = null)
    {
        Relations = relations; // „Personen & Beziehungen“ (0.2.47, ADR-0045)
        _cacheCleaner = cacheCleaner ?? new ThumbnailCacheCleaner(factory, paths, thumbnails);
        _factory = factory;
        _paths = paths;
        _videoRenderer = videoRenderer;
        _formats = formats;
        _theme = theme;
        _dialogs = dialogs;
        _watch = watch;
        _relocation = relocation;
        _restart = restart;
        _logger = logger;
        _autoExtract = autoExtract;
        _libraries = libraries;
        Faces = faces;
        FaceData = faceData;
    }

    /// <summary>Karte „Personen &amp; Beziehungen“ im Reiter Gesichter (0.2.47, ADR-0045); null = ausgeblendet.</summary>
    public Lumora.Settings.PersonRelations.PersonRelationsSettingsViewModel? Relations { get; }

    /// <summary>„Gesichtsdaten löschen…“ im Abschnitt Gesichtserkennung (0.2.38); null = ausgeblendet (Tests ohne den Dienst).</summary>
    public Lumora.Settings.FaceData.FaceDataSettingsViewModel? FaceData { get; }

    /// <summary>Abschnitt „Gesichtserkennung“ (ADR-0035); null = Abschnitt ausgeblendet (Tests ohne den Dienst).</summary>
    public FaceSettingsViewModel? Faces { get; }

    // ---- Archive automatisch entpacken (Services/Archive, ADR-0032; je Bibliothek, ADR-0033) ---------

    [ObservableProperty] private bool _isAutoExtractEnabled;
    [ObservableProperty] private bool _isAutoExtractRecycle;

    /// <summary>Dienst vorhanden und eine aktive Bibliothek gewählt — sonst bleibt der Abschnitt deaktiviert.</summary>
    [ObservableProperty] private bool _isAutoExtractAvailable;

    /// <summary>Für welche Bibliothek die Schalter gelten („Gilt für die aktive Bibliothek „Fotos““).</summary>
    [ObservableProperty] private string _autoExtractLibraryText = "Keine Bibliothek ausgewählt.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtractExistingArchivesCommand))]
    private bool _isExtractingExisting;

    private bool _autoExtractLoading;

    /// <summary>Bibliothek, deren Einstellung der Abschnitt zeigt (die aktive beim Laden der Seite).</summary>
    internal int? AutoExtractLibraryId { get; private set; }

    /// <summary>Aktive Bibliothek lesen und deren Einstellungen anzeigen (ohne sie neu zu speichern).</summary>
    internal async Task LoadAutoExtractSettingsAsync()
    {
        if (_autoExtract is null)
        {
            return;
        }

        LibraryInfo? active = null;
        try
        {
            active = _libraries is null ? null : await _libraries.GetActiveAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatisches Entpacken: aktive Bibliothek nicht lesbar");
        }

        AutoExtractLibraryId = active?.Id;
        AutoExtractLibraryText = active is null
            ? "Keine Bibliothek ausgewählt — die Einstellung gilt je Bibliothek."
            : $"Gilt für die aktive Bibliothek „{active.Name}“ (je Bibliothek einzeln, Standard aus).";
        ShowAutoExtractSettings();
    }

    private void ShowAutoExtractSettings()
    {
        _autoExtractLoading = true;
        try
        {
            var settings = _autoExtract is not null && AutoExtractLibraryId is { } id
                ? _autoExtract.GetSettings(id)
                : ArchiveAutoExtractSettings.Default;
            IsAutoExtractAvailable = _autoExtract is not null && AutoExtractLibraryId is not null;
            IsAutoExtractEnabled = settings.Enabled;
            IsAutoExtractRecycle = settings.RecycleAfterExtract;
        }
        finally
        {
            _autoExtractLoading = false;
        }

        ExtractExistingArchivesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAutoExtractEnabledChanged(bool value)
    {
        if (!_autoExtractLoading && _autoExtract is { } service && AutoExtractLibraryId is { } id)
        {
            _ = ApplyAutoExtractAsync(() => service.SetEnabledAsync(id, value), "Automatisches Entpacken");
        }

        ExtractExistingArchivesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAutoExtractRecycleChanged(bool value)
    {
        if (!_autoExtractLoading && _autoExtract is { } service && AutoExtractLibraryId is { } id)
        {
            _ = ApplyAutoExtractAsync(() => service.SetRecycleAfterExtractAsync(id, value), "Papierkorb nach dem Entpacken");
        }
    }

    /// <summary>Sofort anwenden und speichern; bei Fehler Fehler-InfoBar und Schalter auf den gespeicherten Stand zurück.</summary>
    internal async Task ApplyAutoExtractAsync(Func<Task> apply, string what)
    {
        try
        {
            await apply().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{what} konnte nicht gespeichert werden: {ex.Message}";
            _logger.LogError(ex, "Automatisches Entpacken: {What} konnte nicht gespeichert werden", what);
            ShowAutoExtractSettings();
        }
    }

    private bool CanExtractExistingArchives() => IsAutoExtractAvailable && IsAutoExtractEnabled && !IsExtractingExisting;

    /// <summary>
    /// „Vorhandene Archive in Eingängen jetzt entpacken…“: zählt die Archive, die schon vor dem Einschalten im Katalog
    /// standen (Zielordner fehlt noch), fragt nach und reiht den Lauf ein — Fortschritt und Ergebnis in der Statusleiste.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExtractExistingArchives))]
    private async Task ExtractExistingArchivesAsync()
    {
        if (_autoExtract is not { } service || AutoExtractLibraryId is not { } id)
        {
            return;
        }

        IsExtractingExisting = true;
        try
        {
            var count = await service.CountExistingAsync(id).ConfigureAwait(true);
            if (count == 0)
            {
                InfoMessage = "Keine vorhandenen Archive in den Eingangsordnern, die noch nicht entpackt sind.";
                return;
            }

            var recycle = IsAutoExtractRecycle ? " Nach fehlerfreiem Entpacken wandern die Archive in den Papierkorb." : " Die Archive bleiben liegen.";
            var confirmed = await _dialogs.ConfirmAsync(
                "Vorhandene Archive entpacken",
                $"{ArchiveCountText(count)} in den Eingangsordnern werden jetzt jeweils in einen Ordner mit dem Archivnamen daneben entpackt; danach wird die Bibliothek aktualisiert.{recycle}",
                "Entpacken").ConfigureAwait(true);
            if (confirmed)
            {
                await StartExtractExistingAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Vorhandene Archive konnten nicht entpackt werden: {ex.Message}";
            _logger.LogError(ex, "Vorhandene Archive entpacken fehlgeschlagen");
        }
        finally
        {
            IsExtractingExisting = false;
        }
    }

    /// <summary>Reiht den Lauf ohne Rückfrage ein (nach der Bestätigung bzw. im Rauchtest) und liefert die Zahl der Archive.</summary>
    internal async Task<int> StartExtractExistingAsync()
    {
        if (_autoExtract is not { } service || AutoExtractLibraryId is not { } id)
        {
            return 0;
        }

        var queued = await service.ExtractExistingAsync(id).ConfigureAwait(true);
        InfoMessage = queued == 0
            ? "Keine vorhandenen Archive zum Entpacken."
            : $"{ArchiveCountText(queued)} werden entpackt — Fortschritt und Ergebnis in der Statusleiste.";
        return queued;
    }

    private static string ArchiveCountText(int count) => count == 1 ? "1 Archiv" : $"{count} Archive";

    // ---- Ordnerüberwachung (Services/Watch) ---------------------------------------------------------

    [ObservableProperty] private bool _isWatchEnabled = true;
    [ObservableProperty] private int _watchQuietSeconds = WatchSettings.DefaultQuietSeconds;
    private bool _watchLoading;

    public int WatchQuietMinSeconds => WatchSettings.MinQuietSeconds;

    public int WatchQuietMaxSeconds => WatchSettings.MaxQuietSeconds;

    private void LoadWatchSettings()
    {
        _watchLoading = true;
        try
        {
            IsWatchEnabled = _watch.IsEnabled;
            WatchQuietSeconds = (int)_watch.QuietPeriod.TotalSeconds;
        }
        finally
        {
            _watchLoading = false;
        }
    }

    partial void OnIsWatchEnabledChanged(bool value)
    {
        if (!_watchLoading)
        {
            _ = ApplyWatchAsync(() => _watch.SetEnabledAsync(value), "Ordnerüberwachung");
        }
    }

    partial void OnWatchQuietSecondsChanged(int value)
    {
        var clamped = Math.Clamp(value, WatchSettings.MinQuietSeconds, WatchSettings.MaxQuietSeconds);
        if (clamped != value)
        {
            WatchQuietSeconds = clamped; // löst diesen Handler erneut mit dem gültigen Wert aus
            return;
        }

        if (!_watchLoading)
        {
            _ = ApplyWatchAsync(() => _watch.SetQuietPeriodAsync(TimeSpan.FromSeconds(value)), "Ruhezeit");
        }
    }

    /// <summary>Einstellung sofort anwenden und speichern; Fehler landen in der Fehler-InfoBar.</summary>
    private async Task ApplyWatchAsync(Func<Task> apply, string what)
    {
        try
        {
            await apply().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{what} konnte nicht gespeichert werden: {ex.Message}";
            _logger.LogError(ex, "Ordnerüberwachung: {What} konnte nicht gespeichert werden", what);
        }
    }

    [ObservableProperty] private string _databasePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDatabaseCommand), nameof(ResetDatabaseLocationCommand))]
    private bool _isRelocating;

    [ObservableProperty] private string? _relocationStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetDatabaseLocationCommand))]
    private bool _usesCustomLocation;
    [ObservableProperty] private string _databaseSizeText = string.Empty;
    [ObservableProperty] private string _logsPath = string.Empty;
    [ObservableProperty] private string _thumbnailsPath = string.Empty;
    [ObservableProperty] private string _thumbnailCacheText = "wird berechnet…";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ClearThumbnailCacheCommand))] private bool _isCacheBusy;
    [ObservableProperty] private bool _isFfmpegAvailable;
    [ObservableProperty] private string _ffmpegText = string.Empty;
    [ObservableProperty] private string _imageFormatsText = string.Empty;
    [ObservableProperty] private string _videoFormatsText = string.Empty;
    [ObservableProperty] private string _appVersionText = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isErrorOpen;
    [ObservableProperty] private string? _infoMessage;
    [ObservableProperty] private bool _isInfoOpen;

    public string FfmpegCommandText => FfmpegInstallCommand;

    // Theme-Auswahl als drei TwoWay-gebundene RadioButtons; der Setter mit true wendet den Modus an.
    public bool IsThemeSystem
    {
        get => _theme.Mode == AppThemeMode.System;
        set => OnThemeRadioChanged(value, AppThemeMode.System);
    }

    public bool IsThemeLight
    {
        get => _theme.Mode == AppThemeMode.Light;
        set => OnThemeRadioChanged(value, AppThemeMode.Light);
    }

    public bool IsThemeDark
    {
        get => _theme.Mode == AppThemeMode.Dark;
        set => OnThemeRadioChanged(value, AppThemeMode.Dark);
    }

    partial void OnErrorMessageChanged(string? value) => IsErrorOpen = !string.IsNullOrEmpty(value);

    partial void OnInfoMessageChanged(string? value) => IsInfoOpen = !string.IsNullOrEmpty(value);

    [RelayCommand]
    private async Task LoadAsync()
    {
        DatabasePath = _factory.DatabasePath;
        UsesCustomLocation = !AppDataPaths.PathsEqual(_relocation.CurrentFolder, _relocation.DefaultFolder);
        DatabaseSizeText = FileSizeText(_factory.DatabasePath);
        LogsPath = _paths.LogsRoot;
        ThumbnailsPath = _paths.ThumbnailsRoot;

        IsFfmpegAvailable = _videoRenderer.IsAvailable;
        FfmpegText = IsFfmpegAvailable
            ? "FFmpeg wurde gefunden — Vorschaubilder für Videos werden erzeugt."
            : "FFmpeg wurde nicht gefunden — Videos erhalten keine Vorschaubilder. Installation per winget (danach App neu starten):";

        ImageFormatsText = string.Join("  ", _formats.ImageExtensions.OrderBy(e => e, StringComparer.Ordinal));
        VideoFormatsText = string.Join("  ", _formats.VideoExtensions.OrderBy(e => e, StringComparer.Ordinal));
        AppVersionText = BuildVersionText();
        NotifyThemeChanged();
        LoadWatchSettings();
        await LoadAutoExtractSettingsAsync().ConfigureAwait(true);
        Faces?.Load();
        if (Relations is not null)
        {
            await Relations.LoadAsync().ConfigureAwait(true); // Beziehungen (0.2.47)
        }

        await RefreshCacheStatsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(_paths.LogsRoot);

    [RelayCommand]
    private void OpenDatabaseFolder() => OpenFolder(Path.GetDirectoryName(_factory.DatabasePath) ?? _paths.AppDataRoot);

    [RelayCommand]
    private void OpenThumbnailsFolder() => OpenFolder(_paths.ThumbnailsRoot);

    [RelayCommand]
    private void CopyFfmpegCommand()
    {
        try
        {
            Clipboard.SetText(FfmpegInstallCommand);
            InfoMessage = "Befehl in die Zwischenablage kopiert.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Zwischenablage nicht verfügbar: {ex.Message}";
        }
    }

    // ---- Speicherort der Katalog-DB (ADR-0026) ------------------------------------------------------

    private bool CanRelocate() => !IsRelocating;

    private bool CanResetLocation() => !IsRelocating && UsesCustomLocation;

    [RelayCommand(CanExecute = nameof(CanRelocate))]
    private async Task MoveDatabaseAsync()
    {
        var folder = _dialogs.PickFolder("Neuer Speicherort für Katalog und Vorschaubilder");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await RelocateToAsync(folder).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanResetLocation))]
    private Task ResetDatabaseLocationAsync() => RelocateToAsync(_relocation.DefaultFolder);

    /// <summary>
    /// Zielordner prüfen, Modus erfragen (bei vorhandenem Katalog: übernehmen oder ersetzen), umziehen und neu starten;
    /// bei Fehler bleibt der alte Ort aktiv und ein Dialog nennt den Grund.
    /// </summary>
    internal async Task RelocateToAsync(string folder)
    {
        IsRelocating = true;
        RelocationStatus = "Zielordner wird geprüft…";
        var restarting = false;
        try
        {
            var check = await _relocation.CheckTargetAsync(folder).ConfigureAwait(true);
            var mode = await AskRelocationModeAsync(check).ConfigureAwait(true);
            if (mode is null)
            {
                return;
            }

            _restart.SetInputBlocked(true);
            var progress = new Progress<RelocationProgress>(p => RelocationStatus = p.Total > 0
                ? $"{p.Stage}… {p.Done:N0} von {p.Total:N0}"
                : $"{p.Stage}…");
            var result = await _relocation.RelocateAsync(check.TargetFolder, mode.Value, progress).ConfigureAwait(true);
            if (result.Success)
            {
                RelocationStatus = "Umzug abgeschlossen — Lumora startet neu…";
                restarting = _restart.Restart();
                if (!restarting)
                {
                    _restart.SetInputBlocked(false);
                    await _dialogs.ShowErrorAsync(
                        "Bitte Lumora neu starten",
                        $"Der Katalog liegt jetzt unter\n{result.DatabasePath}\n\nDer automatische Neustart ist fehlgeschlagen. Bitte " +
                        "Lumora beenden und neu starten — bis dahin ist der Katalog gesperrt.").ConfigureAwait(true);
                }

                return;
            }

            _restart.SetInputBlocked(false);
            await _dialogs.ShowErrorAsync(
                "Umzug fehlgeschlagen",
                $"{result.Error}\n\nLumora arbeitet weiter mit dem bisherigen Speicherort:\n{_relocation.CurrentFolder}").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _restart.SetInputBlocked(false);
            _logger.LogError(ex, "DB-Umzug nach {Folder} fehlgeschlagen", folder);
            await _dialogs.ShowErrorAsync("Umzug fehlgeschlagen", $"Unerwarteter Fehler: {ex.Message}").ConfigureAwait(true);
        }
        finally
        {
            if (!restarting)
            {
                IsRelocating = false;
                RelocationStatus = null;
            }
        }
    }

    /// <summary>Dialoge je Prüfergebnis; null = abgebrochen bzw. Ziel ungeeignet.</summary>
    private async Task<RelocationMode?> AskRelocationModeAsync(RelocationTargetCheck check)
    {
        var cloudHint = check.IsCloudSynced
            ? "\n\nAchtung: der Ordner wird von OneDrive synchronisiert. Synchronisierung während der Arbeit kann den Katalog " +
              "beschädigen — besser einen Ordner außerhalb von OneDrive wählen."
            : string.Empty;

        switch (check.State)
        {
            case RelocationTargetState.Invalid:
                await _dialogs.ShowErrorAsync("Speicherort nicht geeignet", check.Problem ?? "Der Ordner ist ungeeignet.").ConfigureAwait(true);
                return null;

            case RelocationTargetState.Ready:
                var ok = await _dialogs.ConfirmAsync(
                    "Katalog umziehen?",
                    $"Katalog (library.db) und {check.ThumbnailFiles:N0} Vorschaubilder werden nach\n{check.TargetFolder}\n" +
                    $"kopiert und geprüft (Platzbedarf etwa {Formatting.Bytes(check.RequiredBytes)}). Laufende Scans und " +
                    "Datei-Operationen werden vorher abgewartet.\n\nDanach startet Lumora neu und arbeitet mit dem neuen " +
                    "Ort; die alten Dateien werden beim Start gelöscht." + cloudHint,
                    "Umziehen und neu starten").ConfigureAwait(true);
                return ok ? RelocationMode.Move : null;
        }

        var existing = check.Existing!;
        var spaceHint = check.HasEnoughSpace ? string.Empty : $"\n\nErsetzen ist nicht möglich: {check.Problem}";
        if (existing is { HasDatabase: true, IsValidCatalog: true })
        {
            var modified = existing.ModifiedAtUtc is { } at ? $", zuletzt geändert {at.ToLocalTime():g}" : string.Empty;
            var choice = await _dialogs.ChooseAsync(
                "Im Zielordner liegt bereits ein Katalog",
                $"{check.TargetFolder}\nenthält einen Lumora-Katalog mit {existing.Libraries:N0} Bibliothek(en) und " +
                $"{existing.Files:N0} Dateien{modified}.\n\n" +
                "Übernehmen: Lumora arbeitet nach dem Neustart mit diesem Katalog; der bisherige bleibt unverändert unter\n" +
                $"{_relocation.CurrentFolder}\nerhalten.\n\n" +
                "Ersetzen: der vorhandene Katalog samt Vorschaubildern wird beim nächsten Start gelöscht und durch den aktuellen " +
                "ersetzt." + spaceHint + cloudHint,
                "Übernehmen",
                "Ersetzen",
                secondaryIsDestructive: true).ConfigureAwait(true);
            return choice switch
            {
                DialogChoice.Primary => RelocationMode.AdoptExisting,
                DialogChoice.Secondary => RelocationMode.ReplaceExisting,
                _ => null,
            };
        }

        var replace = await _dialogs.ConfirmAsync(
            "Zielordner enthält fremde Daten",
            $"{check.TargetFolder}\n{existing.Problem}\n\nErsetzen: diese Daten werden beim nächsten Start gelöscht und " +
            "durch den aktuellen Katalog samt Vorschaubildern ersetzt." + spaceHint + cloudHint,
            "Ersetzen",
            isDestructive: true).ConfigureAwait(true);
        return replace ? RelocationMode.ReplaceExisting : null;
    }


    private bool CanClearCache() => !IsCacheBusy;

    [RelayCommand(CanExecute = nameof(CanClearCache))]
    private async Task ClearThumbnailCacheAsync()
    {
        var ok = await _dialogs.ConfirmAsync(
            "Vorschaubild-Cache leeren?",
            "Alle erzeugten Vorschaubilder werden gelöscht und bei Bedarf neu erzeugt; bisher fehlgeschlagene werden " +
            "erneut versucht (z. B. nach Installation eines Codec-Pakets). Bereits angezeigte Kacheln behalten ihr Bild, " +
            "bis die Galerie sie neu aufbaut (Seiten-, Ordner- oder Bibliothekswechsel).\n\n" +
            "Fotos und Videos selbst sind nicht betroffen.",
            "Cache leeren",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        IsCacheBusy = true;
        try
        {
            // 0.5.40: hält den Vorschaubild-Dienst (Galerie, Pflege, Vorrat) zum Löschen der Zeilen kurz an, verwirft seinen
            // In-Memory-Zustand und meldet ThumbnailCacheClearedMessage — die Vorschaubilder auf Vorrat füllen den Cache wieder auf
            var (deleted, skipped, rows) = await _cacheCleaner.ClearAsync().ConfigureAwait(true);
            InfoMessage = $"{deleted:N0} Dateien und {rows:N0} Datenbankeinträge entfernt"
                          + (skipped > 0 ? $" ({skipped:N0} Dateien in Benutzung übersprungen)." : ".");
            _logger.LogInformation("Thumbnail-Cache geleert: {Deleted} Dateien, {Rows} Zeilen, {Skipped} übersprungen", deleted, rows, skipped);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Cache konnte nicht geleert werden: {ex.Message}";
            _logger.LogError(ex, "Thumbnail-Cache konnte nicht geleert werden");
        }
        finally
        {
            IsCacheBusy = false;
            await RefreshCacheStatsAsync().ConfigureAwait(true);
        }
    }

    private async void OnThemeRadioChanged(bool isChecked, AppThemeMode mode)
    {
        if (!isChecked || _theme.Mode == mode)
        {
            return;
        }

        try
        {
            await _theme.SetModeAsync(mode).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Farbschema konnte nicht gespeichert werden: {ex.Message}";
            _logger.LogError(ex, "Theme konnte nicht gesetzt werden: {Mode}", mode);
        }

        NotifyThemeChanged();
    }

    private void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    private async Task RefreshCacheStatsAsync()
    {
        ThumbnailCacheText = "wird berechnet…";
        try
        {
            var root = _paths.ThumbnailsRoot;
            var (count, bytes) = await Task.Run(() => MeasureDirectory(root)).ConfigureAwait(true);
            ThumbnailCacheText = count == 0 ? "leer" : $"{count:N0} Dateien · {Formatting.Bytes(bytes)}";
        }
        catch (Exception ex)
        {
            ThumbnailCacheText = "unbekannt";
            _logger.LogWarning(ex, "Thumbnail-Cache konnte nicht vermessen werden");
        }
    }

    private static (int Count, long Bytes) MeasureDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var count = 0;
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            count++;
            bytes += file.Length;
        }

        return (count, bytes);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Lumora.Shell.HiddenLibraries.HiddenSessionPrivacy.ThrowIfShellBlocked(); // versteckte Sitzung: keine Shell-Spuren (ADR-0049)
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogWarning(ex, "Explorer konnte nicht geöffnet werden: {Path}", path);
        }
    }

    private static string FileSizeText(string path)
    {
        try
        {
            return File.Exists(path) ? Formatting.Bytes(new FileInfo(path).Length) : "noch nicht angelegt";
        }
        catch (IOException)
        {
            return "unbekannt";
        }
    }

    private static string BuildVersionText()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "?"
            : informational;

        var location = assembly.Location;
        var buildDate = !string.IsNullOrEmpty(location) && File.Exists(location)
            ? $" · Build vom {File.GetLastWriteTime(location):g}"
            : string.Empty;

        return $"Lumora {version}{buildDate}";
    }
}
