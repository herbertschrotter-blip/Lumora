using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Worker;

/// <summary>Wie eine Arbeit des Hilfsprozesses gescheitert ist.</summary>
public enum ImagingFailure
{
    /// <summary>Programmdatei fehlt (Paket unvollständig).</summary>
    NotFound = 0,

    /// <summary>Start oder Laden von OpenCV gescheitert (VC-Laufzeit, Virenscanner …).</summary>
    StartFailed = 1,

    /// <summary>Der Prozess ist während der Arbeit abgestürzt bzw. beendet worden.</summary>
    Crashed = 2,

    /// <summary>Keine Meldung mehr (hängt) bzw. Höchstdauer überschritten — Prozess beendet.</summary>
    TimedOut = 3,

    /// <summary>Der Prozess meldet einen Fehler der Arbeit (Code in <see cref="ImagingWorkerException.Code"/>).</summary>
    Error = 4,
}

/// <summary>Fehler einer Arbeit im Hilfsprozess; Lumora läuft weiter. Die Meldung enthält keine Pfade.</summary>
public sealed class ImagingWorkerException(ImagingFailure failure, string message, WorkerErrorCode? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ImagingFailure Failure { get; } = failure;

    public WorkerErrorCode? Code { get; } = code;
}

/// <summary>
/// Ein laufender Hilfsprozess (ADR-0078 Nachtrag 0.5.44 Punkt 5): startet <c>Lumora.Imaging.Worker.exe</c> ohne Fenster mit
/// TEMP/TMP im Temp-Ordner des Katalogs (in der versteckten Sitzung im Container) und abgeschaltetem OpenCL (die Umgebung
/// zählt nur beim Start — die C-Laufzeit liest sie einmal), spricht das Zeilen-Protokoll über stdin/stdout, meldet Fortschritt,
/// bricht ab (erst höflich, nach <see cref="ImagingWorkerOptions.CancelGrace"/> hart) und erkennt Absturz, Stille und
/// Zeitüberschreitung. Eine Arbeit zur Zeit. Thread-sicher.
/// </summary>
public sealed class ImagingWorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ImagingWorkerOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _jobLock = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly TaskCompletionSource<ReadyMessage> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;
    private readonly Task _errorReader;
    private Job? _job;
    private long _lastMessageTicks = Environment.TickCount64;
    private int _nextId;
    private int _disposed;

    private sealed class Job(int id, Action<ProgressMessage>? progress)
    {
        public int Id { get; } = id;

        public Action<ProgressMessage>? Progress { get; } = progress;

        public TaskCompletionSource<WorkerMessage> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private ImagingWorkerProcess(Process process, ImagingWorkerOptions options, ILogger logger)
    {
        _process = process;
        _options = options;
        _logger = logger;
        _reader = Task.Run(ReadOutputAsync);
        _errorReader = Task.Run(DrainErrorAsync);
    }

    /// <summary>OpenCV-Version des Hilfsprozesses (aus der ersten Zeile).</summary>
    public string OpenCvVersion { get; private set; } = string.Empty;

    /// <summary>Rechen-Threads des Hilfsprozesses beim Start.</summary>
    public int Threads { get; private set; }

    /// <summary>Prozess-Id (Tests: Absturz nachstellen).</summary>
    internal int ProcessId => _process.Id;

    /// <summary>Exit-Code nach dem Ende (Tests: Absturz ohne Fehlerbericht), sonst null.</summary>
    internal int? ExitCode
    {
        get
        {
            try
            {
                return _process.WaitForExit(5000) ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Startet den Hilfsprozess und wartet auf „ready“.</summary>
    /// <param name="tempRoot">Temp-Ordner des Katalogs (Arbeitsordner, TEMP/TMP).</param>
    /// <param name="threads">Rechen-Threads (Vordergrund alle Kerne, Hintergrund <c>BackgroundCpu.MaxThreads</c>).</param>
    public static async Task<ImagingWorkerProcess> StartAsync(ImagingWorkerOptions options, string tempRoot, int threads, ILogger? logger = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        var exe = options.ResolveExePath();
        if (!File.Exists(exe))
        {
            throw new ImagingWorkerException(ImagingFailure.NotFound, "Der Rechen-Prozess für „Zusammenfügen“ fehlt im Programmordner.");
        }

        Directory.CreateDirectory(tempRoot);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = tempRoot,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add("--threads");
        info.ArgumentList.Add(Math.Max(1, threads).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Nichts außerhalb des Katalogs (versteckte Sitzung, ADR-0049/0078 Punkt 13): Temp und OpenCV-Caches im Temp-Ordner, OpenCL aus
        info.Environment["TEMP"] = tempRoot;
        info.Environment["TMP"] = tempRoot;
        info.Environment["OPENCV_OPENCL_RUNTIME"] = "disabled";
        info.Environment["OPENCV_OPENCL_DEVICE"] = "disabled";
        info.Environment["OPENCV_OPENCL_CACHE_ENABLE"] = "0";
        info.Environment["OPENCV_TEMP_PATH"] = tempRoot;
        // Keine Absturz-Abbilder der .NET-Laufzeit (sie lägen außerhalb des Containers, 0.5.48) — nur für diesen Prozess
        info.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        info.Environment["DOTNET_EnableCrashReport"] = "0";
        foreach (var (key, value) in options.Environment)
        {
            info.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ImagingWorkerException(ImagingFailure.StartFailed, "Der Rechen-Prozess ließ sich nicht starten.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ImagingWorkerException(ImagingFailure.StartFailed, "Der Rechen-Prozess ließ sich nicht starten.", inner: ex);
        }

        var worker = new ImagingWorkerProcess(process, options, logger);
        try
        {
            var ready = await worker._ready.Task.WaitAsync(options.StartTimeout, ct).ConfigureAwait(false);
            if (ready.ProtocolVersion != WorkerProtocol.Version)
            {
                throw new ImagingWorkerException(ImagingFailure.StartFailed, "Der Rechen-Prozess passt nicht zu dieser Lumora-Version.");
            }

            worker.OpenCvVersion = ready.OpenCvVersion;
            worker.Threads = ready.Threads;
            return worker;
        }
        catch (TimeoutException ex)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw new ImagingWorkerException(ImagingFailure.StartFailed, "Der Rechen-Prozess hat sich nicht gemeldet.", inner: ex);
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Führt eine Arbeit aus und liefert die Antwort vom Typ <typeparamref name="TResult"/>. Abbrechen über <paramref name="ct"/>
    /// schickt „cancel“ und beendet den Prozess, wenn er nicht binnen <see cref="ImagingWorkerOptions.CancelGrace"/> antwortet.
    /// </summary>
    public async Task<TResult> RunAsync<TResult>(Func<int, WorkerMessage> request, Action<ProgressMessage>? progress, CancellationToken ct)
        where TResult : WorkerMessage
    {
        await _jobLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasExited)
            {
                throw new ImagingWorkerException(ImagingFailure.Crashed, "Der Rechen-Prozess ist beendet.");
            }

            var id = Interlocked.Increment(ref _nextId);
            var job = new Job(id, progress);
            lock (_stateLock)
            {
                _job = job;
                _lastMessageTicks = Environment.TickCount64;
            }

            Send(request(id));
            var started = Environment.TickCount64;
            long? cancelSentAt = null;
            using var registration = ct.Register(() =>
            {
                try
                {
                    Send(new CancelRequest { TargetId = id });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    // Prozess schon weg — der Wächter unten meldet das
                }
            });
            while (true)
            {
                var finished = await Task.WhenAny(job.Reply.Task, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
                if (finished == job.Reply.Task)
                {
                    break;
                }

                var now = Environment.TickCount64;
                if (ct.IsCancellationRequested)
                {
                    cancelSentAt ??= now;
                    if (now - cancelSentAt.Value > _options.CancelGrace.TotalMilliseconds)
                    {
                        Kill("Abbrechen ohne Antwort");
                        throw new OperationCanceledException(ct);
                    }
                }

                if (now - Interlocked.Read(ref _lastMessageTicks) > _options.SilenceTimeout.TotalMilliseconds)
                {
                    Kill("keine Meldung");
                    throw new ImagingWorkerException(ImagingFailure.TimedOut, "Der Rechen-Prozess hat zu lange nichts gemeldet und wurde beendet.");
                }

                if (now - started > _options.JobTimeout.TotalMilliseconds)
                {
                    Kill("Höchstdauer");
                    throw new ImagingWorkerException(ImagingFailure.TimedOut, "Die Berechnung hat zu lange gedauert und wurde beendet.");
                }
            }

            WorkerMessage reply;
            try
            {
                reply = await job.Reply.Task.ConfigureAwait(false);
            }
            catch (ImagingWorkerException) when (Volatile.Read(ref _disposed) != 0)
            {
                // Sitzung wurde geschlossen, während die Arbeit lief: das ist ein Abbruch, kein Absturz
                throw new OperationCanceledException(ct.IsCancellationRequested ? ct : new CancellationToken(true));
            }

            switch (reply)
            {
                case TResult result:
                    return result;
                case ErrorMessage { Code: WorkerErrorCode.Canceled }:
                    throw new OperationCanceledException(ct.IsCancellationRequested ? ct : new CancellationToken(true));
                case ErrorMessage error:
                    _logger.LogDebug("Zusammenfügen: Rechen-Prozess meldet {Code}: {Detail}", error.Code, error.Detail);
                    throw new ImagingWorkerException(ImagingFailure.Error, error.Detail, error.Code);
                default:
                    throw new ImagingWorkerException(ImagingFailure.Error, "Unerwartete Antwort des Rechen-Prozesses.");
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _job = null;
            }

            _jobLock.Release();
        }
    }

    /// <summary>Rechen-Threads ändern (wirksam ab dem nächsten parallelen Abschnitt).</summary>
    public void SetThreads(int threads)
    {
        try
        {
            Send(new ThreadsRequest { Threads = Math.Max(1, threads) });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Prozess schon weg
        }
    }

    private void Send(WorkerMessage message)
    {
        var line = WorkerProtocol.Encode(message);
        lock (_stateLock)
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var message = WorkerProtocol.Decode(line);
                if (message is null)
                {
                    continue;
                }

                Interlocked.Exchange(ref _lastMessageTicks, Environment.TickCount64);
                if (message is ReadyMessage ready && ready.Id == 0)
                {
                    _ready.TrySetResult(ready);
                    continue;
                }

                if (message is ErrorMessage { Id: 0 } startError)
                {
                    _ready.TrySetException(new ImagingWorkerException(ImagingFailure.StartFailed, "OpenCV ließ sich im Rechen-Prozess nicht laden.", startError.Code));
                    continue;
                }

                Job? job;
                lock (_stateLock)
                {
                    job = _job;
                }

                if (job is null || job.Id != message.Id)
                {
                    continue;
                }

                if (message is ProgressMessage progress)
                {
                    try
                    {
                        job.Progress?.Invoke(progress);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _logger.LogDebug(ex, "Zusammenfügen: Fortschritt nicht zugestellt");
                    }

                    continue;
                }

                job.Reply.TrySetResult(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Leitung zu
        }

        // Ende der Ausgabe = Prozess weg: laufende Arbeit und Start scheitern
        var code = ExitCodeOrNull();
        _ready.TrySetException(new ImagingWorkerException(ImagingFailure.StartFailed, "Der Rechen-Prozess hat sich beim Start beendet."));
        Job? pending;
        lock (_stateLock)
        {
            pending = _job;
        }

        if (pending is not null && !pending.Reply.Task.IsCompleted)
        {
            _logger.LogWarning("Zusammenfügen: Rechen-Prozess unerwartet beendet (Code {Code})", code);
            pending.Reply.TrySetException(new ImagingWorkerException(ImagingFailure.Crashed, "Der Rechen-Prozess ist unerwartet beendet worden — Lumora läuft weiter."));
        }
    }

    private async Task DrainErrorAsync()
    {
        try
        {
            var buffer = new char[4096];
            while (await _process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0)
            {
                // Meldungen nativer Bibliotheken: verwerfen (können Pfade enthalten)
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Leitung zu
        }
    }

    private int? ExitCodeOrNull()
    {
        try
        {
            return _process.WaitForExit(2000) ? _process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void Kill(string reason)
    {
        try
        {
            if (!_process.HasExited)
            {
                _logger.LogWarning("Zusammenfügen: Rechen-Prozess wird beendet ({Reason})", reason);
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // schon weg
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!HasExited)
            {
                Send(new QuitRequest());
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            // beendet ihn gleich hart
        }

        Kill("Sitzung geschlossen");
        try
        {
            // erst zurück, wenn der Prozess wirklich weg ist (offene Quellen, Arbeitsordner, Container aushängen — 0.5.48)
            using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(exited.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            _logger.LogWarning("Zusammenfügen: Rechen-Prozess hat sich nicht beendet");
        }

        try
        {
            await Task.WhenAll(_reader, _errorReader).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        // _jobLock bleibt: eine noch laufende RunAsync gibt ihn im finally frei (SemaphoreSlim ohne Wait-Handle hält nichts)
        _process.Dispose();
    }
}
