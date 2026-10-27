using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Places.Accuracy;

/// <summary>Zustand der Genauigkeits-Prüfung (Statusleiste).</summary>
public sealed record GeoAccuracyStatus(bool IsRunning, int Done, int Total, string? Message = null)
{
    public static GeoAccuracyStatus Idle { get; } = new(false, 0, 0);
}

/// <summary>Ergebnis eines Durchgangs: gelesen, davon geschätzt, übersprungen, Sprung-Markierungen (neu/aufgehoben).</summary>
public sealed record GeoAccuracyReport(int Read, int Estimated, int Skipped, int JumpsFlagged, int JumpsCleared);

/// <summary>Genauigkeit der Koordinaten im Hintergrund nachtragen (ADR-0062).</summary>
public interface IGeoAccuracyService
{
    GeoAccuracyStatus Status { get; }

    /// <summary>Nach der DB-Initialisierung: Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    void Start();

    /// <summary>Stößt einen Durchgang an; kehrt sofort zurück.</summary>
    void RequestPass();

    /// <summary>Ein Durchgang über alle noch nicht bewerteten Dateien plus Sprung-Regel (öffentlich für Tests und Rauchtest).</summary>
    Task<GeoAccuracyReport> RunPassAsync(CancellationToken ct = default);
}

/// <summary>
/// Genauigkeit der Koordinaten (ADR-0062) — nach dem Muster des Orte-Nachtrags (ADR-0050): liest für alle Dateien mit
/// gelesenem Ort (<c>GeoSource IS NOT NULL</c>) und <c>GeoAccuracy IS NULL</c> in Stapeln die GPS-Angaben
/// (<see cref="ExifGpsDetailsReader"/>: Höhe, GPS-Zeit, Verfahren, Fehlerradius, <c>OffsetTimeOriginal</c>, Kamera),
/// bewertet sie (<see cref="GeoAccuracyRules"/>) und schreibt eine Transaktion je Stapel — nur für die gelesene
/// Dateiversion. Danach je geänderter Bibliothek die Sprung-Regel (<see cref="GeoJumpDetector"/>). Videos, XMP-
/// Begleitdateien und in Lumora gesetzte Orte gelten als „nicht bewertbar“ (0), ihre Kamera/Zeit-Angaben werden trotzdem
/// gelesen (Zeitabgleich). Ruht bei Scan-Sperre und Umzug; fehlende/Cloud-Dateien bleiben offen.
/// </summary>
public sealed class GeoAccuracyService : IGeoAccuracyService, IDisposable
{
    public const int BatchSize = 200;

    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<GeoAccuracyService> _logger;
    private readonly IScanGate? _scanGate;
    private readonly IMessenger _messenger;
    private readonly Func<string, GpsExifDetails?> _readDetails;
    private readonly Background.IBackgroundPlanner? _planner;
    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _started;
    private int _runRequested;
    private int _disposed;
    private GeoAccuracyStatus _status = GeoAccuracyStatus.Idle;
    private DateTime _lastStatusUtc;

    /// <param name="readDetails">Optional (Tests); produktiv der gemeinsame Lesedurchgang (<paramref name="probe"/>,
    /// ADR-0074) bzw. <see cref="ExifGpsDetailsReader.Read(string)"/>.</param>
    public GeoAccuracyService(
        ILibraryContextFactory factory,
        ILogger<GeoAccuracyService> logger,
        IScanGate? scanGate = null,
        IMessenger? messenger = null,
        Func<string, GpsExifDetails?>? readDetails = null,
        IFileMetadataProbe? probe = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _factory = factory;
        _logger = logger;
        _scanGate = scanGate;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _readDetails = readDetails ?? (probe is null ? ExifGpsDetailsReader.Read : path => probe.Read(path).GpsDetails);
        _planner = planner;
    }

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang (Orte und Aufnahmedatum gehen vor).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(40);

