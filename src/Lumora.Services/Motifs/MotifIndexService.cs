using System.Diagnostics;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Faces;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Motifs.Models;
using Lumora.Services.Scanning;

namespace Lumora.Services.Motifs;

/// <summary>
/// Hintergrund-Index der Motiv-Erkennung (ADR-0052): rechnet für jede Datei der aktiven Bibliothek ohne Bild-Vektor der
/// aktuellen Modell-Version den SigLIP-Vektor (Fotos aus dem Vorschaubild bzw. der Datei, Videos aus dem
/// Vorschaubild) und schreibt Vektor und Treffer stapelweise. Neueste Dateien zuerst, ein Durchgang zur Zeit, niedrige
/// Priorität, Pause/Fortsetzen/Abbrechen; ruht, solange ein Scan oder Datei-Batch die Bibliothek belegt
/// (<see cref="IScanGate"/>), und rechnet nie zugleich mit der Gesichtserkennung auf der Grafikkarte
/// (<see cref="IGpuGate"/>). Startet beim App-Start (verzögert), nach jedem Scan, bei neuen Dateien und nach dem
/// Modell-Download — bestehende Kataloge werden so im Hintergrund nachgeholt.
/// </summary>
public interface IMotifIndexService
{
    MotifIndexStatus Status { get; }

    MotifSettings Settings { get; }

    bool IsPaused { get; }

    Task StartAsync(CancellationToken ct = default);

    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);

    Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default);

    /// <summary>Globale Schwelle ändern und die Treffer der aktiven Bibliothek neu berechnen (ohne neue Analyse).</summary>
    Task SetDefaultThresholdAsync(double threshold, CancellationToken ct = default);

    void Pause();

    void Resume();

    void CancelRun();

    void RequestRun();

    /// <summary>Alle Bild-Vektoren der aktiven Bibliothek verwerfen und neu rechnen; liefert die Zahl verworfener.</summary>
    Task<int> RebuildAsync(CancellationToken ct = default);
}

/// <summary>Stellschrauben (Tests verkürzen sie).</summary>
public sealed record MotifIndexOptions
{
    public static MotifIndexOptions Default { get; } = new();

    public int ReadBatchSize { get; init; } = MotifDefaultsValues.ReadBatchSize;
    public int WriteBatchSize { get; init; } = MotifDefaultsValues.WriteBatchSize;
    public TimeSpan WriteInterval { get; init; } = MotifDefaultsValues.WriteInterval;
    public int LoadMaxEdge { get; init; } = MotifDefaultsValues.LoadMaxEdge;
    public int Prefetch { get; init; } = MotifDefaultsValues.Prefetch;
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Wartezeit, solange ein Scan oder Datei-Batch die Bibliothek belegt.</summary>
    public TimeSpan ScanBusyDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaxConsecutiveAnalyzeErrors { get; init; } = 3;

    /// <summary>Erster Durchgang nach dem App-Start erst nach dieser Zeit (nach der Gesichtserkennung).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(15);
}

/// <inheritdoc cref="IMotifIndexService" />
public sealed class MotifIndexService : IMotifIndexService, IDisposable
{
    private readonly ILibraryService _libraries;
    private readonly IMotifEncoderFactory _encoders;
    private readonly IFaceImageLoader _loader;
    private readonly IMotifSettingsStore _settingsStore;
    private readonly IMotifTermService _terms;
    private readonly MotifStore _store;
    private readonly IScanGate? _scanGate;
    private readonly IGpuGate? _gpuGate;
    private readonly Background.IBackgroundPlanner? _planner;
    private readonly ILogger<MotifIndexService> _logger;
    private readonly IMessenger _messenger;
    private readonly MotifIndexOptions _options;

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private Task? _worker;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource? _resumed;
    private volatile bool _rerun;
    private volatile bool _resetEncoder;
    private MotifSettings _settings = MotifSettings.Default;
    private MotifIndexStatus _status = MotifIndexStatus.Disabled;
    private IMotifImageEncoder? _encoder;
    private bool _encoderUsesGpu;
    private int _started;
    private int _disposed;
    private int _runs;
    private int _runLibraryId = -1;
    private DateTime _lastPublishUtc;

