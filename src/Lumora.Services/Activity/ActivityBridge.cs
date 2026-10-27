using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Archive;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Watch;

namespace Lumora.Services.Activity;

/// <summary>
/// Führt die vorhandenen Fortschrittsquellen in die <see cref="IActivityCenter"/> (ADR-0067): die Messenger-Records aus
/// <c>Services/Messaging</c> und den Modulen (Einlesen, Entpacken, Indizes, Ordnerüberwachung, Wartezustände der
/// Scan-Sperre und der Katalog-Schreibsperre) sowie die Warteschlange der Vorschaubilder (abgefragt alle
/// <see cref="ThumbnailPollInterval"/>). Die Quellen bleiben unverändert; Abbrechen/Pause nutzen deren Wege
/// (<c>CancelRun</c>, <c>Pause</c>, <c>CancelWait</c>, <c>CancelPass</c>, <c>Cancel</c>). Nachrichten kommen von beliebigen
/// Threads — die Zentrale ist thread-sicher und drosselt die Oberfläche. Eine Instanz je Sitzung (DI-Container).
/// </summary>
public sealed partial class ActivityBridge : IDisposable
{
    public static readonly TimeSpan ThumbnailPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IActivityCenter _center;
    private readonly IMessenger _messenger;
    private readonly IScanGate? _scanGate;
    private readonly ILibraryService? _libraries;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, IActivityHandle> _borrowedWaits = new();
    private int _started;

    public ActivityBridge(
        IActivityCenter center,
        IMessenger? messenger = null,
        IScanGate? scanGate = null,
        ILibraryService? libraries = null,
        Thumbnails.IThumbnailService? thumbnails = null,
        Faces.Indexing.IFaceIndexService? faces = null,
        Motifs.IMotifIndexService? motifs = null,
        Quality.IFileQualityIndexService? quality = null,
        Places.Tracks.IGpsTrackImportService? gpsTracks = null,
        TimeProvider? time = null,
        ILogger<ActivityBridge>? logger = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _planner = planner;
        _center = center;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _scanGate = scanGate;
        _libraries = libraries;
        _thumbnails = thumbnails;
        _faces = faces;
        _motifs = motifs;
        _quality = quality;
        _gpsTracks = gpsTracks;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Nachrichten abonnieren (einmal je Sitzung, vor den Hintergrund-Diensten).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<ActivityBridge, ScanProgressMessage>(this, static (r, m) => r.OnScanProgress(m));
        _messenger.Register<ActivityBridge, ScanCompletedMessage>(this, static (r, m) => r.OnScanCompleted(m));
        _messenger.Register<ActivityBridge, ScanAbortedMessage>(this, static (r, m) => r.OnScanAborted(m));
        _messenger.Register<ActivityBridge, ScanGateWaitStartedMessage>(this, static (r, m) => r.OnGateWaitStarted(m.Wait));
        _messenger.Register<ActivityBridge, ScanGateWaitEndedMessage>(this, static (r, m) => r.OnGateWaitEnded(m));
        _messenger.Register<ActivityBridge, CatalogWriteWaitMessage>(this, static (r, m) => r.OnCatalogWriteWait(m.WaitingWriters));
        _messenger.Register<ActivityBridge, WatchStatusChangedMessage>(this, static (r, m) => r.OnWatchStatus(m.Status));
        _messenger.Register<ActivityBridge, ArchiveAutoExtractStatusMessage>(this, static (r, m) => r.OnAutoExtract(m.Status));
        StartIndexes();
        _logger.LogDebug("Aktivitäten: Quellen angeschlossen");
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        StopIndexes();
    }

    // ------------------------------------------------------------------ Einlesen

    private static string ScanKey(int libraryId) => $"scan:{libraryId}";

    private IActivityHandle EnsureScan(int libraryId)
    {
        var key = ScanKey(libraryId);
        if (_center.Find(key) is { } existing)
        {
            return existing;
        }

        var handle = _center.Start(new ActivityStartInfo(ActivityKind.Scan, "Einlesen", Key: key));
        _ = NameScanAsync(handle, libraryId);
        return handle;
    }

