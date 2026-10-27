using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Lumora.Services.AiEdit;

/// <summary>Zustand der ComfyUI-Laufzeit.</summary>
public enum ComfyRuntimeState
{
    /// <summary>Nicht startbar: kein Container, nicht eingerichtet oder ungeprüfte Version (siehe Installation).</summary>
    Unavailable,

    Stopped,

    Starting,

    Ready,

    Stopping,

    /// <summary>Start oder Lauf gescheitert; <see cref="ComfyRuntimeStatus.Message"/> sagt warum.</summary>
    Failed,
}

/// <summary>Grafikkarte laut <c>/system_stats</c>.</summary>
public sealed record ComfyDevice(string Name, string Type, long VramTotalBytes);

/// <summary>Momentaufnahme für die Ansicht; der Port wird nirgends angezeigt (ADR-0076 Punkt 5).</summary>
public sealed record ComfyRuntimeStatus(
    ComfyRuntimeState State,
    ComfyInstallation Installation,
    ComfyDevice? Device = null,
    DateTimeOffset? ReadySince = null,
    string? Message = null);

/// <summary>Zeitgrenzen der Laufzeit (Tests: kürzer).</summary>
public sealed record ComfyRuntimeOptions
{
    /// <summary>Erster Start lädt PyTorch und CUDA — das dauert auf dem Zielrechner bis zu einer Minute.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Höchstens so lange auf das Prozessende warten (ADR-0076 Punkt 4).</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    public static ComfyRuntimeOptions Default { get; } = new();
}

/// <summary>
/// ComfyUI-Laufzeit der versteckten Sitzung (ADR-0076 Punkt 3/4): erkennt die Installation im Container, startet ComfyUI
/// nur bei Bedarf auf 127.0.0.1 mit zufälligem Port und beendet es beim Zusperren, beim Ende der Sitzung, beim Beenden von
/// Lumora und nach Leerlauf — danach sind die Arbeitsordner leer. Nur im DI-Container der versteckten Sitzung registriert.
/// </summary>
public interface IComfyRuntime
{
    ComfyRuntimeStatus Status { get; }

    /// <summary>Zustand geändert (aus beliebigem Thread).</summary>
    event Action? StatusChanged;

    /// <summary>Nach so langer Zeit ohne Auftrag und ohne Aktivität beenden; <see cref="TimeSpan.Zero"/> = nie.</summary>
    TimeSpan IdleTimeout { get; set; }

    /// <summary>Basisadresse des laufenden Servers (für den API-Client), sonst null.</summary>
    Uri? BaseAddress { get; }

    /// <summary>Installation neu erkennen (Container geändert, Version getauscht); ändert einen laufenden Prozess nicht.</summary>
    ComfyInstallation Refresh();

    /// <summary>Startet ComfyUI, falls es nicht schon läuft, und wartet, bis <c>/system_stats</c> antwortet.</summary>
    Task<ComfyRuntimeStatus> EnsureStartedAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>/interrupt</c> → Prozessbaum beenden → höchstens <see cref="ComfyRuntimeOptions.StopTimeout"/> warten → Arbeitsordner
    /// leeren. Ein laufender Start wird abgebrochen.
    /// </summary>
    Task StopAsync(string reason);

    /// <summary>Ein Auftrag läuft: solange gehalten, endet ComfyUI nicht durch Leerlauf. Dispose meldet ab.</summary>
    IDisposable BeginWork();

    /// <summary>Bedienung in der Ansicht: setzt die Leerlauf-Uhr zurück.</summary>
    void NotifyActivity();
}

/// <inheritdoc cref="IComfyRuntime" />
public sealed partial class ComfyRuntime : IComfyRuntime, IDisposable
{
    private const int OutputTailLines = 40;

    private readonly Func<string?> _containerRoot;
    private readonly IComfyProcessLauncher _launcher;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Func<int> _freePort;
    private readonly TimeProvider _time;
    private readonly ComfyRuntimeOptions _options;
    private readonly ILogger<ComfyRuntime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Queue<string> _outputTail = new();
    private readonly ITimer _idleTimer;
    private readonly Func<CancellationToken, Task<int>>? _loadIdleMinutes;