    public MotifIndexService(
        ILibraryService libraries,
        IMotifEncoderFactory encoders,
        IFaceImageLoader loader,
        IMotifSettingsStore settingsStore,
        IMotifTermService terms,
        MotifStore store,
        ILogger<MotifIndexService> logger,
        IScanGate? scanGate = null,
        IGpuGate? gpuGate = null,
        IMessenger? messenger = null,
        MotifIndexOptions? options = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _planner = planner;
        _libraries = libraries;
        _encoders = encoders;
        _loader = loader;
        _settingsStore = settingsStore;
        _terms = terms;
        _store = store;
        _logger = logger;
        _scanGate = scanGate;
        _gpuGate = gpuGate;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _options = options ?? MotifIndexOptions.Default;
    }

    public MotifIndexStatus Status => Volatile.Read(ref _status);

    public MotifSettings Settings => Volatile.Read(ref _settings);

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
            _logger.LogWarning(ex, "Motive: Einstellungen nicht lesbar — Standard");
        }

        _messenger.Register<MotifIndexService, ScanCompletedMessage>(this, static (r, _) => r.RequestRun());
        _messenger.Register<MotifIndexService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Added)
            {
                r.RequestRun();
            }
        });
        _messenger.Register<MotifIndexService, ActiveLibraryChangedMessage>(this, static (r, m) =>
        {
            if (Volatile.Read(ref r._runLibraryId) != m.LibraryId)
            {
                r.CancelRun();
            }

            r.RequestRun();
        });
        _messenger.Register<MotifIndexService, MotifModelChangedMessage>(this, static (r, _) =>
        {
            r._resetEncoder = true;
            r.CancelRun();
            r.RequestRun();
        });

        _worker = Task.Run(WorkerLoopAsync);
        Publish(_settings.Enabled ? new MotifIndexStatus(MotifIndexState.Idle) : MotifIndexStatus.Disabled, force: true);
        _logger.LogInformation("Motive: Hintergrund-Index bereit (eingeschaltet: {Enabled}, Grafikkarte: {Gpu}, Modell: {Model})",
            _settings.Enabled, _settings.UseGpu, _encoders.IsAvailable ? "vorhanden" : "fehlt");
        RequestRun();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await UpdateSettingsAsync(s => s with { Enabled = enabled }, ct).ConfigureAwait(false);
        _logger.LogInformation("Motiv-Erkennung {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
        if (!enabled)
        {
            CancelRun();
            Publish(MotifIndexStatus.Disabled, force: true);
        }

        RequestRun();
    }

    public async Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default)
    {
        await UpdateSettingsAsync(s => s with { UseGpu = useGpu }, ct).ConfigureAwait(false);
        _resetEncoder = true;
        CancelRun();
        RequestRun();
    }

    public async Task SetDefaultThresholdAsync(double threshold, CancellationToken ct = default)
    {
        if (threshold <= 0 || threshold >= 1 || double.IsNaN(threshold))
        {
            throw new MotifTermException("Die Schwelle muss zwischen 0 und 1 liegen.");
        }

        await UpdateSettingsAsync(s => s with { DefaultThreshold = threshold }, ct).ConfigureAwait(false);
        var library = await _libraries.GetActiveAsync(ct).ConfigureAwait(false);
        if (library is not null)
        {
            await _terms.RecomputeAllAsync(library.Id, ct).ConfigureAwait(false);
        }
    }

    private async Task UpdateSettingsAsync(Func<MotifSettings, MotifSettings> change, CancellationToken ct)
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

        if (Status.State is MotifIndexState.Running or MotifIndexState.Waiting)
        {
            Publish(Status with { State = MotifIndexState.Paused }, force: true);
        }

        _logger.LogInformation("Motive: pausiert");
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

        if (Status.State == MotifIndexState.Paused)
        {
            Publish(Status with { State = MotifIndexState.Running }, force: true);
        }

        resumed.TrySetResult();
        _logger.LogInformation("Motive: fortgesetzt");
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
        var count = await _store.ResetAsync(library.Id, ct).ConfigureAwait(false);
        _logger.LogInformation("Motive: Vektoren der Bibliothek {LibraryId} verworfen ({Count})", library.Id, count);
        _messenger.Send(new MotifsChangedMessage(library.Id, []));
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
                _logger.LogError(ex, "Motive: Durchgang mit unerwartetem Fehler beendet");
                Publish(new MotifIndexStatus(MotifIndexState.Error, Message: ex.Message), force: true);
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
            ReleaseEncoder();
            Publish(MotifIndexStatus.Disabled, force: true);
            return;
        }

        if (!_encoders.IsAvailable)
        {
            Publish(new MotifIndexStatus(MotifIndexState.ModelMissing, Message: "Modell nicht vorhanden"), force: true);
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
            Publish(new MotifIndexStatus(MotifIndexState.Idle), force: true);
            return;
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_sync)
        {
            _runCts = run;
            _runLibraryId = library.Id;
        }

        IMotifImageEncoder? encoder = null;
        try
        {
            // Begriffe anlegen bzw. fehlende Text-Vektoren rechnen (nach dem Download oder einem Modellwechsel).
            try
            {
                await _terms.EnsureTextVectorsAsync(library.Id, run.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !IsCatalogUnavailable(ex))
            {
                _logger.LogWarning(ex, "Motive: Text-Vektoren konnten nicht gerechnet werden — Bild-Vektoren werden trotzdem berechnet");
            }

            try
            {
                encoder = await Task.Run(() => EnsureEncoder(settings.UseGpu), run.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Motive: Bildmodell konnte nicht geladen werden");
                Publish(new MotifIndexStatus(MotifIndexState.Error, Message: $"Modell konnte nicht geladen werden: {ex.Message}"), force: true);
                return;
            }

            run.Token.ThrowIfCancellationRequested();
            await IndexLibraryAsync(library.Id, encoder, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            _logger.LogInformation("Motive: Durchgang abgebrochen");
            if (encoder is not null && Settings.Enabled)
            {
                await PublishCountsAsync(library.Id, encoder, "Abgebrochen", stop).ConfigureAwait(false);
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

    private async Task IndexLibraryAsync(int libraryId, IMotifImageEncoder encoder, CancellationToken ct)
    {
        var version = encoder.ModelVersion;
        var (done, total) = await _store.CountAsync(libraryId, version, ct).ConfigureAwait(false);
        if (done >= total)
        {
            Publish(new MotifIndexStatus(MotifIndexState.Idle, done, total, 0, encoder.ExecutionProvider, LibraryId: libraryId), force: true);
            return;
        }

        _logger.LogInformation("Motive: Durchgang startet — Bibliothek {LibraryId}, {Done}/{Total} analysiert, {Provider}", libraryId, done, total, encoder.ExecutionProvider);
        var state = new RunState(libraryId, encoder, done, total);
        PublishRun(state, force: true);

        int? before = null;
        var pending = new List<MotifFileResult>();
        var lastWrite = Stopwatch.StartNew();
        while (true)
        {
            await WaitWhileScanningAsync(state, ct).ConfigureAwait(false);
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

                MotifFileResult? result;
                using (await AcquireGpuAsync(encoder, outcome, ct).ConfigureAwait(false))
                {
                    result = Analyze(outcome, encoder, state);
                }

                if (result is not null)
                {
                    pending.Add(result);
                }

                if (state.ConsecutiveErrors >= _options.MaxConsecutiveAnalyzeErrors)
                {
                    await FlushAsync(state, pending, CancellationToken.None).ConfigureAwait(false);
                    _resetEncoder = true;
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

        await FlushAsync(state, pending, ct).ConfigureAwait(false);
        _logger.LogInformation("Motive: Durchgang fertig — {Processed} Dateien, {Ms:N0} ms/Foto", state.Processed, state.MsPerPhoto);
        await PublishCountsAsync(libraryId, encoder, null, ct, state.MsPerPhoto).ConfigureAwait(false);
    }

    /// <summary>Solange ein Scan oder Datei-Batch die Bibliothek belegt, ruht der Index (wie das Nachtragen der Aufnahmedaten).</summary>
    private async Task WaitWhileScanningAsync(RunState state, CancellationToken ct)
    {
        while (_scanGate is { } gate && gate.IsBusy(state.LibraryId))
        {
            Publish(new MotifIndexStatus(MotifIndexState.Waiting, state.Done, state.Total, state.MsPerPhoto, state.Encoder.ExecutionProvider, "Wartet auf den Scan", state.LibraryId), force: true);
            await Task.Delay(_options.ScanBusyDelay, ct).ConfigureAwait(false);
            state.ResetClock();
        }

        // Hintergrund-Planer (ADR-0074): Einlesen anderer Bibliotheken, Katalog-Umzug, „Alle pausieren“
        if (await Background.BackgroundPlanner.WaitForPermissionAsync(_planner, Background.BackgroundJob.Motifs, ct).ConfigureAwait(false))
        {
            state.ResetClock();
        }
    }

    private MotifFileResult? Analyze(LoadOutcome outcome, IMotifImageEncoder encoder, RunState state)
    {
        var version = encoder.ModelVersion;
        MotifFileResult? result;
        if (outcome.Missing)
        {
            result = null;
        }
        else if (outcome.Image is null)
        {
            result = new MotifFileResult(outcome.Candidate.FileId, version, null, MotifErrorCodes.DecodeFailed);
            _logger.LogDebug("Motive: Datei {FileId} nicht dekodierbar: {Error}", outcome.Candidate.FileId, outcome.Error);
        }
        else
        {
            var thread = Thread.CurrentThread;
            var previous = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;
                result = new MotifFileResult(outcome.Candidate.FileId, version, encoder.EncodeImage(outcome.Image));
                state.ConsecutiveErrors = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.ConsecutiveErrors++;
                state.LastError = ex.Message;
                _logger.LogWarning(ex, "Motive: Analyse von Datei {FileId} gescheitert", outcome.Candidate.FileId);
                result = new MotifFileResult(outcome.Candidate.FileId, version, null, MotifErrorCodes.AnalyzeFailed);
            }
            finally
            {
                thread.Priority = previous;
            }
        }

        state.Completed();
        return result;
    }

    private async Task<IDisposable?> AcquireGpuAsync(IMotifImageEncoder encoder, LoadOutcome outcome, CancellationToken ct)
        => _gpuGate is not null && outcome.Image is not null && string.Equals(encoder.ExecutionProvider, "DirectML", StringComparison.Ordinal)
            ? await _gpuGate.AcquireAsync(ct).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Bild laden: das fertige Vorschaubild (kurze Kante bis 400 px, Orientierung schon angewandt; ≈ 1 ms statt ≈ 45 ms
    /// für das Original), sonst bei Fotos die Datei selbst (längste Kante <see cref="MotifIndexOptions.LoadMaxEdge"/>).
    /// </summary>
    private Task<LoadOutcome> StartLoad(MotifCandidate candidate)
        => Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var previous = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;
                if (candidate.ThumbnailPath is { } thumb && File.Exists(thumb))
                {
                    try
                    {
                        return new LoadOutcome(candidate, _loader.Load(thumb, _options.LoadMaxEdge), false, null);
                    }
                    catch (Exception ex) when (candidate.MediaType == 0 && ex is not OutOfMemoryException)
                    {
                        // Vorschaubild kaputt oder gerade ersetzt → Original
                    }
                }

                if (candidate.MediaType != 0)
                {
                    return new LoadOutcome(candidate, null, true, null); // Video ohne Vorschaubild: später erneut
                }

                if (!File.Exists(candidate.SourcePath))
                {
                    return new LoadOutcome(candidate, null, true, null);
                }

                return new LoadOutcome(candidate, _loader.Load(candidate.SourcePath, _options.LoadMaxEdge), false, null);
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

    private async Task FlushAsync(RunState state, List<MotifFileResult> pending, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var batch = pending.ToList();
        pending.Clear();
        var written = await _store.WriteAsync(state.LibraryId, batch, _encoders.Scoring, Settings.DefaultThreshold, _encoders.ModelVersion, CancellationToken.None).ConfigureAwait(false);
        state.Done += written.Count;
        if (written.Count > 0)
        {
            _messenger.Send(new MotifsChangedMessage(state.LibraryId, written));
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

    private IMotifImageEncoder EnsureEncoder(bool useGpu)
    {
        if (_encoder is not null && !_resetEncoder && _encoderUsesGpu == useGpu)
        {
            return _encoder;
        }

        ReleaseEncoder();
        _resetEncoder = false;
        var encoder = _encoders.CreateImageEncoder(useGpu);
        _encoder = encoder;
        _encoderUsesGpu = useGpu;
        return encoder;
    }

    private void ReleaseEncoder()
    {
        var old = Interlocked.Exchange(ref _encoder, null);
        old?.Dispose();
    }

    private async Task PublishCountsAsync(int libraryId, IMotifImageEncoder encoder, string? message, CancellationToken ct, double ms = 0)
    {
        try
        {
            var (done, total) = await _store.CountAsync(libraryId, encoder.ModelVersion, ct).ConfigureAwait(false);
            Publish(new MotifIndexStatus(MotifIndexState.Idle, done, total, ms, encoder.ExecutionProvider, message, libraryId), force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(new MotifIndexStatus(MotifIndexState.Idle, ExecutionProvider: encoder.ExecutionProvider, Message: message, LibraryId: libraryId), force: true);
        }
    }

    private async Task WaitForCatalogAsync(CancellationToken stop)
    {
        _logger.LogInformation("Motive: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", _options.SuspendedRetryDelay.TotalSeconds);
        Publish(Status with { State = MotifIndexState.Waiting, Message = "Wartet auf den Katalog" }, force: true);
        await Task.Delay(_options.SuspendedRetryDelay, stop).ConfigureAwait(false);
        _rerun = true;
    }

    private static bool IsCatalogUnavailable(Exception ex)
        => ex is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex) || ex.InnerException is DatabaseSuspendedException;

    private void PublishRun(RunState state, bool force = false)
        => Publish(
            new MotifIndexStatus(
                IsPaused ? MotifIndexState.Paused : MotifIndexState.Running,
                state.Done,
                state.Total,
                state.MsPerPhoto,
                state.Encoder.ExecutionProvider,
                null,
                state.LibraryId),
            force);

    private void Publish(MotifIndexStatus status, bool force = false)
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
            _messenger.Send(new MotifIndexStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der MotifIndexStatusMessage warf eine Ausnahme");
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
            // beim Beenden abgebrochen — erwartet
        }

        if (stopped)
        {
            ReleaseEncoder();
        }
    }

    private sealed record LoadOutcome(MotifCandidate Candidate, FaceImage? Image, bool Missing, string? Error);

    private sealed class RunState(int libraryId, IMotifImageEncoder encoder, int done, int total)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public int LibraryId { get; } = libraryId;
        public IMotifImageEncoder Encoder { get; } = encoder;
        public int Done { get; set; } = done;
        public int Total { get; } = total;
        public int Processed { get; private set; }
        public int ConsecutiveErrors { get; set; }
        public string? LastError { get; set; }
        public double MsPerPhoto { get; private set; }

        public void Completed()
        {
            var ms = _clock.Elapsed.TotalMilliseconds;
            _clock.Restart();
            Processed++;
            MsPerPhoto = MsPerPhoto <= 0 ? ms : MsPerPhoto * 0.9 + ms * 0.1;
        }

        public void ResetClock() => _clock.Restart();
    }
}