    /// <summary>Titel „Einlesen „Name““, sobald der Bibliotheksname gelesen ist (nur der Name, kein Pfad).</summary>
    private async Task NameScanAsync(IActivityHandle handle, int libraryId)
    {
        if (_libraries is null)
        {
            return;
        }

        try
        {
            var library = await _libraries.GetAsync(libraryId).ConfigureAwait(false);
            if (library is not null && handle.IsActive)
            {
                handle.SetTitle($"Einlesen „{library.Name}“");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Aktivitäten: Bibliotheksname für das Einlesen nicht lesbar");
        }
    }

    private void OnScanProgress(ScanProgressMessage message)
    {
        var p = message.Progress;
        var phase = p.Phase switch
        {
            ScanPhase.Clearing => "alte Einträge entfernen",
            ScanPhase.Persisting => "in den Katalog schreiben",
            ScanPhase.Indexing => "Suchindex",
            _ => "Ordner durchsuchen",
        };
        EnsureScan(message.LibraryId).Report(0, 0, $"{p.FilesDiscovered:N0} Dateien · {p.FoldersDiscovered:N0} Ordner — {phase}");
    }

    private void OnScanCompleted(ScanCompletedMessage message)
    {
        var r = message.Result;
        var handle = EnsureScan(message.LibraryId);
        handle.Complete(DescribeScan(r));
    }

    /// <summary>Ergebnis des Einlesens; „Fehlende zeigen“ bei neu als fehlend markierten Dateien.</summary>
    internal static ActivityCompletion DescribeScan(ScanResult r)
    {
        if (r.Outcome == ScanOutcome.MassMissingBlocked)
        {
            return new ActivityCompletion($"{r.MissingDetected:N0} Dateien fehlen — Bestätigung nötig", ActivityState.Warning, [ActivityAction.Details],
                "Ungewöhnlich viele Dateien fehlen. Es wurde nichts als fehlend markiert — die Bibliotheksseite fragt nach (Erneut prüfen / Als fehlend markieren).");
        }

        if (r.Outcome == ScanOutcome.LibraryOffline)
        {
            return new ActivityCompletion("Bibliothek offline — nichts als fehlend markiert", ActivityState.Warning);
        }

        if (r.Mode != ScanMode.Incremental)
        {
            return new ActivityCompletion($"{r.FilesInserted:N0} Dateien eingelesen ({Duration(r.TotalDuration)})");
        }

        if (!r.HasChanges)
        {
            return new ActivityCompletion($"Keine Änderungen ({Duration(r.TotalDuration)})");
        }

        var parts = new List<string> { $"{r.Added:N0} neu", $"{r.Modified:N0} geändert" };
        if (r.MarkedMissing > 0)
        {
            parts.Add($"{r.MarkedMissing:N0} fehlen");
        }

        if (r.Reappeared > 0)
        {
            parts.Add($"{r.Reappeared:N0} wieder da");
        }

        return new ActivityCompletion(string.Join(", ", parts), ActivityState.Succeeded, r.MarkedMissing > 0 ? [ActivityAction.ShowMissing] : null);
    }

    private void OnScanAborted(ScanAbortedMessage message)
    {
        var handle = EnsureScan(message.LibraryId);
        if (message.Canceled)
        {
            handle.Canceled("Einlesen abgebrochen");
        }
        else if (message.LibraryOffline)
        {
            handle.Complete(new ActivityCompletion("Bibliothek offline — Einlesen beendet, nichts als fehlend markiert", ActivityState.Warning));
        }
        else
        {
            handle.Fail("Einlesen fehlgeschlagen", message.Reason);
        }
    }

    // ------------------------------------------------------------------ Wartezustände

    private static string GateReason(ScanGateWait wait)
        => wait.Holder == ScanGateUse.Scan ? "wartet auf laufendes Einlesen derselben Bibliothek" : "wartet auf laufenden Datei-Vorgang derselben Bibliothek";

    /// <summary>
    /// Ein Scan oder Datei-Batch wartet auf die Scan-Sperre. Die Nachricht kommt im Ablauf des Wartenden: hat der einen
    /// Vorgang (<see cref="ActivityScope.Current"/>, z. B. „Verschieben“), wartet dieser Eintrag — sonst ein eigener.
    /// „Abbrechen“ geht über <see cref="IScanGate.CancelWait"/> (der Aufrufer bekommt seine gewohnte Ausnahme).
    /// </summary>
    private void OnGateWaitStarted(ScanGateWait wait)
    {
        Action? cancel = _scanGate is { } gate ? () => gate.CancelWait(wait.WaitId) : null;
        if (wait.Waiter == ScanGateUse.FileOperation && ActivityScope.Current is { IsActive: true } owner)
        {
            _borrowedWaits[wait.WaitId] = owner;
            owner.Wait(GateReason(wait), cancel);
            return;
        }

        var handle = _center.Start(new ActivityStartInfo(
            wait.Waiter == ScanGateUse.Scan ? ActivityKind.Scan : ActivityKind.FileOperation,
            wait.Waiter == ScanGateUse.Scan ? "Einlesen" : "Datei-Vorgang",
            KeepInRecent: false,
            Key: $"gate:{wait.WaitId}",
            Cancel: cancel));
        handle.Wait(GateReason(wait), cancel);
    }

    private void OnGateWaitEnded(ScanGateWaitEndedMessage message)
    {
        if (_borrowedWaits.TryRemove(message.Wait.WaitId, out var owner))
        {
            owner.Resume(); // abgebrochen: der Vorgang selbst meldet „Abgebrochen“
            return;
        }

        if (_center.Find($"gate:{message.Wait.WaitId}") is not { } handle)
        {
            return;
        }

        if (message.CanceledByUser)
        {
            handle.Canceled(message.Wait.Waiter == ScanGateUse.FileOperation ? "Wartende Datei-Operation abgebrochen" : "Wartendes Einlesen abgebrochen");
        }
        else
        {
            handle.Dismiss();
        }
    }

    private void OnCatalogWriteWait(int waitingWriters)
    {
        const string key = "catalog-write";
        if (waitingWriters <= 0)
        {
            _center.Find(key)?.Dismiss();
            return;
        }

        var handle = _center.Start(new ActivityStartInfo(ActivityKind.CatalogWrite, "Änderung speichern", KeepInRecent: false, Key: key));
        handle.Wait(waitingWriters == 1
            ? "wartet auf den Katalog (z. B. „Neu aufbauen“ schreibt gerade) — bitte Lumora nicht schließen"
            : $"{waitingWriters} Änderungen warten auf den Katalog — bitte Lumora nicht schließen");
    }

    /// <summary>Ordnerüberwachung: anstehende bzw. angehaltene Automatik als „wartet“; das Einlesen selbst kommt über die Scan-Nachrichten.</summary>
    private void OnWatchStatus(WatchStatus status)
    {
        const string key = "watch";
        switch (status.State)
        {
            case WatchState.ChangesPending:
                // Uhrzeit statt Countdown: der Grund ist eine Momentaufnahme und tickt nicht weiter (jede neue Änderung meldet neu)
                _center.Start(new ActivityStartInfo(ActivityKind.Watch, "Automatische Aktualisierung", KeepInRecent: false, Key: key))
                    .Wait(WatchReason(status.ScanDueAtUtc));
                break;
            case WatchState.Blocked:
                _center.Start(new ActivityStartInfo(ActivityKind.Watch, "Automatische Aktualisierung", KeepInRecent: false, Key: key))
                    .Wait($"angehalten: {status.Reason}");
                break;
            default:
                _center.Find(key)?.Dismiss();
                break;
        }
    }

    /// <summary>„Änderungen erkannt — Aktualisierung gegen 14:03:05“ (Ortszeit) bzw. ohne Zeitpunkt „… — Aktualisierung folgt“.</summary>
    internal static string WatchReason(DateTime? dueUtc)
        => dueUtc is { } due
            ? $"Änderungen erkannt — Aktualisierung gegen {DateTime.SpecifyKind(due, DateTimeKind.Utc).ToLocalTime():HH:mm:ss}"
            : "Änderungen erkannt — Aktualisierung folgt";

    // ------------------------------------------------------------------ Entpacken

    private void OnAutoExtract(ArchiveAutoExtractStatus status)
    {
        var key = $"archive:{status.LibraryId}";
        var handle = _center.Start(new ActivityStartInfo(ActivityKind.Archive, "Archive entpacken", Key: key));
        switch (status.State)
        {
            case ArchiveAutoExtractState.Running:
                handle.Report(Math.Max(0, status.Index - 1), status.Total, status.CurrentArchive is { } name ? $"„{name}“ · automatisch" : "automatisch");
                break;
            case ArchiveAutoExtractState.Scanning:
                handle.Report(status.Total, status.Total, "Entpacktes kommt in den Katalog …");
                break;
            default:
                handle.Complete(DescribeAutoExtract(status.Summary));
                break;
        }
    }

    internal static ActivityCompletion DescribeAutoExtract(ArchiveAutoExtractSummary? summary)
    {
        if (summary is null)
        {
            return new ActivityCompletion("Automatisches Entpacken beendet");
        }

        var lines = summary.Problems.Select(p => $"{p.Name}: {p.Message ?? p.State.ToString()}").ToList();
        if (summary.ScanError is not null)
        {
            lines.Add($"Katalog nicht aktualisiert: {summary.ScanError}");
        }

        var details = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
        var text = summary.ExtractedCount > 0
            ? $"{(summary.ExtractedCount == 1 ? "1 Archiv" : $"{summary.ExtractedCount:N0} Archive")} entpackt · {summary.FilesExtracted:N0} Dateien"
            : summary.Canceled ? "Automatisches Entpacken abgebrochen" : "Nichts entpackt";
        var notExtracted = summary.Items.Count - summary.ExtractedCount;
        if (summary.ExtractedCount > 0 && notExtracted > 0)
        {
            text += $" · {notExtracted:N0} nicht";
        }

        var state = summary.Canceled ? ActivityState.Canceled : summary.HasProblems ? ActivityState.Warning : ActivityState.Succeeded;
        return new ActivityCompletion(text, state, details is null ? null : [ActivityAction.Details], details);
    }

    internal static string Duration(TimeSpan duration)
        => duration.TotalSeconds < 1 ? "unter 1 s"
            : duration.TotalMinutes < 1 ? $"{duration.TotalSeconds:N0} s"
            : $"{(int)duration.TotalMinutes} min {duration.Seconds} s";
}
