using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Media;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;

namespace Lumora.Services.Watch;

/// <summary>
/// Ordnerüberwachung der aktiven Bibliothek (ADR-0019 nennt den FileSystemWatcher als Auslöser für „Aktualisieren“):
/// <list type="bullet">
/// <item>Ein <see cref="FileSystemWatcher"/> je Wurzel (rekursiv, 64 KB Puffer). Nicht erreichbare Wurzeln werden alle
/// <see cref="RootRetryInterval"/> erneut versucht; nach einem Watcher-Fehler (Pufferüberlauf) wird der Watcher neu
/// gestartet und ein Scan eingeplant, weil Ereignisse verloren sein können.</item>
/// <item>Relevant sind nur Medienformate und Ordner (<see cref="WatchPathFilter"/>). Ereignisse werden gezählt, nicht
/// gespeichert; jedes Ereignis startet die Ruhezeit neu. Erst <see cref="QuietPeriod"/> ohne weiteres Ereignis löst einen
/// Incremental-Scan über <see cref="IScanService"/> aus.</item>
/// <item>Nie parallel zu einem laufenden Scan derselben Bibliothek: <see cref="IScanGate"/> serialisiert alle Scans
/// (der spätere wartet). Belegt ein fremder Scan (Bibliotheksseite, Entpacken) die Bibliothek, sind alle bis dahin
/// gesammelten Ereignisse durch ihn abgedeckt und werden verworfen (zusammengelegt); endet er nicht erfolgreich, kommen
/// sie zurück. Ereignisse während eines Scans werden nach dessen Ende erneut geprüft (neue Ruhezeit). Zusätzlich
/// erkennt der Dienst fremde Scans an <see cref="ScanProgressMessage"/>/<see cref="ScanCompletedMessage"/>/
/// <see cref="ScanAbortedMessage"/> der Bibliothek.</item>
/// <item>Eigene Schreibvorgänge der App (Datei-Batches des FileOperationService, Ordner-Aufräumen des Sortierers) stehen
/// kurzzeitig in der <see cref="IOwnChangeRegistry"/>; ihre Ereignisse zählen nicht, denn der Katalog ist dafür schon
/// gebucht (kein leerer Scan nach Verschieben, Papierkorb oder „Ins Archiv…“). Ein Renamed zählt nur dann nicht, wenn
/// alter und neuer Pfad eigen sind. Nimmt die Operation einen Pfad zurück (Katalog nicht gebucht), zählt er nachträglich
/// als Ereignis.</item>
/// <item>Endet der automatische Scan mit <see cref="ScanOutcome.MassMissingBlocked"/> oder <see cref="ScanOutcome.LibraryOffline"/>,
/// wird nicht automatisch wiederholt (<see cref="WatchState.Blocked"/>, Hinweis in der Statusleiste); ein erfolgreicher
/// manueller Scan, ein Bibliothekswechsel oder das Umschalten der Einstellung hebt die Sperre auf.</item>
/// <item>Wechsel der aktiven Bibliothek per <see cref="ActiveLibraryChangedMessage"/>; Start beim App-Start, Stop beim
/// Beenden (Dispose über den DI-Container). Jeder Wechsel erhöht eine Epoche: das Ergebnis eines automatischen Scans
/// der vorigen Bibliothek (Sperre, „Automatisch aktualisiert“) wird verworfen statt der neuen zugeordnet.</item>
/// </list>
/// Alle Zustandsänderungen gehen als <see cref="WatchStatusChangedMessage"/> an den Messenger (vom Watcher-, Timer- oder
/// Scan-Thread). Der Zustand wird unter einem Lock geführt; gesendet wird außerhalb des Locks, immer der jüngste Zustand
/// und nie ein älterer nach einem jüngeren. Reine Countdown-Verschiebungen (jedes Ereignis setzt die Ruhezeit neu)
/// gehen höchstens alle <see cref="CountdownPublishInterval"/> hinaus, die letzte nachlaufend — beim Kopieren Tausender
/// Dateien flutet das den UI-Thread nicht.
/// </summary>
public sealed class LibraryWatchService : ILibraryWatchService
{
    /// <summary>Abstand, in dem eine nicht erreichbare Wurzel erneut versucht wird.</summary>
    public static readonly TimeSpan RootRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>Mindestabstand zweier Statusnachrichten, die nur den Countdown verschieben.</summary>
    public static readonly TimeSpan CountdownPublishInterval = TimeSpan.FromMilliseconds(500);

