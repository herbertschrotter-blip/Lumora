using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Scanning;

namespace Lumora.Services.Background;

/// <summary>Hintergrund-Aufgaben, die der Planer kennt (ADR-0074).</summary>
public enum BackgroundJob
{
    CaptureDates,
    GeoLocations,
    GeoAccuracy,
    Duplicates,
    Quality,
    Faces,
    Motifs,
    Stories,
}

/// <summary>Warum eine Aufgabe gerade nicht arbeitet.</summary>
public enum BackgroundWait
{
    None,

    /// <summary>Vom Benutzer pausiert (nur diese Aufgabe).</summary>
    Paused,

    /// <summary>„Alle pausieren“.</summary>
    AllPaused,

    /// <summary>Eine Bibliothek wird eingelesen bzw. ein Datei-Vorgang läuft.</summary>
    Scan,

    /// <summary>Der Katalog zieht gerade um (ADR-0026).</summary>
    CatalogMove,

    /// <summary>Eine wichtigere Aufgabe liest gerade Dateien.</summary>
    Turn,

    /// <summary>Der Benutzer wartet gerade auf eine Arbeit, die Grafikkarte bzw. Prozessor braucht (<see cref="IForegroundWork"/>).</summary>
    Foreground,
}

/// <summary>Zustand einer Aufgabe für die Aktivitäts-Anzeige.</summary>
/// <param name="Reason">Text für „Wartet“ bzw. null.</param>
public sealed record BackgroundJobState(BackgroundJob Job, BackgroundWait Wait, string? Reason);

/// <summary>Der Zustand einer Aufgabe hat sich geändert (Aktivitäts-Anzeige).</summary>
public sealed record BackgroundJobStateMessage(BackgroundJobState State);

/// <summary>
/// Hintergrund-Planer (ADR-0074): statt dass alle Dienste gleichzeitig dieselben Dateien lesen, vergibt er
/// <b>eine Datei-Spur</b> nach Vorrang — erst die Metadaten (Aufnahmedatum, Orte, Genauigkeit; abwechselnd Stapel um Stapel
/// über dieselben Dateien), dann Doppelte, dann Foto-Güte. Gesichter, Motive und Storys brauchen die Spur nicht (Grafikkarte
/// bzw. Katalog), halten aber wie alle beim Einlesen, beim Katalog-Umzug und bei Pause an. Seit 0.5.12 hat Arbeit, auf die
/// der Benutzer wartet, Vorrang (<see cref="IForegroundWork"/>): braucht sie die Grafikkarte, warten Gesichter und Motive;
/// braucht sie den Prozessor, warten alle.
/// </summary>
public interface IBackgroundPlanner
{
    /// <summary>
    /// Wartet, bis <paramref name="job"/> arbeiten darf. Aufgaben der Datei-Spur bekommen dabei die Spur für einen Stapel
    /// (Dispose gibt sie frei); die übrigen nur die Erlaubnis (Dispose tut nichts).
    /// </summary>
    Task<IDisposable> AcquireAsync(BackgroundJob job, CancellationToken ct);

    /// <summary>true = Aufgabe nutzt die Datei-Spur.</summary>
    bool UsesFileLane(BackgroundJob job);

    /// <summary>Darf <paramref name="job"/> gerade arbeiten (nicht pausiert, kein Einlesen, kein Katalog-Umzug, kein Vorrang)? Für
    /// Aufgaben, die liegengebliebene Arbeit selbst später nachholen, statt zu warten.</summary>
    bool CanRun(BackgroundJob job);

    BackgroundJobState GetState(BackgroundJob job);

    bool IsPaused(BackgroundJob job);

    void SetPaused(BackgroundJob job, bool paused);

    bool AllPaused { get; }

    void SetAllPaused(bool paused);

    /// <summary>Wie viele Dateien dieser Ordner gleichzeitig lesen sollte (SSD mehrere, Festplatte eine, Netz zwei).</summary>
    int ReadParallelism(string path);

