using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Quality;
using Lumora.Services.Scanning;

namespace Lumora.Services.Stories;

/// <summary>
/// Hintergrund-Erkennung der Storys (ADR-0058) im Muster des Doppelte-Index (ADR-0053): erster Durchgang 20 s nach dem
/// Start (alle Gruppen ganz), danach nach jedem Scan, bei neuen bzw. entfernten Dateien, nach Aufnahmedatum-, Orts- und
/// Gesichts-Änderungen, nach Verknüpfen/Lösen und nach geänderten Einstellungen. Dateibezogene Änderungen rechnen nur die
/// betroffenen Zeiträume (Fenster bis zur nächsten Lücke über Pause/Reise-Lücke), Orte und Personen neu. Ruht, solange
/// eine Bibliothek der Gruppe gescannt wird, die Katalog-DB umzieht oder Aufnahmedaten bzw. Orte nachgetragen werden
/// (sonst entstünden Storys aus vorläufigen Daten). Schreibt in einer Transaktion je Gruppe; Titelbilder wählt danach
/// <see cref="IHighlightSelector"/>.
/// </summary>
public interface IStoryIndexService
{
    StoryIndexStatus Status { get; }

    /// <summary>Nach der DB-Initialisierung: Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    void Start();

    /// <summary>Ganze Gruppe neu berechnen (null = alle Gruppen); kehrt sofort zurück.</summary>
    void RequestFull(int? groupId = null);

    /// <summary>Dateien einer Bibliothek haben sich geändert (neu, Datum, Ort, Gesichter); kehrt sofort zurück.</summary>
    void RequestFiles(int libraryId, IReadOnlyCollection<int> fileIds);

    /// <summary>Alle Gruppen ganz berechnen (öffentlich für Tests, Rauchtest und Messung).</summary>
    Task<StoryIndexReport> RunAllAsync(CancellationToken ct = default);

    /// <summary>Eine Gruppe ganz berechnen.</summary>
    Task<StoryIndexReport> RunFullAsync(int groupId, CancellationToken ct = default);