    private const int WatcherBufferSize = 64 * 1024;

    private readonly ILibraryService _libraries;
    private readonly IScanService _scans;
    private readonly IWatchSettingsStore _settingsStore;
    private readonly WatchPathFilter _filter;
    private readonly ILogger<LibraryWatchService> _logger;
    private readonly IMessenger _messenger;
    private readonly IScanGate _scanGate;
    private readonly IOwnChangeRegistry _ownChanges;
    private readonly TimeSpan _rootRetryInterval;

    /// <summary>Ausschlüsse beim Einlesen (ADR-0066): Ereignisse in ausgeschlossenen Ordnern zählen nicht. Null = keine.</summary>
    private readonly Scanning.Exclusions.IScanExclusionStore? _exclusionStore;
    private volatile Scanning.Exclusions.ScanExclusionSet _exclusions = Scanning.Exclusions.ScanExclusionSet.None;

    /// <summary>Kennung der automatischen Scans für die Scan-Sperre (0.2.29): ihr Warten ist nie das der Bibliotheksseite.</summary>
    private readonly ScanOptions _autoScanOptions = new() { GateCallerId = Guid.NewGuid() };

    private readonly object _gate = new();
    private readonly object _sendGate = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _quietTimer;
    private readonly Timer _retryTimer;
    private readonly Timer _publishTimer;

