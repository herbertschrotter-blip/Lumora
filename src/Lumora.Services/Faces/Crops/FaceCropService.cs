using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Infrastructure;
using Lumora.Services.Messaging;

namespace Lumora.Services.Faces.Crops;

/// <inheritdoc cref="IFaceCropService" />
public sealed class FaceCropService : IFaceCropService, IDisposable
{
    private const int BatchSize = 48;

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IFaceCropRenderer _renderer;
    private readonly ILogger<FaceCropService> _logger;
    private readonly IMessenger _messenger;
    private readonly TimeSpan _sweepDelay;

    private readonly ConcurrentQueue<(int LibraryId, long FaceId)> _queue = new();
    private readonly ConcurrentDictionary<(int, long), byte> _queued = new();
    private readonly ConcurrentDictionary<long, byte> _failed = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sweepSync = new();
    private readonly HashSet<int> _sweepLibraries = [];
    private Task? _worker;
    private Task? _sweep;
    private int _started;
    private int _disposed;

    public FaceCropService(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IFaceCropRenderer renderer,
        ILogger<FaceCropService> logger,
        IMessenger? messenger = null,
        TimeSpan? sweepDelay = null)
    {
        _factory = factory;
        _paths = paths;
        _renderer = renderer;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _sweepDelay = sweepDelay ?? TimeSpan.FromSeconds(5);
    }

    public event Action<FaceCropReadyEventArgs>? CropReady;

    public string GetCropPath(int libraryId, long faceId)
        => Path.Combine(FolderOf(libraryId), faceId.ToString(CultureInfo.InvariantCulture) + ".jpg");