    public GeoAccuracyStatus Status => Volatile.Read(ref _status);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<GeoAccuracyService, ScanCompletedMessage>(this, static (r, _) => r.RequestPass());
        _messenger.Register<GeoAccuracyService, GeoBackfillStatusMessage>(this, static (r, m) =>
        {
            if (!m.Status.IsRunning && m.Status.Message is null)
            {
                r.RequestPass(); // Orte-Nachtrag fertig → neu gelesene Orte bewerten
            }
        });
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay, token).ConfigureAwait(false);
                RequestPass();
                while (!token.IsCancellationRequested)
                {
                    await _runSignal.WaitAsync(token).ConfigureAwait(false);
                    while (Interlocked.Exchange(ref _runRequested, 0) == 1)
                    {
                        await RunSafelyAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Beenden
            }
        });
    }

    public void RequestPass()
    {
        Interlocked.Exchange(ref _runRequested, 1);
        try
        {
            if (_runSignal.CurrentCount == 0)
            {
                _runSignal.Release();
            }
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // Signal steht schon aus bzw. Dienst beendet.
        }
    }

    private async Task RunSafelyAsync(CancellationToken token)
    {
        try
        {
            await RunPassAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex))
        {
            PublishStatus(new GeoAccuracyStatus(false, 0, 0, "wartet auf den Katalog"), force: true);
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            Interlocked.Exchange(ref _runRequested, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Genauigkeit der Orte: Durchgang abgebrochen");
            PublishStatus(GeoAccuracyStatus.Idle, force: true);
        }
    }

    private readonly record struct Pending(int FileId, int LibraryId, string Path, long ModifiedAt, int MediaType, int GeoSource);

    private readonly record struct Result(int FileId, int LibraryId, long ModifiedAt, GeoAccuracy Accuracy, GeoAccuracyReason Reason, GpsExifDetails? Details);

    public async Task<GeoAccuracyReport> RunPassAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _passLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await RunPassCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _passLock.Release();
        }
    }

    private async Task<GeoAccuracyReport> RunPassCoreAsync(CancellationToken token)
    {
        await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
        var total = await Task.Run(CountPending, token).ConfigureAwait(false);
        var changedLibraries = new HashSet<int>();
        int read = 0, estimated = 0, skipped = 0;
        var sw = Stopwatch.StartNew();
        if (total > 0)
        {
            _logger.LogDebug("Genauigkeit der Orte: {Count} Dateien offen", total);
            var offline = await LoadOfflineRootsAsync(token).ConfigureAwait(false);
            var cursor = 0;
            PublishStatus(new GeoAccuracyStatus(true, 0, total), force: true);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
                    // Hintergrund-Planer (ADR-0074): Datei-Spur für einen Stapel
                    using var turn = _planner is null ? null : await _planner.AcquireAsync(Background.BackgroundJob.GeoAccuracy, token).ConfigureAwait(false);
                    var batch = await Task.Run(() => LoadPending(cursor, BatchSize), token).ConfigureAwait(false);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    if (_planner is null && _scanGate is { } gate && batch.Select(r => r.LibraryId).Distinct().Any(gate.IsBusy))
                    {
                        await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                        continue;
                    }

                    var results = new List<Result>(batch.Count);
                    var readable = new List<Pending>(batch.Count);
                    foreach (var row in batch)
                    {
                        if (row.MediaType != 0)
                        {
                            results.Add(new Result(row.FileId, row.LibraryId, row.ModifiedAt, GeoAccuracy.Unknown, GeoAccuracyReason.None, null));
                            continue;
                        }

                        if (offline.Any(root => row.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) || !ThumbnailService.IsLocallyAvailable(row.Path))
                        {
                            skipped++;
                            continue;
                        }

                        readable.Add(row);
                    }

                    var evaluated = new Result[readable.Count];
                    var parallelism = readable.Count == 0 ? 1 : _planner?.ReadParallelism(readable[0].Path) ?? 1;
                    Parallel.For(0, readable.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = token },
                        i => evaluated[i] = Evaluate(readable[i]));
                    results.AddRange(evaluated);

                    cursor = batch[^1].FileId;
                    if (results.Count > 0)
                    {
                        var written = await Task.Run(() => Write(results), token).ConfigureAwait(false);
                        read += results.Count;
                        estimated += results.Count(r => r.Accuracy == GeoAccuracy.Estimated);
                        foreach (var lib in written)
                        {
                            changedLibraries.Add(lib);
                        }
                    }

                    PublishStatus(new GeoAccuracyStatus(true, Math.Min(total, read + skipped), total));
                }
            }
            finally
            {
                PublishStatus(GeoAccuracyStatus.Idle, force: true);
            }
        }

        var (flagged, cleared) = changedLibraries.Count == 0 ? (0, 0) : await Task.Run(() => ApplyJumpRule(changedLibraries), token).ConfigureAwait(false);
        if (changedLibraries.Count > 0)
        {
            _logger.LogInformation(
                "Genauigkeit der Orte: {Read} Dateien geprüft ({Estimated} geschätzt), {Skipped} übersprungen, Sprung-Regel {Flagged} markiert / {Cleared} aufgehoben in {Sec:N1} s",
                read, estimated, skipped, flagged, cleared, sw.Elapsed.TotalSeconds);
            try
            {
                _messenger.Send(new GeoAccuracyChangedMessage(changedLibraries.Order().ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der GeoAccuracyChangedMessage warf eine Ausnahme");
            }
        }

        return new GeoAccuracyReport(read, estimated, skipped, flagged, cleared);
    }

    /// <summary>Eine Datei bewerten (Bild mit gelesenem Ort).</summary>
    private Result Evaluate(Pending row)
    {
        var details = _readDetails(row.Path);
        if (row.GeoSource != (int)GeoSource.Exif || details is not { } d)
        {
            return new Result(row.FileId, row.LibraryId, row.ModifiedAt, GeoAccuracy.Unknown, GeoAccuracyReason.None, details);
        }

        // Ort aus einer XMP-Begleitdatei (gewinnt beim Lesen, ADR-0050): nicht bewertbar.
        if (XmpGpsSidecar.FindExisting(row.Path) is { } sidecar && XmpGpsSidecar.Read(sidecar) is not null)
        {
            return new Result(row.FileId, row.LibraryId, row.ModifiedAt, GeoAccuracy.Unknown, GeoAccuracyReason.None, details);
        }

        var (accuracy, reason) = GeoAccuracyRules.Classify(d);
        return new Result(row.FileId, row.LibraryId, row.ModifiedAt, accuracy, reason, details);
    }

    private HashSet<int> Write(IReadOnlyList<Result> results)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var tx = _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE Files SET GeoAccuracy = $a, GeoAccuracyReason = $r, GpsTimeUtc = $t, ExifTimeOffset = $o, CameraModel = $c
            WHERE Id = $id AND ModifiedAtUtc = $m AND GeoAccuracy IS NULL;
            """;
        var pA = cmd.Parameters.Add("$a", SqliteType.Integer);
        var pR = cmd.Parameters.Add("$r", SqliteType.Integer);
        var pT = cmd.Parameters.Add("$t", SqliteType.Integer);
        var pO = cmd.Parameters.Add("$o", SqliteType.Integer);
        var pC = cmd.Parameters.Add("$c", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pM = cmd.Parameters.Add("$m", SqliteType.Integer);
        var libraries = new HashSet<int>();
        foreach (var r in results)
        {
            pA.Value = (int)r.Accuracy;
            pR.Value = r.Reason == GeoAccuracyReason.None ? DBNull.Value : (int)r.Reason;
            pT.Value = r.Details?.GpsTimeUtc is { } t ? new DateTimeOffset(t).ToUnixTimeSeconds() : DBNull.Value;
            pO.Value = (object?)r.Details?.OffsetMinutes ?? DBNull.Value;
            pC.Value = (object?)r.Details?.CameraModel ?? DBNull.Value;
            pId.Value = r.FileId;
            pM.Value = r.ModifiedAt;
            if (cmd.ExecuteNonQuery() > 0)
            {
                libraries.Add(r.LibraryId);
            }
        }

        tx.Commit();
        return libraries;
    }

    /// <summary>Sprung-Regel je Bibliothek; liefert (neu markiert, aufgehoben).</summary>
    private (int Flagged, int Cleared) ApplyJumpRule(IReadOnlyCollection<int> libraryIds)
    {
        int flaggedTotal = 0, clearedTotal = 0;
        using var connection = _factory.OpenSqliteConnection();
        foreach (var libraryId in libraryIds)
        {
            var samples = new List<JumpSample>();
            var jumpOnly = new HashSet<int>();
            var exactNow = new HashSet<int>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT Id, CapturedAt, Latitude, Longitude, GeoAccuracy, COALESCE(GeoAccuracyReason, 0), GpsTimeUtc
                    FROM Files
                    WHERE LibraryId = $lib AND Missing = 0 AND GeoSource = 1 AND GeoAccuracy IN (1, 2)
                      AND Latitude IS NOT NULL AND CapturedAt IS NOT NULL;
                    """;
                cmd.Parameters.AddWithValue("$lib", libraryId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var accuracy = reader.GetInt32(4);
                    var reason = (GeoAccuracyReason)reader.GetInt32(5);
                    var onlyJump = accuracy == (int)GeoAccuracy.Estimated && reason == GeoAccuracyReason.ImpossibleJump;
                    var exact = accuracy == (int)GeoAccuracy.Exact || onlyJump;
                    if (onlyJump)
                    {
                        jumpOnly.Add(id);
                    }
                    else if (accuracy == (int)GeoAccuracy.Exact)
                    {
                        exactNow.Add(id);
                    }

                    samples.Add(new JumpSample(id, reader.GetInt64(1), reader.GetDouble(2), reader.GetDouble(3), exact, !reader.IsDBNull(6)));
                }
            }

            var flagged = GeoJumpDetector.Detect(samples);
            var toFlag = flagged.Where(exactNow.Contains).ToList();
            var toClear = jumpOnly.Where(id => !flagged.Contains(id)).ToList();
            if (toFlag.Count == 0 && toClear.Count == 0)
            {
                continue;
            }

            using var tx = _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE Files SET GeoAccuracy = $a, GeoAccuracyReason = $r WHERE Id = $id AND GeoSource = 1;";
            var pA = update.Parameters.Add("$a", SqliteType.Integer);
            var pR = update.Parameters.Add("$r", SqliteType.Integer);
            var pId = update.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in toFlag)
            {
                pA.Value = (int)GeoAccuracy.Estimated;
                pR.Value = (int)GeoAccuracyReason.ImpossibleJump;
                pId.Value = id;
                update.ExecuteNonQuery();
            }

            foreach (var id in toClear)
            {
                pA.Value = (int)GeoAccuracy.Exact;
                pR.Value = DBNull.Value;
                pId.Value = id;
                update.ExecuteNonQuery();
            }

            tx.Commit();
            flaggedTotal += toFlag.Count;
            clearedTotal += toClear.Count;
        }

        return (flaggedTotal, clearedTotal);
    }

    private int CountPending()
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Files WHERE GeoAccuracy IS NULL AND GeoSource IS NOT NULL AND Missing = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<Pending> LoadPending(int afterFileId, int take)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.Id, f.LibraryId, d.Path, f.Name, f.ModifiedAtUtc, f.MediaType, f.GeoSource
            FROM Files f JOIN Folders d ON d.Id = f.FolderId
            WHERE f.Id > $after AND f.GeoAccuracy IS NULL AND f.GeoSource IS NOT NULL AND f.Missing = 0
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$take", take);
        var rows = new List<Pending>(take);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Pending(reader.GetInt32(0), reader.GetInt32(1), Path.Combine(reader.GetString(2), reader.GetString(3)), reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<string>> LoadOfflineRootsAsync(CancellationToken token)
    {
        List<string> roots;
        await using (var ctx = _factory.Create())
        {
            roots = await ctx.LibraryRoots.AsNoTracking().Select(r => r.Path).ToListAsync(token).ConfigureAwait(false);
        }

        var offline = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await RootAvailabilityProbe.IsAvailableAsync(root, token).ConfigureAwait(false))
            {
                offline.Add(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar);
            }
        }

        return offline;
    }

    private async Task WaitWhileSuspendedAsync(CancellationToken token)
    {
        while ((_factory as IDatabaseAccessGate)?.IsSuspended == true)
        {
            await Task.Delay(BusyDelay, token).ConfigureAwait(false);
        }
    }

    private void PublishStatus(GeoAccuracyStatus status, bool force = false)
    {
        var now = DateTime.UtcNow;
        Volatile.Write(ref _status, status);
        if (!force && now - _lastStatusUtc < StatusInterval)
        {
            return;
        }

        _lastStatusUtc = now;
        try
        {
            _messenger.Send(new GeoAccuracyStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der GeoAccuracyStatusMessage warf eine Ausnahme");
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
        _stop.Dispose();
        _runSignal.Dispose();
    }
}
