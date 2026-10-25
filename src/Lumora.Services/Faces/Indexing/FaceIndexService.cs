using System.Diagnostics;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Faces.Models;
using Lumora.Services.Library;
using Lumora.Services.Messaging;

namespace Lumora.Services.Faces.Indexing;

/// <summary>Stellschrauben des Index (Tests verkürzen sie).</summary>
public sealed record FaceIndexOptions
{
    public static FaceIndexOptions Default { get; } = new();

    public int ReadBatchSize { get; init; } = FaceIndexDefaults.ReadBatchSize;
    public int WriteBatchSize { get; init; } = FaceIndexDefaults.WriteBatchSize;
    public TimeSpan WriteInterval { get; init; } = FaceIndexDefaults.WriteInterval;
    public int LoadMaxEdge { get; init; } = FaceIndexDefaults.LoadMaxEdge;
    public int Prefetch { get; init; } = FaceIndexDefaults.Prefetch;

    /// <summary>Wartezeit, wenn der Katalog gesperrt ist (Umzug) oder ein Schreiber zu lange wartet.</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Höchstens so oft je Sekunde eine Fortschrittsmeldung.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>So viele Analysefehler hintereinander beenden den Durchgang (Modell/Grafikkarte defekt).</summary>
    public int MaxConsecutiveAnalyzeErrors { get; init; } = 3;

    public SqliteBusyRetryOptions? BusyRetry { get; init; }

    /// <summary>Erster Durchgang nach dem App-Start erst nach dieser Zeit (Galerie und Vorschaubilder haben Vorrang).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(10);
}

/// <inheritdoc cref="IFaceIndexService" />
public sealed class FaceIndexService : IFaceIndexService, IDisposable
{
    private readonly ILibraryService _libraries;
    private readonly IFaceAnalyzerFactory _analyzers;
    private readonly IFaceImageLoader _loader;
    private readonly IFaceIndexSettingsStore _settingsStore;
    private readonly ILogger<FaceIndexService> _logger;
    private readonly IMessenger _messenger;
    private readonly FaceIndexOptions _options;
    private readonly FaceIndexStore _store;
    private readonly Lumora.Services.Infrastructure.IGpuGate? _gpuGate;

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private Task? _worker;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource? _resumed;
    private volatile bool _rerun;
    private volatile bool _resetAnalyzer;
    private FaceIndexSettings _settings = FaceIndexSettings.Default;
    private FaceIndexStatus _status = FaceIndexStatus.Disabled;
    private IFaceAnalyzer? _analyzer;
    private bool _analyzerUsesGpu;
    private int _started;
    private int _disposed;
    private int _runs;
    private int _runLibraryId = -1;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    public FaceIndexService(
        ILibraryContextFactory factory,
        ILibraryService libraries,
        IFaceAnalyzerFactory analyzers,
        IFaceImageLoader loader,
        IFaceIndexSettingsStore settingsStore,
        ILogger<FaceIndexService> logger,
        IMessenger? messenger = null,
        FaceIndexOptions? options = null,
        Lumora.Services.Infrastructure.IGpuGate? gpuGate = null)
    {
        _gpuGate = gpuGate; // gemeinsame GPU-Sperre mit der Motiv-Erkennung (ADR-0052)
        _libraries = libraries;
        _analyzers = analyzers;
        _loader = loader;
        _settingsStore = settingsStore;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _options = options ?? FaceIndexOptions.Default;
        _store = new FaceIndexStore(factory, logger, _options.BusyRetry);
    }

    public FaceIndexStatus Status => Volatile.Read(ref _status);

