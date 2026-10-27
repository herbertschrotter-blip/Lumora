using CommunityToolkit.Mvvm.Messaging;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Places;

/// <summary>
/// Nachtragen der Koordinaten im Hintergrund (ADR-0050) — nach demselben Muster wie das Aufnahmedatum (ADR-0046): für
/// neue Dateien nach jedem Scan und einmalig für bestehende Kataloge. Liest alle Dateien mit <c>GeoSource IS NULL</c>
/// (nicht „fehlt“) in Stapeln zu <see cref="BackfillBatchSize"/> nach FileId (<see cref="IGeoLocationReader"/>: XMP-
/// Begleitdatei, sonst EXIF bzw. Video-Metadaten; nur Kopf und Verzeichnisse), sucht den nächsten Ort
/// (<see cref="IReverseGeocoder.Nearest"/>) und schreibt EINE Transaktion je Stapel — nur für die gelesene Dateiversion
/// (<c>ModifiedAtUtc</c>) und nur, solange die Zeile offen ist (ein inzwischen gesetzter Ort bleibt). Ohne Koordinate:
/// Herkunft 0 (wird nicht jede Sitzung neu gelesen).
/// <para>
/// Ruht, solange die Scan-Sperre die Bibliothek belegt oder die Katalog-DB umzieht; fehlende Dateien, OneDrive-Platzhalter
/// und Dateien nicht erreichbarer Wurzeln werden übersprungen und bleiben offen — es wird nie etwas heruntergeladen.
/// </para>
/// </summary>
public sealed partial class GeoLocationService
{
    /// <summary>Dateien je Stapel (eine Transaktion).</summary>
    public const int BackfillBatchSize = 200;

    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ChangedMessageInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _started;
    private int _runRequested;
    private GeoBackfillStatus _status = GeoBackfillStatus.Idle;
    private DateTime _lastStatusUtc;

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang (Start der App, erste Galerie-Seite und das Aufnahmedatum gehen vor).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Wartezeit, wenn die Katalog-DB gerade gesperrt war (Umzug).</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public GeoBackfillStatus BackfillStatus => Volatile.Read(ref _status);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<GeoLocationService, ScanCompletedMessage>(this, static (r, _) => r.RequestBackfill());
        _messenger.Register<GeoLocationService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Added)
            {
                r.RequestBackfill();
            }
        });

        (_geocoder as ReverseGeocoder)?.Warmup();
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay, token).ConfigureAwait(false);
                RequestBackfill();
                while (!token.IsCancellationRequested)
                {
                    await _runSignal.WaitAsync(token).ConfigureAwait(false);
                    while (Interlocked.Exchange(ref _runRequested, 0) == 1)
                    {
                        await RunPassSafelyAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Beenden
            }
        });
    }

    public void RequestBackfill()
    {
        Interlocked.Exchange(ref _runRequested, 1);
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

    private async Task RunPassSafelyAsync(CancellationToken token)
    {
        try
        {
            await RunBackfillPassAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex))
        {
            _logger.LogInformation("Orte nachtragen: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", SuspendedRetryDelay.TotalSeconds);
            PublishStatus(new GeoBackfillStatus(false, 0, 0, Message: "wartet auf den Katalog"), force: true);
            await Task.Delay(SuspendedRetryDelay, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _runRequested, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte nachtragen abgebrochen");
            PublishStatus(GeoBackfillStatus.Idle, force: true);
        }
    }

    private readonly record struct PendingRow(int FileId, int LibraryId, string Path, long ModifiedAt, int MediaType);

    private readonly record struct ReadResult(int FileId, int LibraryId, long ModifiedAt, GeoReading? Value, int? PlaceId);

    public async Task<GeoBackfillReport> RunBackfillPassAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
        var total = await Task.Run(CountPending, token).ConfigureAwait(false);
        if (total == 0)
        {
            if (BackfillStatus != GeoBackfillStatus.Idle)
            {
                PublishStatus(GeoBackfillStatus.Idle, force: true);
            }

            return new GeoBackfillReport(0, 0, 0);
        }

        _logger.LogInformation("Orte: {Count} Dateien noch nicht gelesen — werden im Hintergrund nachgetragen", total);
        var offlineRoots = await LoadOfflineRootsAsync(token).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        var cursor = 0;
        var read = 0;
        var withLocation = 0;
        var skipped = 0;
        var changed = new Dictionary<int, List<int>>();
        var lastChangedMessage = Stopwatch.StartNew();
        PublishStatus(new GeoBackfillStatus(true, 0, total), force: true);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
                // Hintergrund-Planer (ADR-0074): Datei-Spur für einen Stapel
                using var turn = _planner is null ? null : await _planner.AcquireAsync(Background.BackgroundJob.GeoLocations, token).ConfigureAwait(false);
                var batch = await Task.Run(() => LoadPending(cursor, BackfillBatchSize), token).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                if (_planner is null && _scanGate is { } gate && batch.Select(r => r.LibraryId).Distinct().Any(gate.IsBusy))
                {
                    await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                    continue; // denselben Stapel später noch einmal
                }

                var results = new List<ReadResult>(batch.Count);
                var readable = new List<PendingRow>(batch.Count);
                foreach (var row in batch)
                {
                    if (row.MediaType == 2)
                    {
                        results.Add(new ReadResult(row.FileId, row.LibraryId, row.ModifiedAt, null, null)); // Archive: kein Ort
                        continue;
                    }

                    if (IsUnderAny(row.Path, offlineRoots) || !ThumbnailService.IsLocallyAvailable(row.Path))
                    {
                        skipped++; // fehlt oder nur online: in einer späteren Sitzung
                        continue;
                    }

                    readable.Add(row);
                }

                // Lesen parallel je nach Laufwerk (Planer), Ortsnamen danach der Reihe nach
                var values = new GeoReading?[readable.Count];
                var parallelism = readable.Count == 0 ? 1 : _planner?.ReadParallelism(readable[0].Path) ?? 1;
                Parallel.For(0, readable.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = token },
                    i => values[i] = _reader.Read(readable[i].Path));
                for (var i = 0; i < readable.Count; i++)
                {
                    var row = readable[i];
                    var value = values[i];
                    var placeId = value is { } v ? _geocoder.Nearest(v.Coordinate.Latitude, v.Coordinate.Longitude)?.PlaceId : null;
                    results.Add(new ReadResult(row.FileId, row.LibraryId, row.ModifiedAt, value, placeId));
                }

                cursor = batch[^1].FileId;
                if (results.Count > 0)
                {
                    var written = await WriteBackfillAsync(results, token).ConfigureAwait(false);
                    foreach (var r in written.Where(r => r.Value is not null))
                    {
                        AddTo(changed, r.LibraryId, r.FileId);
                    }

                    read += results.Count;
                    withLocation += results.Count(r => r.Value is not null);
                }

                PublishStatus(new GeoBackfillStatus(true, Math.Min(total, read + skipped), total, skipped));
                if (lastChangedMessage.Elapsed >= ChangedMessageInterval && changed.Count > 0)
                {
                    Publish(changed);
                    changed.Clear();
                    lastChangedMessage.Restart();
                }
            }
        }
        finally
        {
            PublishStatus(GeoBackfillStatus.Idle, force: true);
            Publish(changed);
        }

        _logger.LogInformation(
            "Orte nachgetragen: {Read} Dateien gelesen ({WithLocation} mit Koordinaten), {Skipped} übersprungen (fehlt/nur online) in {Sec:N1} s",
            read, withLocation, skipped, sw.Elapsed.TotalSeconds);
        return new GeoBackfillReport(read, withLocation, skipped);
    }

    private async Task WaitWhileSuspendedAsync(CancellationToken token)
    {
        while ((_factory as IDatabaseAccessGate)?.IsSuspended == true)
        {
            await Task.Delay(BusyDelay, token).ConfigureAwait(false);
        }
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
            if (!await Scanning.RootAvailabilityProbe.IsAvailableAsync(root, token).ConfigureAwait(false))
            {
                offline.Add(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar);
            }
        }

        return offline;
    }

    private static bool IsUnderAny(string path, IReadOnlyList<string> roots)
        => roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private int CountPending()
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Files WHERE GeoSource IS NULL AND Missing = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<PendingRow> LoadPending(int afterFileId, int take)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.Id, f.LibraryId, d.Path, f.Name, f.ModifiedAtUtc, f.MediaType
            FROM Files f JOIN Folders d ON d.Id = f.FolderId
            WHERE f.Id > $after AND f.GeoSource IS NULL AND f.Missing = 0
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$take", take);
        var rows = new List<PendingRow>(take);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PendingRow(reader.GetInt32(0), reader.GetInt32(1), Path.Combine(reader.GetString(2), reader.GetString(3)), reader.GetInt64(4), reader.GetInt32(5)));
        }

        return rows;
    }

    private async Task<List<ReadResult>> WriteBackfillAsync(IReadOnlyList<ReadResult> results, CancellationToken token)
    {
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, token).ConfigureAwait(false);
                    using var update = CreateGeoUpdate(connection, tx, " AND ModifiedAtUtc = $modified AND GeoSource IS NULL");
                    var modified = update.Parameters.Add("$modified", Microsoft.Data.Sqlite.SqliteType.Integer);
                    var written = new List<ReadResult>(results.Count);
                    foreach (var r in results)
                    {
                        if (r.Value is { } v)
                        {
                            SetGeoUpdate(update, r.FileId, v.Coordinate.Latitude, v.Coordinate.Longitude, (int)v.Source, r.PlaceId);
                        }
                        else
                        {
                            SetGeoUpdate(update, r.FileId, null, null, (int)GeoSource.None, null);
                        }

                        modified.Value = r.ModifiedAt;
                        if (update.ExecuteNonQuery() > 0)
                        {
                            written.Add(r);
                        }
                    }

                    tx.Commit();
                    return written;
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void PublishStatus(GeoBackfillStatus status, bool force = false)
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
            _messenger.Send(new GeoBackfillStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der GeoBackfillStatusMessage warf eine Ausnahme");
        }
    }

    private void StopBackfill()
    {
        _messenger.UnregisterAll(this);
        _stop.Cancel();
        _stop.Dispose();
        _runSignal.Dispose();
    }
}