    private bool _started;
    private bool _disposed;
    private bool _enabled = WatchSettings.Default.Enabled;
    private TimeSpan _quietPeriod = WatchSettings.Default.QuietPeriod;
    private int? _libraryId;
    private string[] _roots = [];
    private string[] _unavailableRoots = [];
    private int _pendingEvents;
    private int _coveredEvents;
    private bool _ownScanRunning;
    private long _ownScanEpoch;
    private bool _externalScanRunning;
    private bool _blocked;
    private string? _blockedReason;
    private DateTime? _dueAtUtc;
    private CancellationTokenSource? _scanCts;
    private WatchStatus _status = WatchStatus.Off;
    private int _attachVersion;
    private long _libraryEpoch;
    private long _statusVersion;
    private long _sentVersion;
    private long _lastCountdownPublishTicks = long.MinValue / 2;
    private bool _publishScheduled;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="rootRetryInterval">Optional (Tests); produktiv <see cref="RootRetryInterval"/>.</param>
    /// <param name="scanGate">Optional (Tests); produktiv der Singleton, den auch der ScanService belegt.</param>
    /// <param name="ownChanges">Optional (Tests); produktiv der Singleton, in den der FileOperationService schreibt.</param>
    /// <param name="exclusions">Optional (Tests); produktiv die Ausschlüsse des Katalogs (ADR-0066).</param>
    public LibraryWatchService(
        ILibraryService libraries,
        IScanService scans,
        IWatchSettingsStore settingsStore,
        IMediaFormatRegistry formats,
        IAppDataPaths paths,
        ILogger<LibraryWatchService> logger,
        IMessenger? messenger = null,
        TimeSpan? rootRetryInterval = null,
        IScanGate? scanGate = null,
        IOwnChangeRegistry? ownChanges = null,
        Scanning.Exclusions.IScanExclusionStore? exclusions = null)
    {
        _exclusionStore = exclusions;
        _libraries = libraries;
        _scans = scans;
        _settingsStore = settingsStore;
        _filter = new WatchPathFilter(formats, paths.ThumbnailsRoot);
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _scanGate = scanGate ?? new ScanGate();
        _ownChanges = ownChanges ?? new OwnChangeRegistry();
        _rootRetryInterval = rootRetryInterval ?? RootRetryInterval;
        _quietTimer = new Timer(_ => OnQuietElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _retryTimer = new Timer(_ => OnRetryTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _publishTimer = new Timer(_ => OnPublishTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public TimeSpan QuietPeriod
    {
        get
        {
            lock (_gate)
            {
                return _quietPeriod;
            }
        }
    }

    public WatchStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Zu überwachende Wurzeln einer Bibliothek (ADR-0020): Archiv-Wurzel und alle Eingangs-Wurzeln. Ohne Wurzelliste
    /// (Alt-Katalog vor der Migration) dient <see cref="LibraryInfo.RootPath"/> als einzige Wurzel.
    /// </summary>
    public static IReadOnlyList<string> GetRootPaths(LibraryInfo library)
    {
        var roots = library.Roots
            .Select(r => r.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0 && !string.IsNullOrWhiteSpace(library.RootPath))
        {
            roots.Add(library.RootPath);
        }

        return roots;
    }

    // ---- Lebenszyklus ------------------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        var settings = WatchSettings.Default;
        try
        {
            settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordnerüberwachung: Einstellungen konnten nicht gelesen werden — Standard (an, {Quiet} s)", WatchSettings.DefaultQuietSeconds);
        }

        lock (_gate)
        {
            _enabled = settings.Enabled;
            _quietPeriod = WatchSettings.ClampQuietPeriod(settings.QuietPeriod);
        }

        _messenger.Register<LibraryWatchService, ActiveLibraryChangedMessage>(this, static (r, m) => r.OnActiveLibraryChanged(m));
        _messenger.Register<LibraryWatchService, LibraryRootsChangedMessage>(this, static (r, m) => r.OnLibraryRootsChanged(m));
        _messenger.Register<LibraryWatchService, ScanProgressMessage>(this, static (r, m) => r.OnExternalScanProgress(m));
        _messenger.Register<LibraryWatchService, ScanCompletedMessage>(this, static (r, m) => r.OnExternalScanFinished(m.LibraryId, m.Result.Outcome == ScanOutcome.Completed));
        _messenger.Register<LibraryWatchService, ScanAbortedMessage>(this, static (r, m) => r.OnExternalScanFinished(m.LibraryId, completedOk: false));
        _messenger.Register<LibraryWatchService, ScanExclusionsChangedMessage>(this, static (r, m) => _ = r.ReloadExclusionsAsync()); // ADR-0066
        _scanGate.Acquired += OnScanGateAcquired;
        _ownChanges.Revoked += OnOwnChangeRevoked;

        _logger.LogInformation("Ordnerüberwachung: {State}, Ruhezeit {Quiet} s", settings.Enabled ? "an" : "aus", (int)_quietPeriod.TotalSeconds);
        await AttachToActiveLibraryAsync(ct).ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return Task.CompletedTask;
            }

            _started = false;
            _libraryEpoch++;
            CancelOwnScanLocked();
            DisposeWatchersLocked();
            _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _publishTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _publishScheduled = false;
            _libraryId = null;
            _roots = [];
            _unavailableRoots = [];
            _pendingEvents = 0;
            _coveredEvents = 0;
            _dueAtUtc = null;
            _blocked = false;
            _blockedReason = null;
            _externalScanRunning = false;
            SetStatusLocked();
        }

        _scanGate.Acquired -= OnScanGateAcquired;
        _ownChanges.Revoked -= OnOwnChangeRevoked;
        _messenger.UnregisterAll(this);
        _logger.LogInformation("Ordnerüberwachung beendet");
        Publish();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        StopAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            _disposed = true;
        }

        _quietTimer.Dispose();
        _retryTimer.Dispose();
        _publishTimer.Dispose();
    }

    // ---- Einstellungen -----------------------------------------------------------------------------

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _blocked = false;
            _blockedReason = null;
            if (!enabled)
            {
                CancelOwnScanLocked();
                _pendingEvents = 0;
                _coveredEvents = 0;
                _dueAtUtc = null;
                _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            if (_started)
            {
                RebuildWatchersLocked();
            }

            SetStatusLocked();
        }