    /// <summary>Nur die Zeiträume, Orte und Personen der Dateien neu berechnen.</summary>
    Task<StoryIndexReport> RunIncrementalAsync(int groupId, IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>Angestoßene Arbeit jetzt erledigen (Tests).</summary>
    Task<StoryIndexReport> RunPendingAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IStoryIndexService"/>
public sealed partial class StoryIndexService : IStoryIndexService, IDisposable
{
    /// <summary>Mehr geänderte Dateien je Gruppe als das: ganze Gruppe statt Fenster.</summary>
    public const int IncrementalLimit = 20_000;

    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);

    /// <summary>Gleichzeitige Titelbild-Wahlen (lesend, je eine Verbindung).</summary>
    private static readonly int CoverParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<StoryIndexService> _logger;
    private readonly IHighlightSelector _selector;
    private readonly IScanGate? _scanGate;
    private readonly Background.IBackgroundPlanner? _planner;
    private readonly IMessenger _messenger;
    private readonly StoryClock _clock;
    private readonly StoryHomeCache? _homes;
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _pendingLock = new();
    private readonly Dictionary<int, HashSet<int>> _dirtyByLibrary = new();
    private readonly HashSet<int> _fullGroups = new();
    private readonly HashSet<int> _fullLibraries = new();
    private readonly HashSet<int> _personLibraries = new();
    private bool _fullAll;
    private int _started;
    private volatile bool _captureBackfillRunning;
    private volatile bool _geoBackfillRunning;
    private StoryIndexStatus _status = StoryIndexStatus.Idle;

    /// <param name="scanGate">Optional (Tests); produktiv der gemeinsame Singleton.</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="clock">Zeitzone der Tagesabschnitte (Tests: feste Zone).</param>
    /// <param name="homes">Zuhause je Gruppe für die Übersicht (Land der Orte).</param>
    public StoryIndexService(
        ILibraryContextFactory factory,
        ILogger<StoryIndexService> logger,
        IHighlightSelector selector,
        IScanGate? scanGate = null,
        IMessenger? messenger = null,
        StoryClock? clock = null,
        StoryHomeCache? homes = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _planner = planner;
        _homes = homes;
        _factory = factory;
        _logger = logger;
        _selector = selector;
        _scanGate = scanGate;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _clock = clock ?? new StoryClock();
    }

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Sammelzeit nach einer Änderung (viele kleine Stapel → ein Durchgang).</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Wartezeit, wenn die Katalog-DB gerade gesperrt war (Umzug).</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public StoryIndexStatus Status => Volatile.Read(ref _status);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<StoryIndexService, ScanCompletedMessage>(this, static (r, m) => r.RequestFullForLibrary(m.LibraryId));
        _messenger.Register<StoryIndexService, FilesChangedMessage>(this, static (r, m) =>
        {
            switch (m.Kind)
            {
                case FilesChangeKind.Added:
                    r.RequestFiles(m.LibraryId, m.FileIds);
                    break;
                case FilesChangeKind.Removed:
                    r.RequestFullForLibrary(m.LibraryId); // Zeilen sind weg — die Kaskade hat die Einträge schon entfernt
                    break;
            }
        });
        _messenger.Register<StoryIndexService, CaptureDatesChangedMessage>(this, static (r, m) => r.RequestFiles(m.LibraryId, m.FileIds));
        _messenger.Register<StoryIndexService, GeoLocationsChangedMessage>(this, static (r, m) => r.RequestFiles(m.LibraryId, m.FileIds));
        _messenger.Register<StoryIndexService, FacesChangedMessage>(this, static (r, m) => r.RequestFiles(m.LibraryId, m.FileIds));
        _messenger.Register<StoryIndexService, PersonsChangedMessage>(this, static (r, m) => r.RequestPersons(m.LibraryId));
        _messenger.Register<StoryIndexService, LibraryGroupsChangedMessage>(this, static (r, m) =>
        {
            if (m.TermsChanged)
            {
                r.RequestFull();
            }
        });
        _messenger.Register<StoryIndexService, StorySettingsChangedMessage>(this, static (r, _) => r.RequestFull());
        _messenger.Register<StoryIndexService, CaptureDateBackfillStatusMessage>(this, static (r, m) => r.OnBackfill(capture: true, m.Status.IsRunning));
        _messenger.Register<StoryIndexService, GeoBackfillStatusMessage>(this, static (r, m) => r.OnBackfill(capture: false, m.Status.IsRunning));

        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay, token).ConfigureAwait(false);
                RequestFull();
                while (!token.IsCancellationRequested)
                {
                    await _runSignal.WaitAsync(token).ConfigureAwait(false);
                    await Task.Delay(Debounce, token).ConfigureAwait(false);
                    while (HasPending)
                    {
                        if (_captureBackfillRunning || _geoBackfillRunning)
                        {
                            PublishStatus(new StoryIndexStatus(false, "Storys warten auf Aufnahmedaten und Orte"));
                            await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                            continue;
                        }

                        await RunPendingSafelyAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Beenden
            }
            catch (ObjectDisposedException)
            {
                // Beenden
            }
        });
    }

    public void RequestFull(int? groupId = null)
    {
        lock (_pendingLock)
        {
            if (groupId is { } g)
            {
                _fullGroups.Add(g);
            }
            else
            {
                _fullAll = true;
            }
        }

        Signal();
    }

    public void RequestFiles(int libraryId, IReadOnlyCollection<int> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        lock (_pendingLock)
        {
            if (!_dirtyByLibrary.TryGetValue(libraryId, out var set))
            {
                _dirtyByLibrary[libraryId] = set = new HashSet<int>();
            }

            if (set.Count <= IncrementalLimit)
            {
                set.UnionWith(fileIds);
            }
        }

        Signal();
    }

    private void RequestFullForLibrary(int libraryId)
    {
        lock (_pendingLock)
        {
            _fullLibraries.Add(libraryId);
        }

        Signal();
    }

    private void RequestPersons(int libraryId)
    {
        lock (_pendingLock)
        {
            _personLibraries.Add(libraryId);
        }

        Signal();
    }

    private void OnBackfill(bool capture, bool running)
    {
        var wasRunning = capture ? _captureBackfillRunning : _geoBackfillRunning;
        if (capture)
        {
            _captureBackfillRunning = running;
        }
        else
        {
            _geoBackfillRunning = running;
        }

        if (wasRunning && !running)
        {
            Signal();
        }
    }

    private bool HasPending
    {
        get
        {
            lock (_pendingLock)
            {
                return _fullAll || _fullGroups.Count > 0 || _fullLibraries.Count > 0 || _personLibraries.Count > 0 || _dirtyByLibrary.Count > 0;
            }
        }
    }

    private void Signal()
    {
        try
        {
            if (_runSignal.CurrentCount == 0)
            {
                _runSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Ein Signal steht schon aus.
        }
        catch (ObjectDisposedException)
        {
            // Dienst beendet.
        }
    }

    private async Task RunPendingSafelyAsync(CancellationToken token)
    {
        try
        {
            await RunPendingAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex))
        {
            _logger.LogInformation("Storys: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", SuspendedRetryDelay.TotalSeconds);
            RequestFull();
            await Task.Delay(SuspendedRetryDelay, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storys: Durchgang abgebrochen");
            PublishStatus(StoryIndexStatus.Idle);
            await Task.Delay(BusyDelay, token).ConfigureAwait(false);
        }
    }

    public async Task<StoryIndexReport> RunPendingAsync(CancellationToken ct = default)
    {
        bool fullAll;
        HashSet<int> fullGroups, fullLibraries, personLibraries;
        Dictionary<int, HashSet<int>> dirty;
        lock (_pendingLock)
        {
            fullAll = _fullAll;
            fullGroups = new HashSet<int>(_fullGroups);
            fullLibraries = new HashSet<int>(_fullLibraries);
            personLibraries = new HashSet<int>(_personLibraries);
            dirty = _dirtyByLibrary.ToDictionary(kv => kv.Key, kv => kv.Value);
            _fullAll = false;
            _fullGroups.Clear();
            _fullLibraries.Clear();
            _personLibraries.Clear();
            _dirtyByLibrary.Clear();
        }

        var libraryGroups = await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, GroupId FROM Libraries WHERE GroupId <> 0;";
            var map = new Dictionary<int, int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                map[reader.GetInt32(0)] = reader.GetInt32(1);
            }

            return map;
        }, ct).ConfigureAwait(false);

        var full = fullAll ? libraryGroups.Values.ToHashSet() : new HashSet<int>(fullGroups.Where(libraryGroups.ContainsValue));
        full.UnionWith(fullLibraries.Where(libraryGroups.ContainsKey).Select(l => libraryGroups[l]));
        var persons = personLibraries.Where(libraryGroups.ContainsKey).Select(l => libraryGroups[l]).Where(g => !full.Contains(g)).ToHashSet();
        var files = new Dictionary<int, HashSet<int>>();
        foreach (var (libraryId, ids) in dirty)
        {
            if (!libraryGroups.TryGetValue(libraryId, out var g) || full.Contains(g))
            {
                continue;
            }

            if (!files.TryGetValue(g, out var set))
            {
                files[g] = set = new HashSet<int>();
            }

            set.UnionWith(ids);
        }

        foreach (var (g, set) in files.Where(kv => kv.Value.Count > IncrementalLimit).ToList())
        {
            full.Add(g);
            files.Remove(g);
        }

        var report = StoryIndexReport.Empty;
        foreach (var g in full.Order())
        {
            if (IsBusy(g, libraryGroups))
            {
                RequestFull(g);
                continue;
            }

            report = report.Add(await RunFullAsync(g, ct).ConfigureAwait(false));
        }

        foreach (var g in persons.Order())
        {
            if (IsBusy(g, libraryGroups))
            {
                lock (_pendingLock)
                {
                    _personLibraries.UnionWith(libraryGroups.Where(kv => kv.Value == g).Select(kv => kv.Key));
                }

                continue;
            }

            report = report.Add(await RunCoreAsync(g, StoryFamilies.Persons, fileIds: null, ct).ConfigureAwait(false));
        }

        foreach (var (g, ids) in files.OrderBy(kv => kv.Key))
        {
            if (IsBusy(g, libraryGroups))
            {
                lock (_pendingLock)
                {
                    var library = libraryGroups.First(kv => kv.Value == g).Key;
                    if (!_dirtyByLibrary.TryGetValue(library, out var set))
                    {
                        _dirtyByLibrary[library] = set = new HashSet<int>();
                    }

                    set.UnionWith(ids);
                }

                continue;
            }

            report = report.Add(await RunIncrementalAsync(g, ids, ct).ConfigureAwait(false));
        }

        if (HasPending && report.Groups == 0)
        {
            await Task.Delay(BusyDelay, ct).ConfigureAwait(false); // alles belegt: nicht im Kreis drehen
        }

        return report;
    }

    private bool IsBusy(int groupId, Dictionary<int, int> libraryGroups)
        => _planner?.CanRun(Background.BackgroundJob.Stories) == false // pausiert, Einlesen, Katalog-Umzug (ADR-0074)
            || (_scanGate is { } gate && libraryGroups.Where(kv => kv.Value == groupId).Any(kv => gate.IsBusy(kv.Key)));

    public async Task<StoryIndexReport> RunAllAsync(CancellationToken ct = default)
    {
        var groups = await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT GroupId FROM Libraries WHERE GroupId <> 0 ORDER BY GroupId;";
            var list = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetInt32(0));
            }

            return list;
        }, ct).ConfigureAwait(false);

        var report = StoryIndexReport.Empty;
        foreach (var g in groups)
        {
            report = report.Add(await RunFullAsync(g, ct).ConfigureAwait(false));
        }

        return report;
    }

    public Task<StoryIndexReport> RunFullAsync(int groupId, CancellationToken ct = default)
        => RunCoreAsync(groupId, StoryFamilies.All, fileIds: null, ct);

    public Task<StoryIndexReport> RunIncrementalAsync(int groupId, IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        return fileIds.Count == 0 ? Task.FromResult(StoryIndexReport.Empty) : RunCoreAsync(groupId, StoryFamilies.All, fileIds, ct);
    }

    private async Task<StoryIndexReport> RunCoreAsync(int groupId, StoryFamilies families, IReadOnlyCollection<int>? fileIds, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _passLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while ((_factory as IDatabaseAccessGate)?.IsSuspended == true)
            {
                await Task.Delay(BusyDelay, token).ConfigureAwait(false);
            }

            PublishStatus(new StoryIndexStatus(true, "Storys werden berechnet …"));
            var report = await Task.Run(() => Compute(groupId, families, fileIds, token), token).ConfigureAwait(false);
            if (report.Groups > 0)
            {
                var sw = Stopwatch.StartNew();
                await PickCoversAsync(groupId, token).ConfigureAwait(false);
                report = report with { Covers = sw.Elapsed, Total = report.Total + sw.Elapsed };
            }

            var stories = await Task.Run(() => CountStories(), token).ConfigureAwait(false);
            PublishStatus(StoryIndexStatus.Idle with { Stories = stories });
            if (report.Inserted + report.Changed + report.Deleted > 0)
            {
                _logger.LogInformation(
                    "Storys: Gruppe {Group} {Mode}: {Files:N0} Dateien, {Detected:N0} erkannt, {Inserted} neu, {Changed} geändert, {Deleted} entfernt in {Ms:N0} ms (lesen {Read:N0}, erkennen {Detect:N0}, schreiben {Write:N0}, Titelbilder {Covers:N0})",
                    groupId, fileIds is null ? families.ToString() : "inkrementell", report.Files, report.Detected, report.Inserted, report.Changed, report.Deleted,
                    report.Total.TotalMilliseconds, report.Read.TotalMilliseconds, report.Detect.TotalMilliseconds, report.Write.TotalMilliseconds, report.Covers.TotalMilliseconds);
                Send(new StoriesChangedMessage(groupId, StoriesChangeKind.Detected, []));
            }

            return report;
        }
        finally
        {
            _passLock.Release();
        }
    }

    private int CountStories()
    {
        using var connection = _factory.OpenSqliteConnection();
        return (int)StorySql.Scalar(connection, null, "SELECT COUNT(*) FROM Stories WHERE IsHidden = 0;");
    }

    /// <summary>Automatische Titelbilder für Storys ohne (neue bzw. deren Titelbild nicht mehr dazugehört).</summary>
    private async Task PickCoversAsync(int groupId, CancellationToken token)
    {
        var pending = await Task.Run(() =>
        {
            // Alle Kandidaten in einem Zug über den Primärschlüssel-Index (Reihenfolge der Ids ≈ Aufnahme-Reihenfolge).
            using var connection = _factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT si.StoryId, si.FileId FROM Stories s CROSS JOIN StoryItems si ON si.StoryId = s.Id
                WHERE s.GroupId = $g AND s.CoverFileId IS NULL AND s.FileCount > 0 AND si.Origin >= 0
                ORDER BY si.StoryId, si.FileId;
                """;
            cmd.Parameters.AddWithValue("$g", groupId);
            var list = new List<(int Id, List<int> Files)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var storyId = reader.GetInt32(0);
                if (list.Count == 0 || list[^1].Id != storyId)
                {
                    list.Add((storyId, new List<int>()));
                }

                list[^1].Files.Add(reader.GetInt32(1));
            }

            return list;
        }, token).ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return;
        }

        var covers = new System.Collections.Concurrent.ConcurrentBag<(int StoryId, int FileId)>();
        if (_selector is BasicHighlightSelector basic)
        {
            // Ersatz-Auswahl: alle über eine Verbindung (Verbindungen sind nicht gepoolt).
            foreach (var (storyId, fileId) in await Task.Run(() => basic.PickCovers(pending, token), token).ConfigureAwait(false))
            {
                covers.Add((storyId, fileId));
            }
        }
        else
        {
            // Nur lesend und je Story unabhängig: parallel (die erste Erkennung einer großen Bibliothek hat Tausende Storys).
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = CoverParallelism, CancellationToken = token },
                async (story, ct) =>
                {
                    if (await _selector.PickCoverAsync(story.Files, ct).ConfigureAwait(false) is { } cover)
                    {
                        covers.Add((story.Id, cover));
                    }
                }).ConfigureAwait(false);
        }

        await Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, token).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Stories SET CoverFileId = $f, CoverIsManual = 0
                WHERE Id = $s AND CoverFileId IS NULL AND EXISTS (SELECT 1 FROM StoryItems si WHERE si.StoryId = $s AND si.FileId = $f AND si.Origin >= 0);
                """;
            var s = cmd.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Integer);
            var f = cmd.Parameters.Add("$f", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var (storyId, fileId) in covers)
            {
                s.Value = storyId;
                f.Value = fileId;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }, token).ConfigureAwait(false);
    }

    private void PublishStatus(StoryIndexStatus status)
    {
        Volatile.Write(ref _status, status);
        Send(new StoryIndexStatusMessage(status));
    }

    private void Send<TMessage>(TMessage message)
        where TMessage : class
    {
        try
        {
            _messenger.Send(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der {Message} warf eine Ausnahme", typeof(TMessage).Name);
        }
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _stop.Cancel();
        _stop.Dispose();
        _runSignal.Dispose();
    }
}

/// <summary>Welche Story-Familien ein Durchgang neu berechnet.</summary>
[Flags]
internal enum StoryFamilies
{
    Events = 1,
    Places = 2,
    Persons = 4,
    All = Events | Places | Persons,
}