    /// <summary>Ein Zustand hat sich geändert (Pause, Warten, Spur).</summary>
    event Action? Changed;
}

/// <summary>Standard-Umsetzung von <see cref="IBackgroundPlanner"/>.</summary>
public sealed class BackgroundPlanner : IBackgroundPlanner, IDisposable
{
    /// <summary>Wie oft Wartende Einlesen und Katalog-Umzug neu prüfen.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly IReadOnlyDictionary<BackgroundJob, int> FileLanePriority = new Dictionary<BackgroundJob, int>
    {
        [BackgroundJob.CaptureDates] = 0,
        [BackgroundJob.GeoLocations] = 0,
        [BackgroundJob.GeoAccuracy] = 0,
        [BackgroundJob.Duplicates] = 1,
        [BackgroundJob.Quality] = 2,
    };

    private readonly IScanGate? _scanGate;
    private readonly IDatabaseAccessGate? _catalog;
    private readonly IStorageKindProbe _storage;
    private readonly IMessenger? _messenger;
    private readonly IForegroundWork? _foreground;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<Waiter> _waiters = [];
    private readonly HashSet<BackgroundJob> _paused = [];
    private readonly Dictionary<BackgroundJob, BackgroundJobState> _states = new();
    private readonly ITimer _timer;
    private BackgroundJob? _laneHolder;
    private bool _allPaused;
    private long _sequence;