        _logger.LogInformation("Ordnerüberwachung {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
        Publish();
        await PersistSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task SetQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken ct = default)
    {
        var quiet = WatchSettings.ClampQuietPeriod(quietPeriod);
        lock (_gate)
        {
            _quietPeriod = quiet;
            if (_pendingEvents > 0 && !_ownScanRunning && !_externalScanRunning && !_blocked)
            {
                ArmQuietTimerLocked();
            }

            SetStatusLocked();
        }

        _logger.LogInformation("Ordnerüberwachung: Ruhezeit {Quiet} s", (int)quiet.TotalSeconds);
        Publish();
        await PersistSettingsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Speichert den aktuellen Stand (nicht den Wert des Aufrufs): Speichervorgänge laufen nacheinander und lesen den
    /// Zustand erst, wenn sie an der Reihe sind. Schnelle Folgen (NumberBox-Pfeile, Tippen) können sich so nicht
    /// überholen — der zuletzt gesetzte Wert steht immer zuletzt in der Settings-Tabelle.
    /// </summary>
    private async Task PersistSettingsAsync(CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WatchSettings current;
            lock (_gate)
            {
                current = new WatchSettings(_enabled, _quietPeriod);
            }

            await _settingsStore.SaveAsync(current, ct).ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // ---- Bibliothek --------------------------------------------------------------------------------

    private void OnActiveLibraryChanged(ActiveLibraryChangedMessage message)
    {
        _logger.LogDebug("Ordnerüberwachung: ActiveLibraryChangedMessage LibraryId={LibraryId}", message.LibraryId);
        _ = AttachToActiveLibraryAsync(CancellationToken.None);
    }

    /// <summary>Wurzel hinzugefügt/entfernt/Rolle getauscht: Watcher der aktiven Bibliothek neu aufbauen.</summary>
    private void OnLibraryRootsChanged(LibraryRootsChangedMessage message)
    {
        _logger.LogDebug("Ordnerüberwachung: LibraryRootsChangedMessage LibraryId={LibraryId}", message.LibraryId);
        _ = AttachToActiveLibraryAsync(CancellationToken.None);
    }

    private async Task AttachToActiveLibraryAsync(CancellationToken ct)
    {
        int version;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            version = ++_attachVersion;
        }

        LibraryInfo? library = null;
        try
        {
            library = await _libraries.GetActiveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordnerüberwachung: aktive Bibliothek konnte nicht ermittelt werden");
        }

        var roots = library is null ? [] : GetRootPaths(library).ToArray();
        var exclusions = await ReadExclusionsAsync(library?.Id, ct).ConfigureAwait(false); // ADR-0066
        lock (_gate)
        {
            if (!_started || version != _attachVersion)
            {
                return; // ein jüngerer Wechsel ist bereits unterwegs
            }

            if (exclusions is not null)
            {
                _exclusions = exclusions;
            }

            var unchanged = _libraryId == library?.Id && _roots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase);
            if (!unchanged)
            {
                CancelOwnScanLocked();
                if (_libraryId != library?.Id)
                {
                    _libraryEpoch++; // Ergebnis eines noch laufenden Scans gehört nicht mehr zur aktiven Bibliothek
                }

                _libraryId = library?.Id;
                _roots = roots;
                _pendingEvents = 0;
                _coveredEvents = 0;
                _dueAtUtc = null;
                _blocked = false;
                _blockedReason = null;
                _externalScanRunning = false;
                _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _logger.LogInformation("Ordnerüberwachung: Bibliothek {LibraryId} ({Name}) mit {Count} Wurzel(n)", library?.Id, library?.Name ?? "-", roots.Length);
            }

            // Dieselbe Bibliothek erneut gemeldet und die Watcher laufen: nicht neu aufbauen (in der Lücke gingen Ereignisse verloren).
            if (!unchanged || (_watchers.Count == 0 && _unavailableRoots.Length == 0))
            {
                RebuildWatchersLocked();
            }

            SetStatusLocked();
        }

        Publish();
    }