    private string FolderOf(int libraryId) => Path.Combine(_paths.GetLibraryThumbnailsPath(libraryId), FaceCropDefaults.FolderName);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<FaceCropService, FacesChangedMessage>(this, static (r, m) => r.OnFacesChanged(m.LibraryId, m.FileIds));
        _messenger.Register<FaceCropService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Removed)
            {
                r.ScheduleSweep(m.LibraryId); // Gesichter per Kaskade weg
            }
        });
        // Nach einem Scan sind Wurzeln evtl. wieder erreichbar → fehlgeschlagene Ausschnitte erneut versuchen
        _messenger.Register<FaceCropService, ScanCompletedMessage>(this, static (r, _) => r._failed.Clear());
        EnsureWorker();
    }

    public void Request(int libraryId, IReadOnlyCollection<long> faceIds)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        var added = false;
        foreach (var id in faceIds)
        {
            if (_queued.TryAdd((libraryId, id), 0))
            {
                _queue.Enqueue((libraryId, id));
                added = true;
            }
        }

        if (added)
        {
            EnsureWorker();
            _signal.Release();
        }
    }

    public void Delete(int libraryId, IEnumerable<long> faceIds)
    {
        foreach (var id in faceIds)
        {
            TryDelete(GetCropPath(libraryId, id));
        }
    }

    public void DeleteAll()
    {
        if (!Directory.Exists(_paths.ThumbnailsRoot))
        {
            return;
        }

        foreach (var library in Directory.EnumerateDirectories(_paths.ThumbnailsRoot))
        {
            var folder = Path.Combine(library, FaceCropDefaults.FolderName);
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Gesichts-Ausschnitte konnten nicht gelöscht werden");
            }
        }
    }

    private void EnsureWorker()
    {
        if (_worker is null && Volatile.Read(ref _disposed) == 0)
        {
            lock (_queue)
            {
                _worker ??= Task.Run(WorkerLoopAsync);
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        var stop = _stop.Token;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await _signal.WaitAsync(stop).ConfigureAwait(false);
                var batch = new List<(int LibraryId, long FaceId)>(BatchSize);
                while (batch.Count < BatchSize && _queue.TryDequeue(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    ProcessBatch(batch, stop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Gesichts-Ausschnitte: Stapel fehlgeschlagen");
                }
                finally
                {
                    foreach (var item in batch)
                    {
                        _queued.TryRemove(item, out _);
                    }
                }

                if (!_queue.IsEmpty)
                {
                    _signal.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Beenden
        }
    }

    private void ProcessBatch(List<(int LibraryId, long FaceId)> batch, CancellationToken ct)
    {
        batch = ResolveOwnLibraries(batch);
        var missing = new List<(int LibraryId, long FaceId)>();
        foreach (var (libraryId, faceId) in batch)
        {
            var path = GetCropPath(libraryId, faceId);
            if (File.Exists(path))
            {
                Raise(new FaceCropReadyEventArgs(libraryId, faceId, path));
            }
            else if (!_failed.ContainsKey(faceId))
            {
                missing.Add((libraryId, faceId));
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        var libraryOf = missing.ToDictionary(m => m.FaceId, m => m.LibraryId);
        var sources = new Dictionary<string, List<FaceCropTarget>>(StringComparer.OrdinalIgnoreCase);
        var libraryOfSource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var connection = _factory.OpenSqliteConnection())
        {
            using var cmd = FaceAssignmentStore.Command(connection, null, $"""
                SELECT x.Id, x.BoxX, x.BoxY, x.BoxW, x.BoxH, d.Path, f.Name
                FROM Faces x JOIN Files f ON f.Id = x.FileId JOIN Folders d ON d.Id = f.FolderId
                WHERE x.Id IN ({FaceAssignmentStore.IdList(libraryOf.Keys)});
                """);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var faceId = reader.GetInt64(0);
                var source = Path.Combine(reader.GetString(5), reader.GetString(6));
                var libraryId = libraryOf[faceId];
                if (!sources.TryGetValue(source, out var targets))
                {
                    sources[source] = targets = [];
                    libraryOfSource[source] = libraryId;
                }

                targets.Add(new FaceCropTarget(
                    faceId,
                    new RectangleF(reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4)),
                    GetCropPath(libraryId, faceId)));
            }
        }

        Parallel.ForEach(sources, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, entry =>
        {
            if (Cloud.CloudFiles.IsOnlineOnly(entry.Key))
            {
                return; // Speicherplatz freigegeben (ADR-0077): Original nicht laden; nicht als gescheitert merken — später erneut
            }

            try
            {
                Directory.CreateDirectory(FolderOf(libraryOfSource[entry.Key]));
                _renderer.Render(entry.Key, entry.Value, FaceCropDefaults.Size);
                foreach (var target in entry.Value)
                {
                    if (File.Exists(target.TargetPath))
                    {
                        Raise(new FaceCropReadyEventArgs(libraryOfSource[entry.Key], target.FaceId, target.TargetPath));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Offline-Wurzel, nicht dekodierbar … — in dieser Sitzung nicht erneut versuchen (Pfade nur im Debug-Log).
                _logger.LogDebug(ex, "Gesichts-Ausschnitt nicht erzeugt: {Path}", entry.Key);
                foreach (var target in entry.Value)
                {
                    _failed.TryAdd(target.FaceId, 0);
                }
            }
        });
    }

    private void Raise(FaceCropReadyEventArgs args)
    {
        try
        {
            CropReady?.Invoke(args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger von CropReady warf eine Ausnahme");
        }
    }

    /// <summary>
    /// Jeder Ausschnitt liegt im Cache SEINER Bibliothek (ADR-0048): in der Ansicht „Alle verknüpften“ fordert die
    /// Personen-Seite Gesichter mehrerer Bibliotheken mit der aktiven an — hier wird die Bibliothek der Datei eingesetzt
    /// (Gesichter, die es nicht mehr gibt, behalten die angefragte).
    /// </summary>
    private List<(int LibraryId, long FaceId)> ResolveOwnLibraries(List<(int LibraryId, long FaceId)> batch)
    {
        if (batch.Count == 0)
        {
            return batch;
        }

        var own = new Dictionary<long, int>();
        try
        {
            using var connection = _factory.OpenSqliteConnection();
            using var cmd = FaceAssignmentStore.Command(connection, null, $"""
                SELECT x.Id, f.LibraryId FROM Faces x JOIN Files f ON f.Id = x.FileId
                WHERE x.Id IN ({FaceAssignmentStore.IdList(batch.Select(b => b.FaceId))});
                """);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                own[reader.GetInt64(0)] = reader.GetInt32(1);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Bibliotheken der Gesichter nicht lesbar — Ausschnitte bleiben bei der angefragten Bibliothek");
            return batch;
        }

        return batch.Select(b => (own.TryGetValue(b.FaceId, out var lib) ? lib : b.LibraryId, b.FaceId)).Distinct().ToList();
    }

    /// <summary>Gesichter einer Datei neu erkannt: Box kann sich geändert haben → Ausschnitte verwerfen; gelöschte später wegräumen.</summary>
    private void OnFacesChanged(int libraryId, IReadOnlyList<int> fileIds)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var connection = _factory.OpenSqliteConnection();
                foreach (var chunk in fileIds.Chunk(500))
                {
                    using var cmd = FaceAssignmentStore.Command(connection, null, $"SELECT Id FROM Faces WHERE FileId IN ({FaceAssignmentStore.IdList(chunk)});");
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var faceId = reader.GetInt64(0);
                        _failed.TryRemove(faceId, out _);
                        TryDelete(GetCropPath(libraryId, faceId));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Gesichts-Ausschnitte geänderter Dateien nicht verworfen");
            }
        });
        ScheduleSweep(libraryId);
    }

    /// <summary>Entprellt: Ausschnitte, deren Gesicht es nicht mehr gibt, löschen.</summary>
    private void ScheduleSweep(int libraryId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_sweepSync)
        {
            _sweepLibraries.Add(libraryId);
            if (_sweep is { IsCompleted: false })
            {
                return;
            }

            _sweep = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_sweepDelay, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                int[] libraries;
                lock (_sweepSync)
                {
                    libraries = [.. _sweepLibraries];
                    _sweepLibraries.Clear();
                }

                foreach (var library in libraries)
                {
                    try
                    {
                        Sweep(library);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Abgleich der Gesichts-Ausschnitte fehlgeschlagen");
                    }
                }
            });
        }
    }

    /// <summary>Löscht Ausschnitte ohne Gesichter-Zeile (für Tests öffentlich über <see cref="SweepNow"/>).</summary>
    private int Sweep(int libraryId)
    {
        var folder = FolderOf(libraryId);
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        // Reste abgebrochener Schreibvorgänge (.tmp) — nur ältere, damit kein gerade laufender getroffen wird
        foreach (var tmp in Directory.EnumerateFiles(folder, "*.tmp"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp) > TimeSpan.FromMinutes(5))
            {
                TryDelete(tmp);
            }
        }

        var files = new Dictionary<long, string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.jpg"))
        {
            if (long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                files[id] = path;
            }
        }

        if (files.Count == 0)
        {
            return 0;
        }

        var existing = new HashSet<long>();
        using (var connection = _factory.OpenSqliteConnection())
        {
            foreach (var chunk in files.Keys.Chunk(500))
            {
                using var cmd = FaceAssignmentStore.Command(connection, null, $"SELECT Id FROM Faces WHERE Id IN ({FaceAssignmentStore.IdList(chunk)});");
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetInt64(0));
                }
            }
        }

        var deleted = 0;
        foreach (var (id, path) in files)
        {
            if (!existing.Contains(id) && TryDelete(path))
            {
                deleted++;
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Gesichts-Ausschnitte: {Count} verwaiste Datei(en) gelöscht", deleted);
        }

        return deleted;
    }

    /// <summary>Abgleich sofort (Tests).</summary>
    public int SweepNow(int libraryId) => Sweep(libraryId);

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Gesichts-Ausschnitt nicht löschbar");
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _messenger.UnregisterAll(this);
        _stop.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Beenden
        }

        _stop.Dispose();
    }
}