    public FaceIndexSettings Settings => Volatile.Read(ref _settings);

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _resumed is not null;
            }
        }
    }

    /// <summary>Anzahl abgeschlossener Durchgänge (Tests).</summary>
    public int CompletedRuns => Volatile.Read(ref _runs);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _settings, await _settingsStore.LoadAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Gesichtserkennung: Einstellungen nicht lesbar — Standard (aus)");
        }

        _messenger.Register<FaceIndexService, ScanCompletedMessage>(this, static (r, _) => r.RequestRun());
        _messenger.Register<FaceIndexService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Added)
            {
                r.RequestRun();
            }
        });
        _messenger.Register<FaceIndexService, ActiveLibraryChangedMessage>(this, static (r, m) =>
        {
            // Die Nachricht kommt auch nach Scans derselben Bibliothek — dann läuft der Durchgang weiter.
            if (Volatile.Read(ref r._runLibraryId) != m.LibraryId)
            {
                r.CancelRun();
            }

            r.RequestRun();
        });
        _messenger.Register<FaceIndexService, FaceModelChangedMessage>(this, static (r, _) =>
        {
            r._resetAnalyzer = true;
            r.CancelRun();
            r.RequestRun();
        });

        _worker = Task.Run(WorkerLoopAsync);
        Publish(_settings.Enabled ? new FaceIndexStatus(FaceIndexState.Idle) : FaceIndexStatus.Disabled, force: true);
        _logger.LogInformation("Gesichtserkennung: Hintergrund-Index bereit (eingeschaltet: {Enabled}, Grafikkarte: {Gpu})", _settings.Enabled, _settings.UseGpu);
        RequestRun();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await UpdateSettingsAsync(s => s with { Enabled = enabled }, ct).ConfigureAwait(false);
        _logger.LogInformation("Gesichtserkennung {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
        if (!enabled)
        {
            CancelRun();
            Publish(FaceIndexStatus.Disabled, force: true);
        }

        RequestRun();
    }

    public async Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default)
    {
        await UpdateSettingsAsync(s => s with { UseGpu = useGpu }, ct).ConfigureAwait(false);
        _resetAnalyzer = true;
        CancelRun();
        RequestRun();
    }

    /// <summary>Änderungen nacheinander: jede baut auf dem gespeicherten Stand der vorigen auf (kein verlorener Schalter).</summary>
    private async Task UpdateSettingsAsync(Func<FaceIndexSettings, FaceIndexSettings> change, CancellationToken ct)
    {
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = change(Settings);
            await _settingsStore.SaveAsync(next, ct).ConfigureAwait(false);
            Volatile.Write(ref _settings, next);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (Status.State is FaceIndexState.Running or FaceIndexState.Waiting)
        {
            Publish(Status with { State = FaceIndexState.Paused }, force: true);
        }

        _logger.LogInformation("Gesichtserkennung: pausiert");
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_sync)
        {
            resumed = _resumed;
            _resumed = null;
        }

        if (resumed is null)
        {
            return;
        }

        if (Status.State == FaceIndexState.Paused)
        {
            Publish(Status with { State = FaceIndexState.Running }, force: true);
        }

        resumed.TrySetResult();
        _logger.LogInformation("Gesichtserkennung: fortgesetzt");
    }

    public void CancelRun()
    {
        lock (_sync)
        {
            _runCts?.Cancel();
        }
    }

    public void RequestRun()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Läuft gerade ein Durchgang, startet danach ein weiterer (neue Dateien mit höheren Ids, anderer Zustand).
        _rerun = true;
        _wake.Writer.TryWrite(true);
    }

    public async Task<int> RebuildAsync(CancellationToken ct = default)
    {
        var library = await _libraries.GetActiveAsync(ct).ConfigureAwait(false);
        if (library is null)
        {
            return 0;
        }

        CancelRun();
        var count = await _store.ResetScansAsync(library.Id, ct).ConfigureAwait(false);
        _logger.LogInformation("Gesichtserkennung: Index der Bibliothek {LibraryId} zurückgesetzt ({Count} Einträge)", library.Id, count);
        RequestRun();
        return count;
    }

    private async Task WorkerLoopAsync()
    {
        var stop = _stop.Token;
        try
        {
            if (_options.StartDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.StartDelay, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _wake.Reader.ReadAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _rerun = false;
            try
            {
                await RunOnceAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gesichtserkennung: Durchgang mit unerwartetem Fehler beendet");
                Publish(new FaceIndexStatus(FaceIndexState.Error, Message: ex.Message), force: true);
            }
            finally
            {
                Interlocked.Increment(ref _runs);
            }

            if (_rerun)
            {
                _wake.Writer.TryWrite(true);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stop)
    {
        var settings = Settings;
        if (!settings.Enabled)
        {
            ReleaseAnalyzer();
            Publish(FaceIndexStatus.Disabled, force: true);
            return;
        }

        if (!_analyzers.IsAvailable)
        {
            Publish(new FaceIndexStatus(FaceIndexState.ModelMissing, Message: "Modell nicht vorhanden"), force: true);
            return;
        }

        LibraryInfo? library;
        try
        {
            library = await _libraries.GetActiveAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCatalogUnavailable(ex))
        {
            await WaitForCatalogAsync(stop).ConfigureAwait(false);
            return;
        }

        if (library is null)
        {
            Publish(new FaceIndexStatus(FaceIndexState.Idle), force: true);
            return;
        }

        // Abbrechbar ab hier — auch während das Modell noch lädt (Ausschalten, Bibliothek gewechselt).
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_sync)
        {
            _runCts = run;
            _runLibraryId = library.Id;
        }

        IFaceAnalyzer? analyzer = null;
        try
        {
            try
            {
                analyzer = await Task.Run(() => EnsureAnalyzer(settings.UseGpu), run.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Gesichtserkennung: Modell konnte nicht geladen werden");
                Publish(new FaceIndexStatus(FaceIndexState.Error, Message: $"Modell konnte nicht geladen werden: {ex.Message}"), force: true);
                return;
            }

            run.Token.ThrowIfCancellationRequested();
            await IndexLibraryAsync(library.Id, analyzer, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            _logger.LogInformation("Gesichtserkennung: Durchgang abgebrochen");
            if (analyzer is not null && Settings.Enabled)
            {
                await PublishCountsAsync(library.Id, analyzer, FaceIndexState.Idle, "Abgebrochen", stop).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsCatalogUnavailable(ex))
        {
            await WaitForCatalogAsync(stop).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _runCts = null;
                _runLibraryId = -1;
            }
        }
    }

    private async Task IndexLibraryAsync(int libraryId, IFaceAnalyzer analyzer, CancellationToken ct)
    {
        var version = analyzer.ModelVersion;
        var (done, total) = await _store.CountAsync(libraryId, version, ct).ConfigureAwait(false);
        if (done >= total)
        {
            Publish(new FaceIndexStatus(FaceIndexState.Idle, done, total, 0, analyzer.ExecutionProvider, LibraryId: libraryId), force: true);
            return;
        }

        _logger.LogInformation(
            "Gesichtserkennung: Durchgang startet — Bibliothek {LibraryId}, {Done}/{Total} analysiert, {Provider}",
            libraryId, done, total, analyzer.ExecutionProvider);
        var state = new RunState(libraryId, analyzer, done, total);
        PublishRun(state, force: true);

        int? before = null;
        var pending = new List<FaceFileResult>();
        var lastWrite = Stopwatch.StartNew();
        try
        {
            await IndexBatchesAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Abbruch (0.4.41, Issue #20): schon analysierte, noch nicht geschriebene Fotos nicht wegwerfen — sonst
            // analysierte der nächste Durchgang sie erneut (je nachdem, ob das Schreib-Intervall gerade abgelaufen war).
            try
            {
                await FlushAsync(state, pending, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Katalog gerade zu (Beenden, Sitzungswechsel): dann eben im nächsten Durchgang — der Abbruch bleibt ein Abbruch
                _logger.LogWarning(ex, "Gesichtserkennung: {Count} fertige Ergebnisse beim Abbruch nicht geschrieben", pending.Count);
            }

            throw;
        }

        await FlushAsync(state, pending, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Gesichtserkennung: Durchgang fertig — {Processed} Fotos, {Faces} Gesichter, {Ms:N0} ms/Foto",
            state.Processed, state.FacesFound, state.MsPerPhoto);
        await PublishCountsAsync(libraryId, analyzer, FaceIndexState.Idle, null, ct, state.FacesFound, state.MsPerPhoto).ConfigureAwait(false);

        async Task IndexBatchesAsync()
        {
            while (true)
            {
                var candidates = await _store.ReadPendingAsync(libraryId, version, before, _options.ReadBatchSize, ct).ConfigureAwait(false);
                if (candidates.Count == 0)
                {
                    break;
                }

                before = candidates[^1].FileId;
                var loads = new Queue<Task<LoadOutcome>>();
                var next = 0;
                while (next < candidates.Count && loads.Count < Math.Max(1, _options.Prefetch))
                {
                    loads.Enqueue(StartLoad(candidates[next++]));
                }

                while (loads.Count > 0)
                {
                    await WaitWhilePausedAsync(state, ct).ConfigureAwait(false);
                    var outcome = await loads.Dequeue().ConfigureAwait(false);
                    if (next < candidates.Count)
                    {
                        loads.Enqueue(StartLoad(candidates[next++]));
                    }

                    FaceFileResult? result;
                    // Nie zugleich mit der Motiv-Erkennung auf der Grafikkarte (ADR-0052): Sperre je Foto, nur mit DirectML.
                    using (await AcquireGpuAsync(analyzer, outcome, ct).ConfigureAwait(false))
                    {
                        result = Analyze(outcome, analyzer, state);
                    }

                    if (result is not null)
                    {
                        pending.Add(result);
                    }

                    if (state.ConsecutiveErrors >= _options.MaxConsecutiveAnalyzeErrors)
                    {
                        await FlushAsync(state, pending, CancellationToken.None).ConfigureAwait(false);
                        _resetAnalyzer = true;
                        throw new InvalidOperationException($"Die Analyse scheiterte {state.ConsecutiveErrors}× hintereinander: {state.LastError}");
                    }

                    if (pending.Count >= _options.WriteBatchSize || (pending.Count > 0 && lastWrite.Elapsed >= _options.WriteInterval))
                    {
                        await FlushAsync(state, pending, ct).ConfigureAwait(false);
                        lastWrite.Restart();
                    }

                    PublishRun(state);
                    ct.ThrowIfCancellationRequested();
                }
            }
        }
    }

    /// <summary>Analyse einer geladenen Datei; null = nicht vermerken (Datei fehlt — später erneut versuchen).</summary>
    private FaceFileResult? Analyze(LoadOutcome outcome, IFaceAnalyzer analyzer, RunState state)
    {
        var version = analyzer.ModelVersion;
        FaceFileResult? result;
        if (outcome.Missing)
        {
            result = null;
        }
        else if (outcome.Image is null)
        {
            result = new FaceFileResult(outcome.Candidate.FileId, version, null, FaceErrorCodes.DecodeFailed);
            _logger.LogDebug("Gesichtserkennung: Datei {FileId} nicht dekodierbar: {Error}", outcome.Candidate.FileId, outcome.Error);
        }
        else
        {
            var thread = Thread.CurrentThread;
            var previous = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;
                var analysis = analyzer.Analyze(outcome.Image);
                result = new FaceFileResult(outcome.Candidate.FileId, version, analysis);
                state.ConsecutiveErrors = 0;
                state.FacesFound += analysis.Faces.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Vermerken (analyze_failed), sonst stünde dieselbe Datei bei jedem Durchgang wieder vorn. Mehrere Fehler
                // hintereinander deuten auf Modell/Grafikkarte: der Durchgang endet nach dem Schreiben (IndexLibraryAsync).
                state.ConsecutiveErrors++;
                state.LastError = ex.Message;
                _logger.LogWarning(ex, "Gesichtserkennung: Analyse von Datei {FileId} gescheitert", outcome.Candidate.FileId);
                result = new FaceFileResult(outcome.Candidate.FileId, version, null, FaceErrorCodes.AnalyzeFailed);
            }
            finally
            {
                thread.Priority = previous;
            }
        }

        state.Completed();
        return result;
    }

    private async Task<IDisposable?> AcquireGpuAsync(IFaceAnalyzer analyzer, LoadOutcome outcome, CancellationToken ct)
        => _gpuGate is not null && outcome.Image is not null && string.Equals(analyzer.ExecutionProvider, "DirectML", StringComparison.Ordinal)
            ? await _gpuGate.AcquireAsync(ct).ConfigureAwait(false)
            : null;

    private Task<LoadOutcome> StartLoad(FaceIndexCandidate candidate)
        => Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var previous = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;
                if (!File.Exists(candidate.Path))
                {
                    return new LoadOutcome(candidate, null, true, null);
                }

                return new LoadOutcome(candidate, _loader.Load(candidate.Path, _options.LoadMaxEdge), false, null);
            }
            catch (FileNotFoundException)
            {
                return new LoadOutcome(candidate, null, true, null);
            }
            catch (DirectoryNotFoundException)
            {
                return new LoadOutcome(candidate, null, true, null);
            }
            catch (Exception ex)
            {
                return new LoadOutcome(candidate, null, false, ex.Message);
            }
            finally
            {
                thread.Priority = previous;
            }
        });

    private async Task FlushAsync(RunState state, List<FaceFileResult> pending, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var batch = pending.ToList();
        pending.Clear();
        // Ein begonnener Stapel wird auch bei Abbruch noch geschrieben — die Arbeit ist getan.
        var written = await _store.WriteAsync(batch, CancellationToken.None).ConfigureAwait(false);
        state.Done += batch.Count(r => written.Contains(r.FileId));
        if (written.Count > 0)
        {
            _messenger.Send(new FacesChangedMessage(state.LibraryId, written));
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task WaitWhilePausedAsync(RunState state, CancellationToken ct)
    {
        Task? resumed;
        lock (_sync)
        {
            resumed = _resumed?.Task;
        }

        if (resumed is null)
        {
            return;
        }

        PublishRun(state, force: true);
        await resumed.WaitAsync(ct).ConfigureAwait(false);
        state.ResetClock();
        PublishRun(state, force: true);
    }

    private IFaceAnalyzer EnsureAnalyzer(bool useGpu)
    {
        if (_analyzer is not null && !_resetAnalyzer && _analyzerUsesGpu == useGpu)
        {
            return _analyzer;
        }

        ReleaseAnalyzer();
        _resetAnalyzer = false;
        var analyzer = _analyzers.Create(useGpu);
        _analyzer = analyzer;
        _analyzerUsesGpu = useGpu;
        return analyzer;
    }

    private void ReleaseAnalyzer()
    {
        var old = Interlocked.Exchange(ref _analyzer, null);
        old?.Dispose();
    }

    private async Task PublishCountsAsync(int libraryId, IFaceAnalyzer analyzer, FaceIndexState state, string? message, CancellationToken ct, int faces = 0, double ms = 0)
    {
        try
        {
            var (done, total) = await _store.CountAsync(libraryId, analyzer.ModelVersion, ct).ConfigureAwait(false);
            Publish(new FaceIndexStatus(state, done, total, ms, analyzer.ExecutionProvider, message, libraryId, faces), force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(new FaceIndexStatus(state, ExecutionProvider: analyzer.ExecutionProvider, Message: message, LibraryId: libraryId), force: true);
        }
    }

    private async Task WaitForCatalogAsync(CancellationToken stop)
    {
        _logger.LogInformation("Gesichtserkennung: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", _options.SuspendedRetryDelay.TotalSeconds);
        Publish(Status with { State = FaceIndexState.Waiting, Message = "Wartet auf den Katalog" }, force: true);
        await Task.Delay(_options.SuspendedRetryDelay, stop).ConfigureAwait(false);
        _rerun = true;
    }

    private static bool IsCatalogUnavailable(Exception ex)
        => ex is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex) || ex.InnerException is DatabaseSuspendedException;

    private DateTime _lastPublishUtc;

    private void PublishRun(RunState state, bool force = false)
    {
        var paused = IsPaused;
        Publish(
            new FaceIndexStatus(
                paused ? FaceIndexState.Paused : FaceIndexState.Running,
                state.Done,
                state.Total,
                state.MsPerPhoto,
                state.Analyzer.ExecutionProvider,
                null,
                state.LibraryId,
                state.FacesFound),
            force);
    }

    private void Publish(FaceIndexStatus status, bool force = false)
    {
        var now = DateTime.UtcNow;
        var previous = Status;
        Volatile.Write(ref _status, status);
        if (!force && previous.State == status.State && now - _lastPublishUtc < _options.StatusInterval)
        {
            return;
        }

        _lastPublishUtc = now;
        try
        {
            _messenger.Send(new FaceIndexStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der FaceIndexStatusMessage warf eine Ausnahme");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _messenger.UnregisterAll(this);
        _stop.Cancel();
        Resume();
        _wake.Writer.TryComplete();
        var stopped = true;
        try
        {
            stopped = _worker?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        }
        catch (AggregateException)
        {
            // Worker beim Beenden abgebrochen — erwartet.
        }

        // Steckt der Worker noch in einer Analyse oder einem wartenden Schreibvorgang, bleiben Modelle und Token
        // unangetastet (der Prozess endet ohnehin) — sonst liefe er gegen freigegebene ONNX-Sitzungen.
        if (stopped)
        {
            ReleaseAnalyzer();
        }
        else
        {
            _logger.LogInformation("Gesichtserkennung: Worker beim Beenden noch beschäftigt — Modelle bleiben bis zum Prozessende geladen");
        }
    }

    private sealed record LoadOutcome(FaceIndexCandidate Candidate, FaceImage? Image, bool Missing, string? Error);

    /// <summary>Zähler eines Durchgangs (nur der Worker schreibt).</summary>
    private sealed class RunState(int libraryId, IFaceAnalyzer analyzer, int done, int total)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public int LibraryId { get; } = libraryId;
        public IFaceAnalyzer Analyzer { get; } = analyzer;
        public int Done { get; set; } = done;
        public int Total { get; } = total;
        public int Processed { get; private set; }
        public int FacesFound { get; set; }
        public int ConsecutiveErrors { get; set; }
        public string? LastError { get; set; }
        public double MsPerPhoto { get; private set; }

        /// <summary>Gleitender Mittelwert der Zeit zwischen zwei fertigen Fotos (Laden parallel, also echte Rate).</summary>
        public void Completed()
        {
            var ms = _clock.Elapsed.TotalMilliseconds;
            _clock.Restart();
            Processed++;
            MsPerPhoto = MsPerPhoto <= 0 ? ms : MsPerPhoto * 0.9 + ms * 0.1;
        }

        /// <summary>Nach einer Pause zählt die Wartezeit nicht in die Rate.</summary>
        public void ResetClock() => _clock.Restart();
    }
}
