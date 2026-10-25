using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Archive;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;

namespace Lumora.Shell;

/// <summary>
/// Statusleiste gemäß ADR-0013 mit den vier Indikatoren: (1) Scan-Fortschritt (ScanProgress-/ScanCompleted-/
/// ScanAbortedMessage), (2) Thumbnail-Warteschlange (IThumbnailService.PendingCount, alle 500 ms),
/// (3) Hintergrund-Aktivität und (4) Recovery-Hinweise: unvollständige oder fehlgeschlagene OperationBatches
/// (IFileOperationService.GetRecentBatchesAsync — nur Anzeige) sowie „Bibliothek offline“. Dazu (0.2.22) der Hinweis
/// „Wartet auf laufenden Scan…“ mit „Abbrechen“, solange eine Datei-Operation auf die Scan-Sperre wartet (ADR-0025),
/// sowie Hinweise der Ordnerüberwachung und des automatischen Entpackens (ArchiveAutoExtractStatusMessage, ADR-0032).
/// Alle Messenger-Nachrichten werden auf den UI-Thread dispatcht.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ThumbnailPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ResultDisplayDuration = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Batch-Status zusätzlich periodisch prüfen: ein komplett fehlgeschlagener Batch ändert keine Datei und
    /// sendet deshalb keine FilesChangedMessage — ohne Poll bliebe der Hinweis aus (eine 50-Zeilen-Query).
    /// </summary>
    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromSeconds(10);
    private const int RecoveryBatchWindow = 50;
    private const int RecoveryDetailLines = 8;

    private readonly IThumbnailService _thumbnails;
    private readonly ILibraryService _libraries;
    private readonly ILibraryScope? _libraryScope;
    private readonly IFileOperationService _fileOperations;
    private readonly ILogger<StatusBarViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly DispatcherTimer _thumbnailTimer;
    private readonly DispatcherTimer _resultTimer;
    private readonly DispatcherTimer _recoveryTimer;
    private bool _recoveryRefreshRunning;

    [ObservableProperty] private string? _activeLibraryName;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _scanText;
    [ObservableProperty] private string? _scanResultText;
    [ObservableProperty] private bool _scanResultIsError;
    [ObservableProperty] private int _thumbnailPending;
    [ObservableProperty] private bool _hasThumbnailWork;
    [ObservableProperty] private string? _hintText;
    [ObservableProperty] private string? _backgroundActivityText;
    [ObservableProperty] private bool _isBackgroundActive;

    /// <summary>Recovery-Hinweis (ADR-0013 Nr. 4): unvollständige/fehlgeschlagene Dateivorgänge; null = keine.</summary>
    [ObservableProperty] private string? _recoveryText;

    /// <summary>Tooltip zum Recovery-Hinweis: die betroffenen Batches.</summary>
    [ObservableProperty] private string? _recoveryDetails;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public StatusBarViewModel(
        IThumbnailService thumbnails,
        ILibraryService libraries,
        IFileOperationService fileOperations,
        IScanGate scanGate,
        ILogger<StatusBarViewModel> logger,
        IMessenger? messenger = null,
        Lumora.Services.Faces.Indexing.IFaceIndexService? faceIndex = null,
        Lumora.Services.Motifs.IMotifIndexService? motifIndex = null,
        ILibraryScope? libraryScope = null,
        Lumora.Duplicates.IDuplicatesPageNavigator? duplicates = null,
        Lumora.Services.HiddenLibraries.SecurityCheck.ISecurityCheckService? securityCheck = null,
        Lumora.Shell.Activity.ActivityStatusViewModel? activities = null)
    {
        Activities = activities; // Aktivitäts-Anzeige ganz rechts (ADR-0067)
        _libraryScope = libraryScope; // Anzeige „Alle verknüpften (Name)“ (ADR-0048)
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _thumbnails = thumbnails;
        _libraries = libraries;
        _fileOperations = fileOperations;
        _logger = logger;
        FileOperationWait = new ScanGateWaitIndicator(scanGate, static w => w.Waiter == ScanGateUse.FileOperation, _messenger);
        FileOperationWait.Ended += OnFileOperationWaitEnded;

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _thumbnailTimer = new DispatcherTimer(ThumbnailPollInterval, DispatcherPriority.Background, OnThumbnailTick, dispatcher);
        _resultTimer = new DispatcherTimer(ResultDisplayDuration, DispatcherPriority.Background, OnResultTick, dispatcher);
        _resultTimer.Stop();
        _recoveryTimer = new DispatcherTimer(RecoveryPollInterval, DispatcherPriority.Background, (_, _) => _ = RefreshRecoveryAsync(), dispatcher);
        _watchTimer = new DispatcherTimer(WatchTickInterval, DispatcherPriority.Background, OnWatchTick, dispatcher);
        _watchTimer.Stop();
        _messenger.Register<StatusBarViewModel, WatchStatusChangedMessage>(this, static (r, m) => r.QueueWatchStatus(m.Status));

        _messenger.Register<StatusBarViewModel, ScanProgressMessage>(this, static (r, m) => UiThread.Post(() => r.OnScanProgress(m)));
        _messenger.Register<StatusBarViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => r.OnScanCompleted(m)));
        _messenger.Register<StatusBarViewModel, ScanAbortedMessage>(this, static (r, m) => UiThread.Post(() => r.OnScanAborted(m)));
        _messenger.Register<StatusBarViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.RefreshLibraryAsync(); }));
        // Jede Datei-Operation endet mit einer FilesChangedMessage → Batch-Status neu prüfen (billig: 50 Zeilen).
        _messenger.Register<StatusBarViewModel, FilesChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.RefreshRecoveryAsync(); }));

        _archiveTimer = new DispatcherTimer(ArchiveWarningDisplayDuration, DispatcherPriority.Background, (_, _) => ClearArchiveText(), dispatcher);
        _archiveTimer.Stop();
        _messenger.Register<StatusBarViewModel, ArchiveAutoExtractStatusMessage>(this, static (r, m) => UiThread.Post(() => r.OnAutoExtractStatus(m.Status)));
        InitCatalogWriteWait(); // Schreibvorgänge warten auf die Katalog-DB (ADR-0030, StatusBarViewModel.CatalogWriteWait.cs)
        InitFaceIndex(faceIndex); // Gesichts-Index (ADR-0035, StatusBarViewModel.FaceIndex.cs)
        InitMotifIndex(motifIndex); // Motiv-Index (ADR-0052, StatusBarViewModel.MotifIndex.cs)
        InitCaptureDate(); // Aufnahmedatum nachtragen (ADR-0046, StatusBarViewModel.CaptureDate.cs)
        InitPlaces(); // Orte nachtragen (ADR-0050, StatusBarViewModel.Places.cs)
        InitDuplicates(duplicates); // Doppelte: Fortschritt + Eingang-Hinweis (ADR-0053, StatusBarViewModel.Duplicates.cs)
        InitQuality(); // Foto-Güte: Fortschritt (ADR-0057, StatusBarViewModel.Quality.cs)
        InitStories(); // Story-Erkennung: „Storys aktuell“ (ADR-0060, StatusBarViewModel.Stories.cs)
        InitGeoTracks(); // Ungenaue Orte und GPS-Spuren (ADR-0062, StatusBarViewModel.GeoTracks.cs)
        InitSecurityCheck(securityCheck); // Sicherheits-Check nur in der versteckten Sitzung (ADR-0064, StatusBarViewModel.SecurityCheck.cs)
        if (activities is not null)
        {
            _thumbnailTimer.Stop(); // Vorschaubild-Warteschlange zeigt seit 0.4.36 die Aktivitäts-Anzeige (ADR-0067) — kein zweiter Abfrage-Timer
        }
    }

    /// <summary>Datei-Operationen, die auf einen laufenden Scan derselben Bibliothek warten (Hinweis + „Abbrechen“).</summary>
    public ScanGateWaitIndicator FileOperationWait { get; }

    /// <summary>
    /// Aktivitäts-Anzeige ganz rechts (ADR-0067, Mockup 15): ersetzt seit 0.4.36 die Einzelanzeigen für Scan, Vorschaubilder,
    /// Entpacken, Indizes, Wartezustände usw. Deren Texte oben bleiben als Zustand erhalten (Tests, Rauchtest), werden aber
    /// nicht mehr einzeln gezeigt. null in Tests ohne Zentrale.
    /// </summary>
    public Lumora.Shell.Activity.ActivityStatusViewModel? Activities { get; }

    /// <summary>Einmal nach dem Laden des Hauptfensters: aktive Bibliothek, Offline-Hinweis und Batch-Status ermitteln.</summary>
    public async Task InitializeAsync()
    {
        await RefreshLibraryAsync().ConfigureAwait(true);
        await RefreshRecoveryAsync().ConfigureAwait(true);
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
            var scope = _libraryScope is null ? null : await _libraryScope.GetCurrentAsync().ConfigureAwait(true);
            ActiveLibraryName = scope is { IsAllLinked: true } ? $"Alle verknüpften ({scope.GroupName})" : active?.Name;
            HintText = active is { IsRootAvailable: false }
                ? $"Bibliothek offline: {active.RootPath} ist nicht erreichbar"
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Statusleiste: aktive Bibliothek konnte nicht ermittelt werden");
        }
    }

    /// <summary>
    /// Recovery-Hinweis: Batches, die nicht sauber abgeschlossen wurden (Pending/Applying/Undoing = Abbruch oder
    /// Absturz mitten im Vorgang) oder bei denen keine Datei gelang (Failed). Nur Anzeige — der nächste Scan
    /// bzw. Undo heilt den Katalog.
    /// </summary>
    private async Task RefreshRecoveryAsync()
    {
        if (_recoveryRefreshRunning)
        {
            return;
        }

        _recoveryRefreshRunning = true;
        try
        {
            var batches = await _fileOperations.GetRecentBatchesAsync(RecoveryBatchWindow).ConfigureAwait(true);
            var incomplete = batches.Where(b => b.Status is BatchStatus.Pending or BatchStatus.Applying or BatchStatus.Undoing).ToList();
            var failed = batches.Where(b => b.Status == BatchStatus.Failed).ToList();
            if (incomplete.Count == 0 && failed.Count == 0)
            {
                RecoveryText = null;
                RecoveryDetails = null;
                return;
            }

            var parts = new List<string>(2);
            if (incomplete.Count > 0)
            {
                parts.Add(incomplete.Count == 1 ? "1 Dateivorgang unvollständig" : $"{incomplete.Count} Dateivorgänge unvollständig");
            }

            if (failed.Count > 0)
            {
                parts.Add(failed.Count == 1 ? "1 Dateivorgang fehlgeschlagen" : $"{failed.Count} Dateivorgänge fehlgeschlagen");
            }

            var text = string.Join(" · ", parts);
            var details = string.Join(
                Environment.NewLine,
                incomplete.Concat(failed)
                    .Take(RecoveryDetailLines)
                    .Select(b => $"#{b.Id} {b.Type} {b.Status} · {b.ItemCount} Datei(en) · {b.CreatedAtUtc.ToLocalTime():g}: {b.Description}"));

            if (text != RecoveryText)
            {
                _logger.LogWarning("Statusleiste: {Text}", text);
            }

            RecoveryText = text;
            RecoveryDetails = details;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Statusleiste: Status der Dateivorgänge konnte nicht ermittelt werden");
        }
        finally
        {
            _recoveryRefreshRunning = false;
        }
    }

    private void OnScanProgress(ScanProgressMessage message)
    {
        var p = message.Progress;
        IsScanning = true;
        ScanResultText = null;
        _resultTimer.Stop();
        ScanText = $"Scan: {p.FilesDiscovered:N0} Dateien · {p.FoldersDiscovered:N0} Ordner · {Formatting.Bytes(p.TotalBytesDiscovered)} — {Formatting.ScanPhase(p.Phase)}";
        UpdateActivity();
    }

    private void OnScanCompleted(ScanCompletedMessage message)
    {
        var r = message.Result;
        IsScanning = false;
        ScanText = null;
        ScanResultIsError = r.Outcome == ScanOutcome.MassMissingBlocked;
        ScanResultText = ScanResultSummary(r);
        _resultTimer.Start();
        UpdateActivity();
        _ = RefreshLibraryAsync();
    }

    /// <summary>
    /// Kurzfassung des Scan-Ergebnisses. Beim Mass-Missing-Guard (PLAN Kap. 12) wurde nichts markiert — die
    /// Bestätigung holt die Bibliotheksseite ein; der Incremental-Scan zeigt seine Diff-Zähler statt „N Dateien“.
    /// </summary>
    private static string ScanResultSummary(ScanResult r)
    {
        if (r.Outcome == ScanOutcome.MassMissingBlocked)
        {
            return $"Scan: {r.MissingDetected:N0} fehlende Dateien, Bestätigung nötig";
        }

        if (r.Mode != ScanMode.Incremental)
        {
            return $"Scan abgeschlossen: {r.FilesInserted:N0} Dateien in {Formatting.Duration(r.TotalDuration)}";
        }

        return r.HasChanges
            ? $"Aktualisiert: {r.Added:N0} neu · {r.Modified:N0} geändert · {r.MarkedMissing:N0} fehlend · {r.Reappeared:N0} wieder da ({Formatting.Duration(r.TotalDuration)})"
            : $"Aktualisiert: keine Änderungen ({Formatting.Duration(r.TotalDuration)})";
    }

    private void OnScanAborted(ScanAbortedMessage message)
    {
        IsScanning = false;
        ScanText = null;
        ScanResultIsError = !message.Canceled;
        ScanResultText = message switch
        {
            { Canceled: true } => "Scan abgebrochen",
            { LibraryOffline: true } => "Bibliothek offline — Scan beendet, nichts als fehlend markiert",
            _ => $"Scan fehlgeschlagen: {message.Reason}",
        };
        _resultTimer.Start();
        UpdateActivity();
        if (message.LibraryOffline)
        {
            _ = RefreshLibraryAsync(); // Offline-Hinweis (HintText) sofort nachziehen
        }
    }

    /// <summary>Nach „Abbrechen“ kurz bestätigen — die Galerie verwirft den Abbruch still (der wartende Batch hat noch nichts geändert).</summary>
    private void OnFileOperationWaitEnded(ScanGateWaitEndedMessage message)
    {
        if (!message.CanceledByUser)
        {
            return;
        }

        _logger.LogInformation("Statusleiste: wartende Datei-Operation abgebrochen (Bibliothek {LibraryId})", message.Wait.LibraryId);
        ScanResultIsError = false;
        ScanResultText = "Wartende Datei-Operation abgebrochen";
        _resultTimer.Stop();
        _resultTimer.Start();
    }

    private void OnResultTick(object? sender, EventArgs e)
    {
        _resultTimer.Stop();
        ScanResultText = null;
    }

    private void OnThumbnailTick(object? sender, EventArgs e)
    {
        var pending = _thumbnails.PendingCount;
        if (pending == ThumbnailPending)
        {
            return;
        }

        ThumbnailPending = pending;
        HasThumbnailWork = pending > 0;
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var parts = new List<string>(2);
        if (IsScanning)
        {
            parts.Add("Scan");
        }

        if (HasThumbnailWork)
        {
            parts.Add("Vorschaubilder");
        }

        if (ArchiveIsRunning)
        {
            parts.Add("Entpacken");
        }

        if (FaceIndexIsActive && !FaceIndexIsPaused)
        {
            parts.Add("Gesichter");
        }

        if (MotifIndexIsActive && !MotifIndexIsPaused)
        {
            parts.Add("Motive"); // ADR-0052
        }

        IsBackgroundActive = parts.Count > 0;
        BackgroundActivityText = IsBackgroundActive ? "Hintergrund: " + string.Join(" · ", parts) : null;
    }

    // ---- Ordnerüberwachung (Services/Watch): Hinweis „Änderungen erkannt — Aktualisierung in n s“ ------------------

    private static readonly TimeSpan WatchTickInterval = TimeSpan.FromSeconds(1);
    private readonly DispatcherTimer _watchTimer;
    private WatchStatus _watchStatus = WatchStatus.Off;
    private DateTime? _watchClearAtUtc;
    private WatchStatus? _queuedWatchStatus;
    private int _watchPostQueued;

    /// <summary>Hinweis der Ordnerüberwachung (Countdown, laufender/abgeschlossener automatischer Scan, Sperre); null = nichts.</summary>
    [ObservableProperty] private string? _watchText;

    /// <summary>true bei angehaltener Automatik (Warnsymbol statt Sync-Symbol).</summary>
    [ObservableProperty] private bool _watchIsWarning;

    /// <summary>
    /// Bündelt Statusnachrichten der Ordnerüberwachung (kommen von Watcher-/Timer-/Scan-Threads, beim Kopieren vieler
    /// Dateien auch dicht hintereinander): nur der jüngste Zustand zählt, höchstens ein Dispatcher-Auftrag steht aus,
    /// mit Background-Priorität (CLAUDE.md: viele Hintergrund-Meldungen gebündelt an den UI-Thread).
    /// </summary>
    private void QueueWatchStatus(WatchStatus status)
    {
        Interlocked.Exchange(ref _queuedWatchStatus, status);
        if (Interlocked.Exchange(ref _watchPostQueued, 1) != 0)
        {
            return; // ein Auftrag steht schon aus und nimmt den jüngsten Zustand mit
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyQueuedWatchStatus();
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyQueuedWatchStatus, DispatcherPriority.Background);
    }

    private void ApplyQueuedWatchStatus()
    {
        Volatile.Write(ref _watchPostQueued, 0);
        if (Interlocked.Exchange(ref _queuedWatchStatus, null) is { } status)
        {
            OnWatchStatus(status);
        }
    }

    private void OnWatchStatus(WatchStatus status)
    {
        _watchStatus = status;
        _watchClearAtUtc = null;
        _watchTimer.Stop();
        WatchIsWarning = status.State == WatchState.Blocked;
        switch (status.State)
        {
            case WatchState.ChangesPending:
                UpdateWatchCountdown();
                _watchTimer.Start();
                break;
            case WatchState.Scanning:
                WatchText = "Automatische Aktualisierung läuft…";
                break;
            case WatchState.Blocked:
                WatchText = $"Automatische Aktualisierung angehalten: {status.Reason}";
                break;
            case WatchState.Idle when status.LastAutoScan is { } r:
                WatchText = r.HasChanges
                    ? $"Automatisch aktualisiert: {r.Added:N0} neu · {r.Modified:N0} geändert · {r.MarkedMissing:N0} fehlend · {r.Reappeared:N0} wieder da"
                    : "Automatisch aktualisiert: keine Änderungen";
                _watchClearAtUtc = DateTime.UtcNow + ResultDisplayDuration;
                _watchTimer.Start();
                break;
            default:
                WatchText = null;
                break;
        }
    }

    private void OnWatchTick(object? sender, EventArgs e)
    {
        if (_watchStatus.State == WatchState.ChangesPending)
        {
            UpdateWatchCountdown();
            return;
        }

        if (_watchClearAtUtc is { } clearAt && DateTime.UtcNow >= clearAt)
        {
            _watchTimer.Stop();
            _watchClearAtUtc = null;
            WatchText = null;
        }
    }

    private void UpdateWatchCountdown()
    {
        var remaining = _watchStatus.ScanDueAtUtc is { } due ? Math.Max(0, (int)Math.Ceiling((due - DateTime.UtcNow).TotalSeconds)) : 0;
        WatchText = $"Änderungen erkannt — Aktualisierung in {remaining} s";
    }

    // ---- Automatisches Entpacken (Services/Archive, ADR-0032) ------------------------------------------------------

    private static readonly TimeSpan ArchiveWarningDisplayDuration = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _archiveTimer;

    /// <summary>Fortschritt bzw. Ergebnis des automatischen Entpackens; null = nichts.</summary>
    [ObservableProperty] private string? _archiveText;

    /// <summary>Tooltip: die gemeldeten Archive mit Grund.</summary>
    [ObservableProperty] private string? _archiveDetails;

    [ObservableProperty] private bool _archiveIsWarning;
    [ObservableProperty] private bool _archiveIsRunning;

    private void OnAutoExtractStatus(ArchiveAutoExtractStatus status)
    {
        var (text, warning, details) = DescribeAutoExtract(status);
        ArchiveIsRunning = status.State != ArchiveAutoExtractState.Finished;
        ArchiveText = text;
        ArchiveIsWarning = warning;
        ArchiveDetails = details;
        _archiveTimer.Stop();
        if (!ArchiveIsRunning)
        {
            // Erfolg so lange wie ein Scan-Ergebnis, Meldungen länger (gemeldete Archive sollen auffallen)
            _archiveTimer.Interval = warning ? ArchiveWarningDisplayDuration : ResultDisplayDuration;
            _archiveTimer.Start();
            if (warning)
            {
                _logger.LogWarning("Statusleiste: {Text}{NewLine}{Details}", text, Environment.NewLine, details);
            }
        }

        UpdateActivity();
    }

    private void ClearArchiveText()
    {
        _archiveTimer.Stop();
        ArchiveText = null;
        ArchiveDetails = null;
        ArchiveIsWarning = false;
    }

    /// <summary>Text, Warnstufe und Tooltip für einen Zustand des automatischen Entpackens.</summary>
    internal static (string Text, bool IsWarning, string? Details) DescribeAutoExtract(ArchiveAutoExtractStatus status)
    {
        switch (status.State)
        {
            case ArchiveAutoExtractState.Running:
                var prefix = status.Total > 1 ? $"Archiv {status.Index}/{status.Total}" : "Archiv";
                return ($"{prefix} wird automatisch entpackt: {status.CurrentArchive}", false, null);
            case ArchiveAutoExtractState.Scanning:
                return ("Entpackte Archive werden in den Katalog aufgenommen…", false, null);
        }

        var summary = status.Summary;
        if (summary is null)
        {
            return ("Automatisches Entpacken beendet", false, null);
        }

        var problems = summary.Problems;
        var lines = problems.Select(p => $"{p.Name}: {p.Message ?? DescribeState(p.State)}").ToList();
        if (summary.ScanError is not null)
        {
            lines.Add($"Katalog nicht aktualisiert: {summary.ScanError}");
        }

        if (summary.Canceled)
        {
            lines.Add("Automatisches Entpacken abgebrochen.");
        }

        var details = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
        var notExtracted = summary.Items.Count - summary.ExtractedCount;
        string text;
        if (summary.ExtractedCount > 0)
        {
            text = $"Automatisch entpackt: {Plural(summary.ExtractedCount, "Archiv", "Archive")} · {summary.FilesExtracted:N0} Dateien";
            if (summary.RecycledCount > 0)
            {
                text += $" · {summary.RecycledCount:N0} im Papierkorb";
            }

            if (notExtracted > 0)
            {
                text += $" · {notExtracted:N0} nicht entpackt";
            }
            else if (problems.Count > 0)
            {
                text += " · mit Hinweisen";
            }
        }
        else if (problems.Count == 1)
        {
            var p = problems[0];
            text = $"Archiv nicht automatisch entpackt: {p.Name} — {p.Message ?? DescribeState(p.State)}";
        }
        else if (problems.Count > 1)
        {
            text = $"{problems.Count:N0} Archive nicht automatisch entpackt";
        }
        else
        {
            text = summary.Canceled ? "Automatisches Entpacken abgebrochen" : "Automatisches Entpacken beendet";
        }

        return (text, summary.HasProblems, details);
    }

    private static string DescribeState(ArchiveAutoExtractItemState state) => state switch
    {
        ArchiveAutoExtractItemState.ExtractedWithErrors => "mit Fehlern entpackt",
        ArchiveAutoExtractItemState.Failed => "nicht entpackt",
        ArchiveAutoExtractItemState.Skipped => "übersprungen",
        _ => "entpackt",
    };

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count:N0} {many}";

    public void Dispose()
    {
        _archiveTimer.Stop();
        _thumbnailTimer.Stop();
        _resultTimer.Stop();
        _recoveryTimer.Stop();
        _watchTimer.Stop();
        FileOperationWait.Ended -= OnFileOperationWaitEnded;
        FileOperationWait.Dispose();
        _messenger.UnregisterAll(this);
    }
}