    private ComfyRuntimeStatus _status;
    private IComfyProcess? _process;
    private HttpClient? _http;
    private AiEditLayout? _layout;
    private CancellationTokenSource? _startCts;
    private DateTimeOffset _lastActivity;
    private int _leases;
    private int _stopRequests;
    private TimeSpan _idleTimeout = TimeSpan.FromMinutes(AiEditSettings.DefaultIdleMinutes);
    private bool _disposed;

    public ComfyRuntime(
        Func<string?> containerRoot,
        IComfyProcessLauncher launcher,
        ILogger<ComfyRuntime> logger,
        Func<HttpMessageHandler>? handlerFactory = null,
        Func<int>? freePort = null,
        TimeProvider? time = null,
        ComfyRuntimeOptions? options = null,
        Func<CancellationToken, Task<int>>? loadIdleMinutes = null)
    {
        _loadIdleMinutes = loadIdleMinutes;
        _containerRoot = containerRoot;
        _launcher = launcher;
        _logger = logger;
        _handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        _freePort = freePort ?? FindFreeLoopbackPort;
        _time = time ?? TimeProvider.System;
        _options = options ?? ComfyRuntimeOptions.Default;
        _lastActivity = _time.GetUtcNow();
        _status = StatusFor(ComfyInstallation.Detect(containerRoot()));
        _idleTimer = _time.CreateTimer(_ => OnIdleTick(), null, _options.IdleCheckInterval, _options.IdleCheckInterval);
    }

    public event Action? StatusChanged;

    public ComfyRuntimeStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public TimeSpan IdleTimeout
    {
        get
        {
            lock (_lock)
            {
                return _idleTimeout;
            }
        }
        set
        {
            lock (_lock)
            {
                _idleTimeout = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            }
        }
    }

    public Uri? BaseAddress
    {
        get
        {
            lock (_lock)
            {
                return _status.State == ComfyRuntimeState.Ready ? _http?.BaseAddress : null;
            }
        }
    }

    public ComfyInstallation Refresh()
    {
        var installation = ComfyInstallation.Detect(_containerRoot());
        lock (_lock)
        {
            // Läuft gerade etwas, oder ist der letzte Start gescheitert (Meldung bleibt stehen), bleibt der Zustand
            if (_status.State is ComfyRuntimeState.Starting or ComfyRuntimeState.Ready or ComfyRuntimeState.Stopping
                || (_status.State == ComfyRuntimeState.Failed && installation.CanStart))
            {
                return installation;
            }
        }

        SetStatus(StatusFor(installation));
        return installation;
    }

