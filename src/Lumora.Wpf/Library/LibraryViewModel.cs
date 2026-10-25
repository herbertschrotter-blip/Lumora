using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Shell;

namespace Lumora.Libraries;

/// <summary>
/// Bibliotheksverwaltung: Liste aller Bibliotheken, Anlegen (Ordnerdialog), Scannen mit Fortschrittspanel und
/// Abbruch, Aktivieren, Umbenennen, Entfernen. Primäraktion je Bibliothek: „Scannen“ (Fresh) bei leerem Katalog,
/// sonst „Aktualisieren“ (Incremental, Favoriten/Bewertungen bleiben); Sekundäraktion „Neu aufbauen“
/// (ClearAndRescan nach Bestätigung). Offline-vs-Missing-Guard (PLAN Kap. 12): bei
/// <see cref="ScanOutcome.MassMissingBlocked"/> Rückfrage [Erneut prüfen] [Als fehlend markieren] [Abbrechen],
/// bei <see cref="ScanOutcome.LibraryOffline"/> [Erneut prüfen] [Andere Bibliothek wählen].
/// Nach einem Scan mit Änderungen wird automatisch zur Galerie gewechselt.
/// Wartet der Scan auf einen anderen Scan oder Datei-Batch derselben Bibliothek (Scan-Sperre, ADR-0025), zeigt das
/// Panel bis zum Start „Wartet auf laufenden Scan…“ bzw. „Wartet auf laufende Datei-Operation…“ (0.2.22).
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private static readonly TimeSpan ElapsedTickInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryService _libraries;
    private readonly IScanService _scanner;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _scanStopwatch = new();
    private CancellationTokenSource? _scanCts;

    /// <summary>
    /// Kennung des laufenden eigenen Scans für die Scan-Sperre (0.2.29, <see cref="ScanOptions.GateCallerId"/>), solange
    /// er die Bibliothek noch nicht hat — nur ein Warten mit dieser Kennung ist das eigene.
    /// </summary>
    private Guid? _gateCallerId;

    /// <summary>Das Warten auf die Scan-Sperre, das das Panel gerade anzeigt.</summary>
    private long? _gateWaitId;

    /// <summary>Das angezeigte Warten endete ohne Sperre (Abbruch) — der Scan hat nichts angefasst.</summary>
    private bool _gateWaitAborted;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="folderActions">„In die Bibliothek übernehmen…“ je Eingang (ADR-0034); optional für Tests.</param>
    public LibraryViewModel(
        ILibraryService libraries,
        IScanService scanner,
        IDialogService dialogs,
        INavigationService navigation,
        ILogger<LibraryViewModel> logger,
        IMessenger? messenger = null,
        Folders.FolderActions? folderActions = null,
        ILibraryLinkService? linking = null,
        ILibraryScope? libraryScope = null,
        Linking.ILibraryLinkDialogService? linkDialogs = null,
        Services.Scanning.Exclusions.IScanExclusionService? exclusions = null)
    {
        _exclusions = exclusions; // Rückfrage „Neu aufbauen“ mit Einträgen in ausgeschlossenen Ordnern (#22, LibraryViewModel.Rebuild.cs)
        _linking = linking; // „Verknüpfen mit…“ / „Verknüpfung lösen“ (ADR-0048, LibraryViewModel.Linking.cs)
        _libraryScope = libraryScope;
        _linkDialogs = linkDialogs;
        _folderActions = folderActions;
        _libraries = libraries;
        _scanner = scanner;
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _elapsedTimer = new DispatcherTimer(ElapsedTickInterval, DispatcherPriority.Background, OnElapsedTick, dispatcher);
        _elapsedTimer.Stop();

        var bus = messenger ?? WeakReferenceMessenger.Default;
        bus.Register<LibraryViewModel, ScanGateWaitStartedMessage>(this, static (r, m) => UiThread.Post(() => r.OnGateWaitStarted(m.Wait)));
        bus.Register<LibraryViewModel, ScanGateWaitEndedMessage>(this, static (r, m) => UiThread.Post(() => r.OnGateWaitEnded(m)));
    }

    public ObservableCollection<LibraryListItem> Libraries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isErrorOpen;

    // ---- Scan-Panel ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanPanelVisible))]
    [NotifyCanExecuteChangedFor(
        nameof(ScanCommand), nameof(RebuildCommand), nameof(RemoveCommand), nameof(CreateLibraryCommand), nameof(SetActiveCommand), nameof(RenameCommand), nameof(CancelScanCommand),
        nameof(AddInboxRootCommand), nameof(MakeArchiveRootCommand), nameof(RenameRootCommand), nameof(RemoveRootCommand), nameof(AdoptInboxCommand),
        nameof(LinkCommand), nameof(UnlinkCommand), nameof(RenameGroupCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string? _scanLibraryName;

    /// <summary>Verb der laufenden Aktion: „Scanne“, „Aktualisiere“ oder „Baue neu auf“.</summary>
    [ObservableProperty]
    private string _scanActionText = "Scanne";

    [ObservableProperty]
    private string _scanPhaseText = string.Empty;

    /// <summary>true, solange der eigene Scan auf die Sperre der Bibliothek wartet (ScanPhaseText = „Wartet auf …“).</summary>
    [ObservableProperty]
    private bool _isWaitingForGate;

    [ObservableProperty]
    private int _scanFolders;

    [ObservableProperty]
    private int _scanFiles;

    [ObservableProperty]
    private string _scanSizeText = "0 B";

    [ObservableProperty]
    private string? _scanCurrentPath;

    [ObservableProperty]
    private string _scanElapsedText = "0,0 s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanPanelVisible))]
    private bool _isScanSummaryOpen;

    [ObservableProperty]
    private string _scanSummaryTitle = string.Empty;

    [ObservableProperty]
    private string? _scanSummary;

    [ObservableProperty]
    private bool _scanSummaryIsWarning;

    public bool IsScanPanelVisible => IsScanning || IsScanSummaryOpen;

    public bool IsEmptyStateVisible => !IsLoading && Libraries.Count == 0;

    partial void OnErrorMessageChanged(string? value) => IsErrorOpen = !string.IsNullOrEmpty(value);

    private bool CanModify() => !IsScanning;

    private bool CanModifyItem(LibraryListItem? item) => !IsScanning;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var all = await _libraries.GetAllAsync().ConfigureAwait(true);

            // Aktiv = jüngstes LastOpenedAt, sonst jüngstes CreatedAt (gleiche Regel wie ILibraryService.GetActiveAsync)
            var activeId = all
                .OrderByDescending(l => l.LastOpenedAt ?? DateTime.MinValue)
                .ThenByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id)
                .Select(l => (int?)l.Id)
                .FirstOrDefault();

            var groups = await LoadGroupsAsync().ConfigureAwait(true); // Verknüpfungen (ADR-0048)
            Libraries.Clear();
            foreach (var info in all)
            {
                var item = new LibraryListItem(info, info.Id == activeId);
                ApplyGroup(item, all, groups);
                Libraries.Add(item);
            }

            _logger.LogInformation("Bibliotheken geladen: {Count} (aktiv: {ActiveId})", all.Count, activeId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliotheken konnten nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Bibliotheken konnten nicht geladen werden");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmptyStateVisible));
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task CreateLibraryAsync()
    {
        var folder = _dialogs.PickFolder("Foto-Ordner für die neue Bibliothek wählen");
        if (folder is null)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync(
            "Neue Bibliothek",
            $"Ordner: {folder}\n\nName der Bibliothek:",
            LibraryService.SuggestName(folder),
            "Anlegen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        LibraryInfo info;
        try
        {
            info = await _libraries.CreateAsync(name, folder).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht angelegt werden: {ex.Message}";
            _logger.LogError(ex, "Bibliothek konnte nicht angelegt werden: {Folder}", folder);
            return;
        }

        ErrorMessage = null;
        await LoadAsync().ConfigureAwait(true);

        var scanNow = await _dialogs.ConfirmAsync(
            "Bibliothek angelegt",
            $"„{info.Name}“ wurde angelegt. Soll der Ordner jetzt durchsucht und der Katalog aufgebaut werden?",
            "Jetzt scannen",
            cancelText: "Später").ConfigureAwait(true);
        if (scanNow && Libraries.FirstOrDefault(l => l.Id == info.Id) is { } item)
        {
            await StartScanAsync(item, ScanMode.Fresh).ConfigureAwait(true);
        }
    }

    /// <summary>Primäraktion: leerer Katalog → Fresh, sonst Incremental (Änderungen übernehmen, Ids/Favoriten bleiben).</summary>
    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task ScanAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        await StartScanAsync(item, item.IsEmpty ? ScanMode.Fresh : ScanMode.Incremental).ConfigureAwait(true);
    }

    /// <summary>Sekundäraktion: Katalog löschen und neu aufbauen (ClearAndRescan) — nach Bestätigung, FileIds werden neu vergeben.</summary>
    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task RebuildAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsEmpty)
        {
            await StartScanAsync(item, ScanMode.Fresh).ConfigureAwait(true);
            return;
        }

        var options = await ConfirmRebuildAsync(item).ConfigureAwait(true); // #22: mit Einträgen in ausgeschlossenen Ordnern
        if (options is null)
        {
            return;
        }

        await StartScanAsync(item, ScanMode.ClearAndRescan, options).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        _logger.LogInformation("Scan-Abbruch angefordert");
        _scanCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task SetActiveAsync(LibraryListItem? item)
    {
        if (item is null || item.IsActive)
        {
            return;
        }

        try
        {
            await _libraries.SetActiveAsync(item.Id).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht aktiviert werden: {ex.Message}";
            _logger.LogError(ex, "SetActive fehlgeschlagen: {Id}", item.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task RenameAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        var newName = await _dialogs.PromptTextAsync("Bibliothek umbenennen", "Neuer Name:", item.Name, "Umbenennen")
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name)
        {
            return;
        }

        try
        {
            await _libraries.RenameAsync(item.Id, newName).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht umbenannt werden: {ex.Message}";
            _logger.LogError(ex, "Rename fehlgeschlagen: {Id}", item.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task RemoveAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Bibliothek aus dem Katalog entfernen?",
            $"„{item.Name}“ wird mit allen Katalogdaten (Ordner, Dateien, Vorschaubilder, Favoriten, Bewertungen) " +
            "aus dem Katalog entfernt.\n\n" +
            "Die Fotos und Videos auf der Festplatte bleiben unverändert — es werden keine Dateien gelöscht.",
            "Entfernen",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        try
        {
            await _libraries.DeleteAsync(item.Id).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht entfernt werden: {ex.Message}";
            _logger.LogError(ex, "Delete fehlgeschlagen: {Id}", item.Id);
        }
    }

    // ---- Wurzeln (ADR-0020) ----

    private bool CanModifyRoot(LibraryRootListItem? root) => !IsScanning && !IsAdopting;

    /// <summary>Eingangs-Wurzel per Ordnerdialog hinzufügen; danach Angebot, die Bibliothek zu aktualisieren (Scan über alle Wurzeln).</summary>
    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task AddInboxRootAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        var folder = _dialogs.PickFolder($"Eingangs-Ordner für „{item.Name}“ wählen");
        if (folder is null)
        {
            return;
        }

        try
        {
            await _libraries.AddRootAsync(item.Id, folder, LibraryRootRole.Inbox).ConfigureAwait(true);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Eingang konnte nicht hinzugefügt werden: {ex.Message}";
            _logger.LogError(ex, "AddRoot fehlgeschlagen: {Folder}", folder);
            return;
        }

        await LoadAsync().ConfigureAwait(true);

        var scanNow = await _dialogs.ConfirmAsync(
            "Eingang hinzugefügt",
            $"„{folder}“ ist jetzt ein Eingangs-Ordner von „{item.Name}“. Soll die Bibliothek jetzt aktualisiert werden, damit die Dateien im Katalog erscheinen?",
            "Jetzt aktualisieren",
            cancelText: "Später").ConfigureAwait(true);
        if (scanNow && Libraries.FirstOrDefault(l => l.Id == item.Id) is { } refreshed)
        {
            await StartScanAsync(refreshed, refreshed.IsEmpty ? ScanMode.Fresh : ScanMode.Incremental).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyRoot))]
    private async Task MakeArchiveRootAsync(LibraryRootListItem? root)
    {
        if (root is null || root.IsArchive)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Bibliotheksordner wechseln?",
            $"„{root.Path}“ wird der Bibliotheksordner von „{root.Library.Name}“; der bisherige Bibliotheksordner „{root.Library.RootPath}“ wird zum Eingang. " +
            "Dateien werden nicht verschoben — nur die Rollen ändern sich (Filter „Noch im Eingang“/„In der Bibliothek“ und Ziel von „In die Bibliothek…“).",
            "Zum Bibliotheksordner machen").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        try
        {
            await _libraries.SetRootRoleAsync(root.Id, LibraryRootRole.Archive).ConfigureAwait(true);
            ErrorMessage = null;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rolle konnte nicht geändert werden: {ex.Message}";
            _logger.LogError(ex, "SetRootRole fehlgeschlagen: {RootId}", root.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyRoot))]
    private async Task RenameRootAsync(LibraryRootListItem? root)
    {
        if (root is null)
        {
            return;
        }

        var newName = await _dialogs.PromptTextAsync("Wurzel umbenennen", $"Anzeigename für „{root.Path}“:", root.Name, "Umbenennen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == root.Name)
        {
            return;
        }

        try
        {
            await _libraries.RenameRootAsync(root.Id, newName).ConfigureAwait(true);
            ErrorMessage = null;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Wurzel konnte nicht umbenannt werden: {ex.Message}";
            _logger.LogError(ex, "RenameRoot fehlgeschlagen: {RootId}", root.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyRoot))]
    private async Task RemoveRootAsync(LibraryRootListItem? root)
    {
        if (root is null || !root.CanRemove)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Eingang aus dem Katalog entfernen?",
            $"„{root.Path}“ wird mit seinen Katalogdaten (Ordner, Dateien, Vorschaubilder, Favoriten, Bewertungen) aus der Bibliothek entfernt — " +
            "nur aus dem Katalog.\n\nDie Fotos und Videos auf der Festplatte bleiben unverändert — es werden keine Dateien gelöscht.",
            "Entfernen",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        try
        {
            await _libraries.RemoveRootAsync(root.Id).ConfigureAwait(true);
            ErrorMessage = null;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Eingang konnte nicht entfernt werden: {ex.Message}";
            _logger.LogError(ex, "RemoveRoot fehlgeschlagen: {RootId}", root.Id);
        }
    }

    [RelayCommand]
    private void GoToGallery() => _navigation.NavigateTo(NavigationTags.Gallery);

    [RelayCommand]
    private void DismissScanSummary() => IsScanSummaryOpen = false;

    /// <summary>
    /// Scan ohne weitere Rückfragen starten (Aufrufer hat bestätigt). Aktiviert die Bibliothek vorher, falls nötig.
    /// Führt die Dialoge des Offline-vs-Missing-Guards (PLAN Kap. 12) und wiederholt den Scan je nach Antwort.
    /// </summary>
    internal async Task StartScanAsync(LibraryListItem item, ScanMode mode, ScanOptions? initialOptions = null)
    {
        if (IsScanning)
        {
            return;
        }

        if (!item.IsActive)
        {
            // Galerie und ScanCompletedMessage beziehen sich auf die aktive Bibliothek → vor dem Scan aktivieren
            await _libraries.SetActiveAsync(item.Id).ConfigureAwait(true);
        }

        var options = initialOptions ?? ScanOptions.Default;
        while (true)
        {
            var result = await RunScanOnceAsync(item, mode, options).ConfigureAwait(true);
            if (result is null)
            {
                return; // abgebrochen oder fehlgeschlagen — bereits gemeldet
            }

            switch (result.Outcome)
            {
                case ScanOutcome.MassMissingBlocked:
                {
                    var choice = await _dialogs.ChooseAsync(
                        "Ungewöhnlich viele Dateien fehlen",
                        $"Beim Scan wurden {result.MissingDetected:N0} fehlende Dateien erkannt. " +
                        "Das kann passieren, wenn ein Laufwerk nicht vollständig verfügbar ist.",
                        "Erneut prüfen",
                        "Als fehlend markieren",
                        "Abbrechen",
                        secondaryIsDestructive: true).ConfigureAwait(true);

                    if (choice == DialogChoice.Primary)
                    {
                        mode = ScanMode.Incremental;
                        options = ScanOptions.Default;
                        continue;
                    }

                    if (choice == DialogChoice.Secondary)
                    {
                        _logger.LogInformation("Mass-Missing bestätigt: {Count} Dateien werden als fehlend markiert", result.MissingDetected);
                        mode = ScanMode.Incremental;
                        options = new ScanOptions { ConfirmMassMissing = true };
                        continue;
                    }

                    ShowSummary(
                        "Scan abgebrochen",
                        $"„{item.Name}“: {result.MissingDetected:N0} fehlende Dateien wurden nicht markiert. " +
                        $"Übernommen wurden {ChangesText(result)}",
                        warning: true);
                    return;
                }

                case ScanOutcome.LibraryOffline:
                {
                    var retry = await _dialogs.ConfirmAsync(
                        "Bibliothek offline",
                        $"{result.RootPath} ist nicht erreichbar.\nBitte Laufwerk anschließen oder Netzwerkverbindung prüfen.",
                        "Erneut prüfen",
                        cancelText: "Andere Bibliothek wählen").ConfigureAwait(true);
                    if (retry)
                    {
                        continue;
                    }

                    ShowSummary(
                        "Bibliothek offline",
                        $"„{item.Name}“: {result.RootPath} ist nicht erreichbar. " +
                        (result.HasChanges
                            ? $"Bis zum Abbruch übernommen: {ChangesText(result)} Es wurde nichts als fehlend markiert."
                            : "Der Katalog wurde nicht verändert."),
                        warning: true);
                    _navigation.NavigateTo(NavigationTags.Library);
                    return;
                }

                default:
                {
                    ShowSummary(
                        result.Mode == ScanMode.Incremental ? "Aktualisierung abgeschlossen" : "Scan abgeschlossen",
                        CompletedSummary(item, result),
                        warning: false);
                    // Von der Seite „Ordner“ aus („Eingang hinzufügen…“, ADR-0042) dort bleiben — sie zeigt die Galerie schon
                    if ((result.Mode != ScanMode.Incremental || result.HasChanges) && _navigation.CurrentTag != NavigationTags.Folders)
                    {
                        _navigation.NavigateTo(NavigationTags.Gallery);
                    }

                    return;
                }
            }
        }
    }

    /// <summary>Ein Scan-Durchlauf mit Fortschrittspanel; null bei Abbruch oder Fehler (beides bereits gemeldet).</summary>
    private async Task<ScanResult?> RunScanOnceAsync(LibraryListItem item, ScanMode mode, ScanOptions options)
    {
        ErrorMessage = null;
        IsScanSummaryOpen = false;
        ScanLibraryName = item.Name;
        ScanActionText = mode switch
        {
            ScanMode.Incremental => "Aktualisiere",
            ScanMode.ClearAndRescan => "Baue neu auf:",
            _ => "Scanne",
        };
        ScanPhaseText = Formatting.ScanPhase(ScanPhase.Enumerating);
        ScanFolders = 0;
        ScanFiles = 0;
        ScanSizeText = Formatting.Bytes(0);
        ScanCurrentPath = null;
        ScanElapsedText = Formatting.Duration(TimeSpan.Zero);

        _scanCts = new CancellationTokenSource();
        var callerId = Guid.NewGuid();
        options = options with { GateCallerId = callerId };
        ResetGateWait(callerId);
        IsScanning = true;
        _scanStopwatch.Restart();
        _elapsedTimer.Start();

        // Progress<T> stellt die Callbacks auf den UI-Thread (SynchronizationContext beim Erzeugen)
        var progress = new Progress<ScanProgress>(ApplyProgress);
        _logger.LogInformation(
            "Scan gestartet: {Name} (Id={Id}) Modus={Mode} ConfirmMassMissing={Confirm}",
            item.Name, item.Id, mode, options.ConfirmMassMissing);

        try
        {
            var result = await _scanner.ScanAsync(item.Id, mode, options, progress, _scanCts.Token).ConfigureAwait(true);
            _logger.LogInformation(
                "Scan beendet: {Name} — {Outcome}: {Added} neu, {Modified} geändert, {Missing} fehlend ({Detected} erkannt), {Reappeared} wieder da, {Folders} neue Ordner in {Sec:N1} s",
                item.Name, result.Outcome, result.Added, result.Modified, result.MarkedMissing, result.MissingDetected, result.Reappeared, result.FoldersAdded, result.TotalDuration.TotalSeconds);
            return result;
        }
        catch (OperationCanceledException) when (IsWaitingForGate || _gateWaitAborted)
        {
            ShowSummary("Scan abgebrochen", "Der Scan wurde abgebrochen, bevor er begann; der Katalog ist unverändert.", warning: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            ShowSummary(
                "Scan abgebrochen",
                mode == ScanMode.Incremental
                    ? "Die Aktualisierung wurde abgebrochen. Bereits übernommene Änderungen bleiben erhalten; es wurde nichts als fehlend markiert."
                    : "Der Scan wurde abgebrochen; unvollständige Daten wurden verworfen.",
                warning: true);
            return null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Scan fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Scan fehlgeschlagen: {Name} (Id={Id})", item.Name, item.Id);
            return null;
        }
        finally
        {
            _elapsedTimer.Stop();
            _scanStopwatch.Stop();
            ResetGateWait(null);
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Rauchtest-Pfad (SmokeTestRunner): Bibliothek auf rootPath anlegen, falls nötig, und wie per Primärknopf scannen (Fresh bzw. Incremental).</summary>
    internal async Task RunSmokeScanAsync(string rootPath)
    {
        var item = await EnsureSmokeLibraryAsync(rootPath).ConfigureAwait(true);
        await StartScanAsync(item, item.IsEmpty ? ScanMode.Fresh : ScanMode.Incremental).ConfigureAwait(true);
    }

    /// <summary>Rauchtest-Pfad: Katalog der Bibliothek auf rootPath neu aufbauen (ClearAndRescan ohne Rückfrage).</summary>
    internal async Task RunSmokeRebuildAsync(string rootPath)
    {
        var item = await EnsureSmokeLibraryAsync(rootPath).ConfigureAwait(true);
        await StartScanAsync(item, item.IsEmpty ? ScanMode.Fresh : ScanMode.ClearAndRescan).ConfigureAwait(true);
    }

    /// <summary>Rauchtest-Pfad: Bibliothek auf rootPath wieder aus dem Katalog entfernen.</summary>
    internal async Task RunSmokeCleanupAsync(string rootPath)
    {
        await LoadAsync().ConfigureAwait(true);

        var item = FindByRoot(rootPath);
        if (item is null)
        {
            return;
        }

        await _libraries.DeleteAsync(item.Id).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task<LibraryListItem> EnsureSmokeLibraryAsync(string rootPath)
    {
        await LoadAsync().ConfigureAwait(true);

        var item = FindByRoot(rootPath);
        if (item is not null)
        {
            return item;
        }

        var normalized = LibraryService.NormalizeRootPath(rootPath);
        var info = await _libraries.CreateAsync(LibraryService.SuggestName(normalized), normalized).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        return Libraries.First(l => l.Id == info.Id);
    }

    private LibraryListItem? FindByRoot(string rootPath)
    {
        var normalized = LibraryService.NormalizeRootPath(rootPath);
        return Libraries.FirstOrDefault(l =>
            string.Equals(LibraryService.NormalizeRootPath(l.RootPath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Warten auf die Scan-Sperre (0.2.22) ------------------------------------------------------

    private void ResetGateWait(Guid? callerId)
    {
        _gateCallerId = callerId;
        _gateWaitId = null;
        _gateWaitAborted = false;
        IsWaitingForGate = false;
    }

    /// <summary>
    /// Nur das Warten mit der Kennung des eigenen Scans zählt (0.2.29): ein automatischer Scan der Ordnerüberwachung
    /// oder ein anderer Scan derselben Bibliothek, der zugleich wartet, trägt eine andere bzw. keine Kennung.
    /// </summary>
    private void OnGateWaitStarted(ScanGateWait wait)
    {
        if (wait.Waiter != ScanGateUse.Scan || _gateCallerId is null || wait.CallerId != _gateCallerId || _gateWaitId is not null || !IsScanning)
        {
            return;
        }

        _gateWaitId = wait.WaitId;
        IsWaitingForGate = true;
        ScanPhaseText = Formatting.GateWait(wait.Holder);
        _logger.LogInformation("Scan wartet auf die Bibliothek {LibraryId} ({Holder} läuft)", wait.LibraryId, wait.Holder);
    }

    private void OnGateWaitEnded(ScanGateWaitEndedMessage message)
    {
        if (_gateWaitId != message.Wait.WaitId)
        {
            return;
        }

        _gateWaitId = null;
        IsWaitingForGate = false;
        if (message.Acquired)
        {
            _gateCallerId = null; // der eigene Scan läuft
            ScanPhaseText = Formatting.ScanPhase(ScanPhase.Enumerating);
        }
        else
        {
            _gateWaitAborted = true; // die Abbruch-Ausnahme des Scans kommt gleich hinterher
        }
    }

    private void ApplyProgress(ScanProgress p)
    {
        ResetGateWait(null);
        ScanFolders = p.FoldersDiscovered;
        ScanFiles = p.FilesDiscovered;
        ScanSizeText = Formatting.Bytes(p.TotalBytesDiscovered);
        ScanCurrentPath = p.CurrentPath;
        ScanPhaseText = Formatting.ScanPhase(p.Phase);
    }

    private void ShowSummary(string title, string text, bool warning)
    {
        ScanSummaryTitle = title;
        ScanSummary = text;
        ScanSummaryIsWarning = warning;
        IsScanSummaryOpen = true;
    }

    private static string CompletedSummary(LibraryListItem item, ScanResult result)
    {
        if (result.Mode != ScanMode.Incremental)
        {
            return $"„{item.Name}“: {result.FilesInserted:N0} Dateien in {result.FoldersInserted:N0} Ordnern " +
                   $"({Formatting.Bytes(result.TotalBytes)}) in {Formatting.Duration(result.TotalDuration)} katalogisiert." +
                   KeptExcludedText(result); // #22
        }

        if (!result.HasChanges)
        {
            return $"„{item.Name}“ ist aktuell — keine Änderungen gefunden ({Formatting.Duration(result.TotalDuration)}).";
        }

        return $"„{item.Name}“ in {Formatting.Duration(result.TotalDuration)} aktualisiert: {ChangesText(result)}";
    }

    /// <summary>Zähler des Incremental-Scans als Satzende, z. B. „3 neu, 1 geändert, 2 fehlend, 0 wieder vorhanden, 1 neuer Ordner.“</summary>
    private static string ChangesText(ScanResult r) =>
        $"{r.Added:N0} neu, {r.Modified:N0} geändert, {r.MarkedMissing:N0} fehlend, {r.Reappeared:N0} wieder vorhanden, " +
        (r.FoldersAdded == 1 ? "1 neuer Ordner." : $"{r.FoldersAdded:N0} neue Ordner.");

    private void OnElapsedTick(object? sender, EventArgs e) =>
        ScanElapsedText = Formatting.Duration(_scanStopwatch.Elapsed);
}