    /// <summary>Watcher für die aktuellen Wurzeln (neu) aufbauen; Zähler und Sperre bleiben unberührt.</summary>
    private void RebuildWatchersLocked()
    {
        DisposeWatchersLocked();
        _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _unavailableRoots = [];
        if (!_started || !_enabled || _libraryId is null || _roots.Length == 0)
        {
            return;
        }

        var unavailable = new List<string>();
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                unavailable.Add(root);
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = WatcherBufferSize,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, null);
                watcher.Deleted += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, null);
                watcher.Changed += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, null);
                watcher.Renamed += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, e.OldFullPath);
                watcher.Error += (_, e) => OnWatcherError(root, e.GetException());
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                _logger.LogWarning(ex, "Ordnerüberwachung: Wurzel {Root} kann nicht überwacht werden", root);
                unavailable.Add(root);
            }
        }

        _unavailableRoots = unavailable.ToArray();
        if (unavailable.Count > 0)
        {
            _logger.LogWarning("Ordnerüberwachung: {Count} Wurzel(n) nicht erreichbar, erneuter Versuch in {Seconds} s: {Roots}", unavailable.Count, (int)_rootRetryInterval.TotalSeconds, string.Join(" | ", unavailable));
            _retryTimer.Change(_rootRetryInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisposeWatchersLocked()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ordnerüberwachung: Watcher konnte nicht sauber beendet werden");
            }
        }

        _watchers.Clear();
    }

    private void OnRetryTick()
    {
        lock (_gate)
        {
            if (!_started || !_enabled || _libraryId is null || _unavailableRoots.Length == 0)
            {
                return;
            }

            var before = _unavailableRoots.Length;
            RebuildWatchersLocked();
            if (_unavailableRoots.Length < before && !_blocked)
            {
                // Wurzel wieder da: währenddessen kann sich alles geändert haben → Scan nach Ruhezeit
                _pendingEvents++;
                ArmQuietTimerLocked();
            }

            SetStatusLocked();
        }

        Publish();
    }

    // ---- Ereignisse --------------------------------------------------------------------------------

    private void OnWatcherEvent(WatcherChangeTypes change, string fullPath, string? oldPath)
    {
        if (!_filter.IsRelevant(change, fullPath, oldPath))
        {
            return;
        }

        // ADR-0066: in ausgeschlossenen Ordnern (.thumbs in jeder Tiefe, Ordner der Bibliothek) nichts zählen. Ein Renamed zählt,
        // wenn eine der beiden Seiten nicht ausgeschlossen ist (Medium in/aus einem ausgeschlossenen Ordner verschoben).
        var exclusions = _exclusions;
        if (exclusions.IsExcludedPath(fullPath) && (oldPath is null || exclusions.IsExcludedPath(oldPath)))
        {
            _logger.LogDebug("Ordnerüberwachung: Ereignis in ausgeschlossenem Ordner ignoriert");
            return;
        }

        if (_ownChanges.IsOwn(fullPath) && (oldPath is null || _ownChanges.IsOwn(oldPath)))
        {
            _logger.LogDebug("Ordnerüberwachung: eigene Änderung ignoriert — {Change} {Path}", change, fullPath);
            return;
        }

        CountEvent(change.ToString(), fullPath, requireRoot: false);
    }

    private async Task ReloadExclusionsAsync()
    {
        int? libraryId;
        lock (_gate)
        {
            libraryId = _libraryId;
        }

        var exclusions = await ReadExclusionsAsync(libraryId, CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            if (exclusions is not null && _libraryId == libraryId)
            {
                _exclusions = exclusions; // nur, wenn inzwischen keine andere Bibliothek aktiv ist
            }
        }
    }

    /// <summary>Ausschlüsse der Bibliothek lesen (ADR-0066); null bei Fehler (die bisherigen bleiben).</summary>
    private async Task<Scanning.Exclusions.ScanExclusionSet?> ReadExclusionsAsync(int? libraryId, CancellationToken ct)
    {
        if (_exclusionStore is null)
        {
            return Scanning.Exclusions.ScanExclusionSet.None;
        }

        try
        {
            return libraryId is { } id
                ? await _exclusionStore.LoadSetAsync(id, ct).ConfigureAwait(false)
                : Scanning.Exclusions.ScanExclusionSet.None;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordnerüberwachung: Ausschlüsse konnten nicht gelesen werden");
            return null;
        }
    }

    /// <summary>Eine Operation der App hat einen Pfad zurückgenommen (Katalog nicht gebucht): zählt wie ein Ereignis.</summary>
    private void OnOwnChangeRevoked(string path) => CountEvent("zurückgenommen", path, requireRoot: true);

    /// <param name="requireRoot">true: nur zählen, wenn der Pfad unter einer überwachten Wurzel liegt (Ereignisse der Watcher tun das immer).</param>
    private void CountEvent(string what, string fullPath, bool requireRoot)
    {
        var publishNow = false;
        lock (_gate)
        {
            if (!_started || !_enabled || _libraryId is null || _blocked)
            {
                return;
            }

            if (requireRoot && !_roots.Any(root => IsUnderRoot(fullPath, root)))
            {
                return;
            }

            _pendingEvents++;
            ArmQuietTimerLocked();
            if (!IsScanRunningForActiveLibraryLocked())
            {
                var countdownOnly = _status.State == WatchState.ChangesPending && _status.LibraryId == _libraryId;
                SetStatusLocked();
                publishNow = ShouldPublishNowLocked(countdownOnly);
            }
        }

        _logger.LogDebug("Ordnerüberwachung: {Change} {Path}", what, fullPath);
        if (publishNow)
        {
            Publish();
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var full = OwnChangeRegistry.Normalize(path);
        var normalizedRoot = OwnChangeRegistry.Normalize(root);
        return full is not null && normalizedRoot is not null
               && (full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private void OnWatcherError(string root, Exception? error)
    {
        _logger.LogWarning(error, "Ordnerüberwachung: Fehler am Watcher für {Root} — Neustart, Scan wird eingeplant", root);
        // Nicht im Watcher-Callback selbst neu aufbauen (Dispose des eigenen Watchers).
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
            {
                if (!_started || !_enabled || _libraryId is null)
                {
                    return;
                }

                RebuildWatchersLocked();
                if (!_blocked)
                {
                    _pendingEvents++; // Ereignisse können verloren sein (Pufferüberlauf)
                    ArmQuietTimerLocked();
                }

                SetStatusLocked();
            }

            Publish();
        });
    }

    private void ArmQuietTimerLocked()
    {
        _dueAtUtc = DateTime.UtcNow + _quietPeriod;
        _quietTimer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
    }

    // ---- Scan --------------------------------------------------------------------------------------

    private void OnQuietElapsed()
    {
        int libraryId;
        long epoch;
        CancellationToken token;
        int events;
        lock (_gate)
        {
            if (!_started || !_enabled || _libraryId is null || _blocked || _pendingEvents == 0)
            {
                return;
            }

            if (_ownScanRunning || _externalScanRunning)
            {
                return; // wird nach dem Ende des laufenden Scans erneut geprüft
            }

            events = _pendingEvents;
            _pendingEvents = 0;
            _coveredEvents = 0;
            _dueAtUtc = null;
            _ownScanRunning = true;
            _ownScanEpoch = _libraryEpoch;
            _scanCts = new CancellationTokenSource();
            token = _scanCts.Token;
            libraryId = _libraryId.Value;
            epoch = _libraryEpoch;
            SetStatusLocked();
        }

        _logger.LogInformation("Ordnerüberwachung: {Events} Ereignis(se), Ruhezeit abgelaufen → Incremental-Scan der Bibliothek {LibraryId}", events, libraryId);
        Publish();
        _ = RunAutoScanAsync(libraryId, epoch, token);
    }

    private async Task RunAutoScanAsync(int libraryId, long epoch, CancellationToken token)
    {
        ScanResult? result = null;
        string? blockReason = null;
        try
        {
            result = await _scans.ScanAsync(libraryId, ScanMode.Incremental, _autoScanOptions, null, token).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case ScanOutcome.MassMissingBlocked:
                    blockReason = $"{result.MissingDetected:N0} fehlende Dateien — Bestätigung auf der Bibliotheksseite nötig („Aktualisieren“)";
                    break;
                case ScanOutcome.LibraryOffline:
                    blockReason = "Bibliothek offline — nach dem Anschließen auf der Bibliotheksseite „Aktualisieren“";
                    break;
                default:
                    _logger.LogInformation(
                        "Ordnerüberwachung: Scan fertig — {Added} neu, {Modified} geändert, {Missing} fehlend, {Reappeared} wieder da ({Duration:N1} s)",
                        result.Added, result.Modified, result.MarkedMissing, result.Reappeared, result.TotalDuration.TotalSeconds);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Ordnerüberwachung: automatischer Scan abgebrochen");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ordnerüberwachung: automatischer Scan fehlgeschlagen");
        }

        lock (_gate)
        {
            _ownScanRunning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            if (epoch != _libraryEpoch)
            {
                // Bibliothek gewechselt (oder Überwachung beendet), während der Scan lief: Sperre und Ergebnis gehören
                // zur vorigen Bibliothek und dürfen der aktiven nicht zugeordnet werden.
                _logger.LogInformation("Ordnerüberwachung: Ergebnis des automatischen Scans der Bibliothek {LibraryId} verworfen (Bibliothek gewechselt)", libraryId);
                if (_pendingEvents > 0 && _started && _enabled && !_blocked && !_externalScanRunning)
                {
                    ArmQuietTimerLocked(); // Ereignisse der neuen Bibliothek, die auf das Ende dieses Scans gewartet haben
                }

                SetStatusLocked();
            }
            else
            {
                if (blockReason is not null)
                {
                    _blocked = true;
                    _blockedReason = blockReason;
                    _pendingEvents = 0;
                    _dueAtUtc = null;
                    _logger.LogWarning("Ordnerüberwachung angehalten: {Reason}", blockReason);
                }
                else if (_pendingEvents > 0 && _started && _enabled && !_externalScanRunning)
                {
                    ArmQuietTimerLocked(); // während des Scans kamen weitere Ereignisse
                }

                SetStatusLocked(result is { Outcome: ScanOutcome.Completed } ? result : null);
            }
        }

        Publish();
    }

    private void CancelOwnScanLocked()
    {
        try
        {
            _scanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Scan ist gerade fertig geworden
        }
    }

    /// <summary>Läuft ein Scan der aktiven Bibliothek (eigener der aktuellen Epoche oder fremder)?</summary>
    private bool IsScanRunningForActiveLibraryLocked()
        => (_ownScanRunning && _ownScanEpoch == _libraryEpoch) || _externalScanRunning;

    // ---- Fremde Scans (Bibliotheksseite, Entpacken) ------------------------------------------------

    /// <summary>
    /// Ein Scan hat die Bibliothek belegt und liest das Dateisystem erst danach: alle bis hierher gesammelten Ereignisse
    /// sieht er. Sie werden verworfen (merkt sich <see cref="_coveredEvents"/> für den Fall, dass er nicht erfolgreich
    /// endet). Ein fremder Scan gilt ab hier als laufend (kein Countdown, kein eigener Scan) — nicht erst ab seiner
    /// ersten <see cref="ScanProgressMessage"/>; jeder belegende Scan endet mit ScanCompleted- oder ScanAbortedMessage.
    /// </summary>
    private void OnScanGateAcquired(int libraryId)
    {
        lock (_gate)
        {
            if (!_started || libraryId != _libraryId)
            {
                return;
            }

            if (_pendingEvents > 0)
            {
                _coveredEvents += _pendingEvents;
                _pendingEvents = 0;
                _dueAtUtc = null;
                _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            // Belegt der eigene Scan (oder ein fremder, während der eigene noch wartet), bleibt der eigene maßgeblich —
            // dessen Ende prüft die Ereignisse erneut.
            if (!_ownScanRunning)
            {
                _externalScanRunning = true;
            }

            SetStatusLocked();
        }

        Publish();
    }

    private void OnExternalScanProgress(ScanProgressMessage message)
    {
        var publish = false;
        lock (_gate)
        {
            if (!_started || message.LibraryId != _libraryId || IsScanRunningForActiveLibraryLocked())
            {
                return;
            }

            _externalScanRunning = true;
            if (_pendingEvents > 0)
            {
                _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _dueAtUtc = null;
                SetStatusLocked();
                publish = true;
            }
        }

        if (publish)
        {
            Publish();
        }
    }

    private void OnExternalScanFinished(int libraryId, bool completedOk)
    {
        lock (_gate)
        {
            if (!_started || libraryId != _libraryId || (_ownScanRunning && _ownScanEpoch == _libraryEpoch))
            {
                return;
            }

            _externalScanRunning = false;
            if (completedOk && _blocked)
            {
                _blocked = false;
                _blockedReason = null;
                _logger.LogInformation("Ordnerüberwachung: Sperre nach erfolgreichem manuellen Scan aufgehoben");
            }

            if (!completedOk && _coveredEvents > 0 && _enabled && !_blocked)
            {
                _pendingEvents += _coveredEvents; // abgebrochen/fehlgeschlagen: die abgedeckten Ereignisse gelten wieder
            }

            _coveredEvents = 0;
            if (_pendingEvents > 0 && _enabled && !_blocked)
            {
                ArmQuietTimerLocked();
            }

            SetStatusLocked();
        }

        Publish();
    }

    // ---- Zustand -----------------------------------------------------------------------------------

    private WatchStatus SetStatusLocked(ScanResult? lastAutoScan = null)
    {
        WatchState state;
        string? reason = null;
        if (!_started || !_enabled || _libraryId is null)
        {
            state = WatchState.Disabled;
        }
        else if (_blocked)
        {
            state = WatchState.Blocked;
            reason = _blockedReason;
        }
        else if (_ownScanRunning && _ownScanEpoch == _libraryEpoch)
        {
            state = WatchState.Scanning;
        }
        else if (_pendingEvents > 0 && _dueAtUtc is not null && !_externalScanRunning)
        {
            state = WatchState.ChangesPending;
        }
        else if (_unavailableRoots.Length > 0)
        {
            state = WatchState.RootUnavailable;
            reason = string.Join(" | ", _unavailableRoots);
        }
        else
        {
            state = WatchState.Idle;
        }

        _status = new WatchStatus(_libraryId, state, state == WatchState.ChangesPending ? _dueAtUtc : null, reason, lastAutoScan);
        _statusVersion++;
        return _status;
    }

    /// <summary>
    /// Drosselung reiner Countdown-Verschiebungen: sofort, wenn der letzte Countdown-Versand mindestens
    /// <see cref="CountdownPublishInterval"/> zurückliegt, sonst einmal nachlaufend über <see cref="_publishTimer"/>.
    /// Zustandswechsel gehen immer sofort hinaus.
    /// </summary>
    private bool ShouldPublishNowLocked(bool countdownOnly)
    {
        var now = Environment.TickCount64;
        var interval = (long)CountdownPublishInterval.TotalMilliseconds;
        var elapsed = now - _lastCountdownPublishTicks;
        if (!countdownOnly || elapsed >= interval)
        {
            _lastCountdownPublishTicks = now;
            return true;
        }

        if (!_publishScheduled)
        {
            _publishScheduled = true;
            _publishTimer.Change(TimeSpan.FromMilliseconds(interval - elapsed), Timeout.InfiniteTimeSpan);
        }

        return false;
    }

    private void OnPublishTick()
    {
        lock (_gate)
        {
            _publishScheduled = false;
            _lastCountdownPublishTicks = Environment.TickCount64;
        }

        Publish();
    }

    /// <summary>
    /// Sendet den jüngsten Zustand, falls er noch nicht gesendet wurde. Serialisiert über <see cref="_sendGate"/>: ein
    /// älterer Zustand kann einen jüngeren nie überholen (Watcher-, Timer- und Scan-Threads senden gleichzeitig).
    /// Nie unter <see cref="_gate"/> aufrufen.
    /// </summary>
    private void Publish()
    {
        lock (_sendGate)
        {
            WatchStatus status;
            long version;
            lock (_gate)
            {
                status = _status;
                version = _statusVersion;
            }

            if (version == _sentVersion)
            {
                return;
            }

            _sentVersion = version;
            try
            {
                _messenger.Send(new WatchStatusChangedMessage(status));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ordnerüberwachung: Statusnachricht konnte nicht zugestellt werden");
            }
        }
    }
}