    public async Task<ComfyRuntimeStatus> EnsureStartedAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NotifyActivity();
        if (IsRunningAndReady())
        {
            return Status;
        }

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Ein Beenden, das ankommt, während dieser Start noch auf die Sperre wartet, gilt auch für ihn — sonst startete
        // ComfyUI nach dem Zusperren (der Container bliebe offen).
        var stopsBefore = Volatile.Read(ref _stopRequests);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunningAndReady() || _disposed || Volatile.Read(ref _stopRequests) != stopsBefore)
            {
                return Status;
            }

            lock (_lock)
            {
                _startCts = startCts;
            }

            await CleanUpProcessAsync(interrupt: false).ConfigureAwait(false);
            return await StartCoreAsync(startCts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _startCts = null;
            }

            _gate.Release();
        }
    }

    public async Task StopAsync(string reason)
    {
        Interlocked.Increment(ref _stopRequests);
        CancelStart();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IDisposable BeginWork()
    {
        Interlocked.Increment(ref _leases);
        NotifyActivity();
        return new WorkLease(this);
    }

    public void NotifyActivity()
    {
        lock (_lock)
        {
            _lastActivity = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Synchron (Container-Dispose beim Zusperren und beim Beenden): beendet ComfyUI und leert die Arbeitsordner, bevor der
    /// Container ausgehängt wird.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer.Dispose();
        try
        {
            if (!Task.Run(() => StopAsync("Sitzung beendet")).Wait(_options.StopTimeout + TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("KI-Bearbeitung: Beenden dauerte zu lange — Prozess wird hart beendet");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("KI-Bearbeitung: Beenden nicht sauber ({Type})", ex.GetType().Name);
        }
        finally
        {
            IComfyProcess? process;
            lock (_lock)
            {
                process = _process;
                _process = null;
            }

            if (process is not null)
            {
                process.KillTree();
                process.Dispose();
            }

            _http?.Dispose();
        }
    }

    private bool IsRunningAndReady()
    {
        lock (_lock)
        {
            return _status.State == ComfyRuntimeState.Ready && _process is { HasExited: false };
        }
    }

    private async Task<ComfyRuntimeStatus> StartCoreAsync(CancellationToken ct)
    {
        var installation = ComfyInstallation.Detect(_containerRoot());
        if (!installation.CanStart || installation.Layout is not { } layout)
        {
            var unavailable = StatusFor(installation);
            SetStatus(unavailable);
            return unavailable;
        }

        SetStatus(new ComfyRuntimeStatus(ComfyRuntimeState.Starting, installation, Message: "ComfyUI startet …"));
        lock (_lock)
        {
            _outputTail.Clear();
        }

        try
        {
            await LoadIdleTimeoutAsync(ct).ConfigureAwait(false);
            layout.EnsureWorkFolders();
            Directory.CreateDirectory(layout.ModelsRoot);
            layout.ClearTransient(); // Reste eines Absturzes
            var port = _freePort();
            var startInfo = BuildStartInfo(installation, port);
            var http = new HttpClient(_handlerFactory(), disposeHandler: true)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
                Timeout = TimeSpan.FromSeconds(5),
            };
            var process = _launcher.Start(startInfo, line => OnOutput(line, layout));
            lock (_lock)
            {
                _process = process;
                _http = http;
                _layout = layout;
            }

            _logger.LogInformation("KI-Bearbeitung: ComfyUI {Version} wird gestartet", installation.Version);
            var stats = await WaitUntilReadyAsync(process, http, ct).ConfigureAwait(false);
            var problem = CheckStats(stats, installation);
            if (problem is not null)
            {
                await StopCoreAsync(reason: null).ConfigureAwait(false);
                var failed = new ComfyRuntimeStatus(ComfyRuntimeState.Failed, installation, stats.Device, Message: problem);
                SetStatus(failed);
                _logger.LogWarning("KI-Bearbeitung: Start abgelehnt — {Problem}", problem);
                return failed;
            }

            NotifyActivity();
            var ready = new ComfyRuntimeStatus(ComfyRuntimeState.Ready, installation, stats.Device, _time.GetUtcNow());
            SetStatus(ready);
            _logger.LogInformation("KI-Bearbeitung: ComfyUI bereit ({Device}, {Vram} GB)", stats.Device?.Name,
                stats.Device is { } d ? Math.Round(d.VramTotalBytes / 1024d / 1024 / 1024, 1) : 0);
            return ready;
        }
        catch (OperationCanceledException)
        {
            await StopCoreAsync(reason: null).ConfigureAwait(false);
            var stopped = new ComfyRuntimeStatus(ComfyRuntimeState.Stopped, installation, Message: "Start abgebrochen.");
            SetStatus(stopped);
            return stopped;
        }
        catch (Exception ex) when (ex is ComfyStartException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var tail = OutputTail();
            _logger.LogWarning("KI-Bearbeitung: Start fehlgeschlagen ({Type}): {Message}\n{Tail}", ex.GetType().Name,
                Redact(ex.Message, layout), tail);
            await StopCoreAsync(reason: null).ConfigureAwait(false);
            var failed = new ComfyRuntimeStatus(ComfyRuntimeState.Failed, installation,
                Message: ex is ComfyStartException ? ex.Message : $"ComfyUI ließ sich nicht starten ({ex.GetType().Name}).");
            SetStatus(failed);
            return failed;
        }
    }

    /// <summary>Leerlauf-Zeit aus dem Katalog — erst hier, weil der Katalog beim Sitzungsstart noch migriert wird.</summary>
    private async Task LoadIdleTimeoutAsync(CancellationToken ct)
    {
        if (_loadIdleMinutes is null)
        {
            return;
        }

        try
        {
            IdleTimeout = TimeSpan.FromMinutes(await _loadIdleMinutes(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("KI-Bearbeitung: Leerlauf-Zeit nicht lesbar ({Type}) — es gilt {Minutes} min", ex.GetType().Name, IdleTimeout.TotalMinutes);
        }
    }

    private ProcessStartInfo BuildStartInfo(ComfyInstallation installation, int port)
    {
        var layout = installation.Layout!;
        var info = new ProcessStartInfo(layout.PythonExe) { WorkingDirectory = layout.WorkRoot };
        foreach (var arg in BuildArguments(installation, port))
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in BuildEnvironment(layout))
        {
            if (value is null)
            {
                info.Environment.Remove(key);
            }
            else
            {
                info.Environment[key] = value;
            }
        }

        return info;
    }

    /// <summary>
    /// Startoptionen, geprüft an ComfyUI 0.43.0 (<c>comfy/cli_args.py</c>): nur 127.0.0.1, kein Browser, offline (keine
    /// Partner-/API-Knoten, Oberfläche ohne Internet), keine Erweiterungen außer den freigegebenen, kein Manager (nur mit
    /// <c>--enable-manager</c>), alle Ordner im Container. Nicht <c>--windows-standalone-build</c> (öffnet den Browser).
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(ComfyInstallation installation, int port)
    {
        var layout = installation.Layout ?? throw new InvalidOperationException("Keine Installation.");
        var args = new List<string>
        {
            "-s", layout.MainPy,
            "--listen", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--disable-auto-launch",
            "--offline",
            "--disable-all-custom-nodes",
        };
        if (installation.AllowedExtensions.Count > 0)
        {
            args.Add("--whitelist-custom-nodes");
            args.AddRange(installation.AllowedExtensions);
        }

        args.AddRange(
        [
            "--models-directory", layout.ModelsRoot,
            "--input-directory", layout.InputDir,
            "--output-directory", layout.OutputDir,
            "--temp-directory", layout.TempDir,
            "--user-directory", layout.UserDir,
            "--previews-directory", layout.PreviewsDir,
        ]);
        return args;
    }

    /// <summary>Umgebung nur für diesen Prozess: Caches in den Container, offline, kein Nutzer-Python (ADR-0076 Punkt 3).</summary>
    internal static IReadOnlyList<(string Key, string? Value)> BuildEnvironment(AiEditLayout layout)
    {
        var cache = layout.CacheRoot;
        var tmp = Path.Combine(cache, "tmp");
        return
        [
            ("TEMP", tmp),
            ("TMP", tmp),
            ("HF_HOME", Path.Combine(cache, "huggingface")),
            ("TORCH_HOME", Path.Combine(cache, "torch")),
            ("CUDA_CACHE_PATH", Path.Combine(cache, "cuda")),
            ("PIP_CACHE_DIR", Path.Combine(cache, "pip")),
            ("XDG_CACHE_HOME", cache),
            ("MPLCONFIGDIR", Path.Combine(cache, "matplotlib")),
            ("TRITON_CACHE_DIR", Path.Combine(cache, "triton")),
            ("HF_HUB_OFFLINE", "1"),
            ("TRANSFORMERS_OFFLINE", "1"),
            ("HF_HUB_DISABLE_TELEMETRY", "1"),
            ("DO_NOT_TRACK", "1"),
            ("PYTHONNOUSERSITE", "1"),
            ("PYTHONUNBUFFERED", "1"),
            ("PYTHONIOENCODING", "utf-8"),
            ("PYTHONPATH", null),
            ("PYTHONHOME", null),
            ("PYTHONSTARTUP", null),
        ];
    }

    private async Task<SystemStats> WaitUntilReadyAsync(IComfyProcess process, HttpClient http, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _options.StartTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new ComfyStartException($"ComfyUI wurde beim Start beendet (Code {process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}). Details im Protokoll.");
            }

            try
            {
                using var response = await http.GetAsync("system_stats", ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return ParseStats(json);
                }
            }
            catch (HttpRequestException)
            {
                // Server lauscht noch nicht
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Zeitgrenze der einzelnen Anfrage
            }

            if (_time.GetUtcNow() >= deadline)
            {
                throw new ComfyStartException("ComfyUI hat nicht rechtzeitig geantwortet. Details im Protokoll.");
            }

            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    internal static SystemStats ParseStats(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? version = null;
            if (root.TryGetProperty("system", out var system) && system.TryGetProperty("comfyui_version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                version = v.GetString();
            }

            ComfyDevice? device = null;
            if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array && devices.GetArrayLength() > 0)
            {
                var first = devices[0];
                var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "?" : "?";
                var type = first.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "?" : "?";
                var vram = first.TryGetProperty("vram_total", out var vt) && vt.TryGetInt64(out var bytes) ? bytes : 0;
                device = new ComfyDevice(CleanDeviceName(name), type, vram);
            }

            return new SystemStats(version, device);
        }
        catch (JsonException ex)
        {
            throw new ComfyStartException("ComfyUI antwortet unerwartet.", ex);
        }
    }

    /// <summary>Läuft wirklich die erkannte Version, und rechnet sie auf einer CUDA-Grafikkarte (ADR-0076 Punkt 10)?</summary>
    internal static string? CheckStats(SystemStats stats, ComfyInstallation installation)
    {
        if (!string.Equals(stats.Version, installation.Version, StringComparison.Ordinal))
        {
            return $"ComfyUI meldet Version {stats.Version ?? "?"} statt {installation.Version} — nicht gestartet.";
        }

        if (stats.Device is not { Type: "cuda" })
        {
            return "Keine CUDA-Grafikkarte erkannt — die KI-Bearbeitung rechnet nicht auf dem Prozessor.";
        }

        return null;
    }

    private async Task StopCoreAsync(string? reason)
    {
        IComfyProcess? process;
        HttpClient? http;
        AiEditLayout? layout;
        ComfyInstallation installation;
        lock (_lock)
        {
            process = _process;
            http = _http;
            layout = _layout;
            installation = _status.Installation;
        }

        if (process is null && layout is null)
        {
            if (reason is not null && Status.State != ComfyRuntimeState.Unavailable)
            {
                SetStatus(new ComfyRuntimeStatus(ComfyRuntimeState.Stopped, installation));
            }

            return;
        }

        SetStatus(new ComfyRuntimeStatus(ComfyRuntimeState.Stopping, installation, Message: "ComfyUI wird beendet …"));
        await CleanUpProcessAsync(interrupt: true).ConfigureAwait(false);
        if (reason is not null)
        {
            _logger.LogInformation("KI-Bearbeitung: ComfyUI beendet ({Reason})", reason);
            SetStatus(StatusFor(ComfyInstallation.Detect(_containerRoot())) is { State: ComfyRuntimeState.Unavailable } unavailable
                ? unavailable
                : new ComfyRuntimeStatus(ComfyRuntimeState.Stopped, installation, Message: $"Beendet: {reason}."));
        }
    }

    /// <summary>Beendet einen vorhandenen Prozess und leert die Arbeitsordner; setzt keinen Status.</summary>
    private async Task CleanUpProcessAsync(bool interrupt)
    {
        IComfyProcess? process;
        HttpClient? http;
        AiEditLayout? layout;
        lock (_lock)
        {
            process = _process;
            http = _http;
            layout = _layout;
            _process = null;
            _http = null;
            _layout = null;
        }

        if (process is not null)
        {
            if (interrupt && http is not null && !process.HasExited)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using var response = await http.PostAsync("interrupt", content: null, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Beenden folgt ohnehin
                }
            }

            process.KillTree();
            try
            {
                using var cts = new CancellationTokenSource(_options.StopTimeout);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("KI-Bearbeitung: ComfyUI endete nicht innerhalb von {Seconds} s", _options.StopTimeout.TotalSeconds);
            }

            process.Dispose();
        }

        http?.Dispose();
        if (layout is not null)
        {
            var left = layout.ClearTransient();
            if (left > 0)
            {
                // Windows gibt Dateien eines eben beendeten Prozesses manchmal einen Moment später frei
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                left = layout.ClearTransient();
            }

            if (left > 0)
            {
                _logger.LogWarning("KI-Bearbeitung: {Count} Einträge in den Arbeitsordnern ließen sich nicht löschen", left);
            }
        }
    }

    private void CancelStart()
    {
        lock (_lock)
        {
            try
            {
                _startCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Start eben fertig geworden
            }
        }
    }

    private void OnIdleTick()
    {
        if (_disposed)
        {
            return;
        }

        bool exited;
        bool idle;
        lock (_lock)
        {
            if (_status.State != ComfyRuntimeState.Ready)
            {
                return;
            }

            exited = _process is not { HasExited: false };
            idle = _idleTimeout > TimeSpan.Zero
                   && Volatile.Read(ref _leases) == 0
                   && _time.GetUtcNow() - _lastActivity >= _idleTimeout;
        }

        if (exited)
        {
            _ = HandleUnexpectedExitAsync();
        }
        else if (idle)
        {
            _ = StopAsync("Leerlauf");
        }
    }

    private async Task HandleUnexpectedExitAsync()
    {
        _logger.LogWarning("KI-Bearbeitung: ComfyUI wurde unerwartet beendet\n{Tail}", OutputTail());
        var installation = Status.Installation;
        await StopAsync(reason: "unerwartet beendet").ConfigureAwait(false);
        SetStatus(new ComfyRuntimeStatus(ComfyRuntimeState.Failed, installation, Message: "ComfyUI wurde unerwartet beendet. Details im Protokoll."));
    }

    private void OnOutput(string line, AiEditLayout layout)
    {
        var redacted = Redact(line, layout);
        lock (_lock)
        {
            _outputTail.Enqueue(redacted);
            while (_outputTail.Count > OutputTailLines)
            {
                _outputTail.Dequeue();
            }
        }

        _logger.LogDebug("ComfyUI: {Line}", redacted);
    }

    private string OutputTail()
    {
        lock (_lock)
        {
            return string.Join('\n', _outputTail);
        }
    }

    /// <summary>Pfade aus Ausgabezeilen entfernen (ADR-0076 Punkt 3: Protokoll ohne Pfade).</summary>
    internal static string Redact(string line, AiEditLayout? layout)
    {
        line = AnsiPattern().Replace(line, string.Empty);
        if (layout is not null)
        {
            line = line.Replace(layout.Root, ".lumora-ki", StringComparison.OrdinalIgnoreCase)
                .Replace(layout.Root.Replace('\\', '/'), ".lumora-ki", StringComparison.OrdinalIgnoreCase);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            line = line.Replace(profile, "<Pfad>", StringComparison.OrdinalIgnoreCase)
                .Replace(profile.Replace('\\', '/'), "<Pfad>", StringComparison.OrdinalIgnoreCase);
        }

        return PathPattern().Replace(line, "<Pfad>");
    }

    private static ComfyRuntimeStatus StatusFor(ComfyInstallation installation)
        => installation.CanStart
            ? new ComfyRuntimeStatus(ComfyRuntimeState.Stopped, installation)
            : new ComfyRuntimeStatus(ComfyRuntimeState.Unavailable, installation, Message: installation.Problem);

    private void SetStatus(ComfyRuntimeStatus status)
    {
        lock (_lock)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KI-Bearbeitung: Empfänger der Statusänderung fehlgeschlagen");
        }
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // Laufwerkspfade („X:\…“, „X:/…“) und UNC-Pfade; nicht das „p:/“ in „http://“. Ordner vor einem Trenner dürfen
    // Leerzeichen enthalten („C:\Users\Vorname Nachname\…“), der letzte Teil nicht (sonst schluckte es den Satz).
    [GeneratedRegex(@"(?:(?<![A-Za-z])[A-Za-z]:[\\/]|\\\\)(?:[^\\/\r\n""'<>|]*[\\/])*[^\s""'<>|,;)\]]*")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiPattern();

    /// <summary>„cuda:0 NVIDIA GeForce GTX 1080 Ti : cudaMallocAsync“ → „NVIDIA GeForce GTX 1080 Ti“.</summary>
    internal static string CleanDeviceName(string name)
    {
        var cleaned = DevicePrefixPattern().Replace(name, string.Empty);
        var colon = cleaned.LastIndexOf(" : ", StringComparison.Ordinal);
        return (colon > 0 ? cleaned[..colon] : cleaned).Trim();
    }

    [GeneratedRegex(@"^\s*[a-z]+:\d+\s+")]
    private static partial Regex DevicePrefixPattern();

    internal sealed record SystemStats(string? Version, ComfyDevice? Device);

    private sealed class ComfyStartException(string message, Exception? inner = null) : Exception(message, inner);

    private sealed class WorkLease(ComfyRuntime owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._leases);
                owner.NotifyActivity();
            }
        }
    }
}