    public BackgroundPlanner(IScanGate? scanGate = null, IDatabaseAccessGate? catalog = null, IStorageKindProbe? storage = null,
        IMessenger? messenger = null, ILogger<BackgroundPlanner>? logger = null, TimeProvider? time = null, IForegroundWork? foreground = null)
    {
        _scanGate = scanGate;
        _catalog = catalog;
        _storage = storage ?? new StorageKindProbe();
        _messenger = messenger;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _foreground = foreground;
        if (_foreground is not null)
        {
            _foreground.Changed += OnForegroundChanged;
        }

        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Pump(), null, PollInterval, PollInterval);
    }

    public event Action? Changed;

    public bool AllPaused
    {
        get
        {
            lock (_lock)
            {
                return _allPaused;
            }
        }
    }

    public bool UsesFileLane(BackgroundJob job) => FileLanePriority.ContainsKey(job);

    public bool CanRun(BackgroundJob job)
    {
        lock (_lock)
        {
            return !_paused.Contains(job) && Block(job) == BackgroundWait.None;
        }
    }

    /// <summary>
    /// Erlaubnis für Aufgaben außerhalb der Datei-Spur abwarten; true = es wurde gewartet (der Aufrufer setzt dann
    /// seine Zeitmessung zurück, damit Wartezeit nicht als Rechenzeit zählt).
    /// </summary>
    public static async Task<bool> WaitForPermissionAsync(IBackgroundPlanner? planner, BackgroundJob job, CancellationToken ct)
    {
        if (planner is null)
        {
            return false;
        }

        var permission = planner.AcquireAsync(job, ct);
        var waited = !permission.IsCompleted;
        (await permission.ConfigureAwait(false)).Dispose();
        return waited;
    }

    public Task<IDisposable> AcquireAsync(BackgroundJob job, CancellationToken ct)
    {
        var waiter = new Waiter(job, Interlocked.Increment(ref _sequence));
        lock (_lock)
        {
            _waiters.Add(waiter);
        }

        if (ct.CanBeCanceled)
        {
            waiter.Registration = ct.Register(() => Cancel(waiter, ct));
        }

        Pump();
        return waiter.Done.Task;
    }

    public BackgroundJobState GetState(BackgroundJob job)
    {
        lock (_lock)
        {
            return _states.TryGetValue(job, out var s) ? s : new BackgroundJobState(job, BackgroundWait.None, null);
        }
    }

    public bool IsPaused(BackgroundJob job)
    {
        lock (_lock)
        {
            return _paused.Contains(job);
        }
    }

    public void SetPaused(BackgroundJob job, bool paused)
    {
        lock (_lock)
        {
            if (paused ? !_paused.Add(job) : !_paused.Remove(job))
            {
                return;
            }
        }

        _logger.LogInformation("Hintergrund: {Job} {State}", job, paused ? "pausiert" : "fortgesetzt");
        Pump();
        Changed?.Invoke();
    }

    public void SetAllPaused(bool paused)
    {
        lock (_lock)
        {
            if (_allPaused == paused)
            {
                return;
            }

            _allPaused = paused;
        }

        _logger.LogInformation("Hintergrund: alle {State}", paused ? "pausiert" : "fortgesetzt");
        Pump();
        Changed?.Invoke();
    }

    public int ReadParallelism(string path)
    {
        var cores = Environment.ProcessorCount;
        return _storage.GetKind(path) switch
        {
            StorageKind.Ssd => Math.Clamp(cores / 2, 2, 6),
            StorageKind.Network => 2,
            _ => 1, // Festplatte oder unbekannt: ein Lesekopf — paralleles Lesen macht sie nur langsamer
        };
    }

    /// <summary>Vergibt Spur und Erlaubnis neu (bei jedem Wechsel und regelmäßig für Einlesen/Umzug).</summary>
    internal void Pump()
    {
        lock (_lock)
        {
            if (_waiters.Count == 0)
            {
                return; // Takt ohne Wartende: nichts zu tun
            }
        }

        var changed = new List<BackgroundJobState>();
        var granted = new List<Waiter>();
        lock (_lock)
        {
            // Datei-Spur: der Wartende mit höchstem Vorrang, bei gleichem Vorrang der älteste (abwechselnd Stapel um Stapel)
            Waiter? next = null;
            if (_laneHolder is null)
            {
                next = _waiters.Where(w => UsesFileLane(w.Job) && !_paused.Contains(w.Job) && Block(w.Job) == BackgroundWait.None)
                    .OrderBy(w => FileLanePriority[w.Job]).ThenBy(w => w.Sequence).FirstOrDefault();
                _laneHolder = next?.Job; // vor der Schleife, damit Wartende schon sehen, wer dran ist
            }

            foreach (var w in _waiters.ToList())
            {
                BackgroundWait wait;
                var blocker = Block(w.Job);
                if (_paused.Contains(w.Job))
                {
                    wait = BackgroundWait.Paused;
                }
                else if (blocker != BackgroundWait.None)
                {
                    wait = blocker;
                }
                else if (!UsesFileLane(w.Job))
                {
                    wait = BackgroundWait.None;
                }
                else
                {
                    wait = ReferenceEquals(w, next) ? BackgroundWait.None : BackgroundWait.Turn;
                }

                if (wait == BackgroundWait.None)
                {
                    _waiters.Remove(w);
                    granted.Add(w);
                    Track(new BackgroundJobState(w.Job, BackgroundWait.None, null), changed);
                }
                else
                {
                    Track(new BackgroundJobState(w.Job, wait, ReasonFor(wait)), changed);
                }
            }
        }

        foreach (var w in granted)
        {
            w.Registration.Dispose();
            w.Done.TrySetResult(UsesFileLane(w.Job) ? new Lease(this, w.Job) : NoLease.Instance);
        }

        Publish(changed);
    }

    private BackgroundWait Block(BackgroundJob job)
        => _allPaused ? BackgroundWait.AllPaused
            : _catalog?.IsSuspended == true ? BackgroundWait.CatalogMove
            : _scanGate?.IsAnyBusy == true ? BackgroundWait.Scan
            : YieldsToForeground(job) ? BackgroundWait.Foreground
            : BackgroundWait.None;

    /// <summary>Prozessor gebraucht → alle warten; nur Grafikkarte → Gesichter und Motive.</summary>
    private bool YieldsToForeground(BackgroundJob job)
    {
        var need = _foreground?.Need ?? ForegroundNeed.None;
        return need.HasFlag(ForegroundNeed.Cpu) || (need.HasFlag(ForegroundNeed.Gpu) && job is BackgroundJob.Faces or BackgroundJob.Motifs);
    }

    private void OnForegroundChanged()
    {
        _logger.LogInformation("Hintergrund: Vorrang {State}", _foreground?.What is { } what ? $"für „{what}“" : "beendet");
        Pump();
        Changed?.Invoke(); // auch ohne Wartende (Storys fragen nur CanRun)
    }

    private string? ReasonFor(BackgroundWait wait) => wait switch
    {
        BackgroundWait.Paused => "pausiert",
        BackgroundWait.AllPaused => "alle Hintergrund-Aufgaben pausiert",
        BackgroundWait.Scan => "wartet auf das Einlesen",
        BackgroundWait.CatalogMove => "wartet auf den Katalog-Umzug",
        BackgroundWait.Foreground => _foreground?.What is { } what ? $"wartet — Vorrang für „{what}“" : "wartet — Vorrang für deine Arbeit",
        BackgroundWait.Turn => _laneHolder is { } holder ? $"wartet — zuerst {JobNames.Of(holder)}" : "wartet auf die Reihe",
        _ => null,
    };

    private void Track(BackgroundJobState state, List<BackgroundJobState> changed)
    {
        if (!_states.TryGetValue(state.Job, out var old) || old != state)
        {
            _states[state.Job] = state;
            changed.Add(state);
        }
    }

    private void Publish(List<BackgroundJobState> changed)
    {
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var state in changed)
        {
            _messenger?.Send(new BackgroundJobStateMessage(state));
        }

        Changed?.Invoke();
    }

    private void Release(BackgroundJob job)
    {
        lock (_lock)
        {
            if (_laneHolder == job)
            {
                _laneHolder = null;
            }
        }

        Pump();
    }

    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        bool removed;
        lock (_lock)
        {
            removed = _waiters.Remove(waiter);
        }

        if (removed)
        {
            lock (_lock)
            {
                // Kein Wartender dieser Aufgabe mehr: alten Wartegrund nicht stehen lassen
                if (!_waiters.Any(w => w.Job == waiter.Job) && _states.TryGetValue(waiter.Job, out var old) && old.Wait != BackgroundWait.None)
                {
                    _states[waiter.Job] = new BackgroundJobState(waiter.Job, BackgroundWait.None, null);
                }
            }

            waiter.Done.TrySetCanceled(ct);
            Pump();
        }
    }

    public void Dispose()
    {
        if (_foreground is not null)
        {
            _foreground.Changed -= OnForegroundChanged;
        }

        _timer.Dispose();
    }

    private sealed class Waiter(BackgroundJob job, long sequence)
    {
        public BackgroundJob Job { get; } = job;

        public long Sequence { get; } = sequence;

        public TaskCompletionSource<IDisposable> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Lease(BackgroundPlanner owner, BackgroundJob job) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(job);
            }
        }
    }

    private sealed class NoLease : IDisposable
    {
        public static NoLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>Namen der Aufgaben wie in der Aktivitäts-Anzeige.</summary>
public static class JobNames
{
    public static string Of(BackgroundJob job) => job switch
    {
        BackgroundJob.CaptureDates => "Aufnahmedatum",
        BackgroundJob.GeoLocations => "Orte nachtragen",
        BackgroundJob.GeoAccuracy => "Orte prüfen",
        BackgroundJob.Duplicates => "Doppelte",
        BackgroundJob.Quality => "Foto-Güte",
        BackgroundJob.Faces => "Gesichter",
        BackgroundJob.Motifs => "Motive",
        BackgroundJob.Stories => "Storys",
        _ => job.ToString(),
    };
}
