using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Metadata;

/// <summary>
/// Nachtragen des Aufnahmedatums im Hintergrund (ADR-0046) — für neue Dateien nach jedem Scan und einmalig für bestehende
/// Kataloge. Liest alle Dateien mit <c>CapturedAtSource IS NULL</c> (nicht „fehlt“) in Stapeln zu
/// <see cref="BackfillBatchSize"/> nach FileId: Datei lesen (<see cref="ICaptureDateReader"/>, nur Kopf und Verzeichnisse),
/// dann EINE Transaktion je Stapel (Single-Writer, Wiederholung bei gesperrter DB). Geschrieben wird nur für die gelesene
/// Dateiversion (<c>ModifiedAtUtc</c>) und nur, solange die Zeile noch nicht gesetzt ist — ein inzwischen manuell
/// gesetztes Datum bleibt unberührt. Ohne Datum: Herkunft „unbekannt“ (wird nicht jede Sitzung neu gelesen).
/// <para>
/// Ruht, solange ein Scan oder Datei-Batch die Bibliothek belegt (Scan-Sperre) oder die Katalog-DB umzieht (der Umzug
/// belegt alle Bibliotheken und sperrt die Factory); Originale, die fehlen oder nur online liegen (OneDrive-Platzhalter),
/// werden übersprungen und bleiben für eine spätere Sitzung offen — es wird nie etwas heruntergeladen.
/// </para>
/// </summary>
public sealed partial class CaptureDateService
{
    /// <summary>Dateien je Stapel (≈ 0,1–0,5 s Lesen, eine Transaktion).</summary>
    public const int BackfillBatchSize = 200;

    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Zwischenstand für Galerie und Lightbox höchstens so oft (die Galerie lädt bei Sortierung nach Aufnahmedatum neu —
    /// beim ersten Nachtragen eines großen Katalogs soll sie nicht ständig umsortieren); am Ende des Durchgangs immer.
    /// </summary>
    private static readonly TimeSpan ChangedMessageInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _started;
    private int _runRequested;
    private CaptureDateBackfillStatus _status = CaptureDateBackfillStatus.Idle;
    private DateTime _lastStatusUtc;

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang (Start der App und erste Galerie-Seite gehen vor).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Wartezeit nach einem Durchgang, wenn die Katalog-DB gerade gesperrt war (Umzug).</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public CaptureDateBackfillStatus BackfillStatus => Volatile.Read(ref _status);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<CaptureDateService, ScanCompletedMessage>(this, static (r, _) => r.RequestBackfill());
        _messenger.Register<CaptureDateService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Added)
            {
                r.RequestBackfill();
            }
        });

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
        catch (Exception ex) when (IsCatalogUnavailable(ex))
        {
            _logger.LogInformation("Aufnahmedatum nachtragen: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", SuspendedRetryDelay.TotalSeconds);
            PublishStatus(new CaptureDateBackfillStatus(false, 0, 0, Message: "wartet auf den Katalog"), force: true);
            await Task.Delay(SuspendedRetryDelay, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _runRequested, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aufnahmedatum nachtragen abgebrochen");
            PublishStatus(CaptureDateBackfillStatus.Idle, force: true);
        }
    }

    private static bool IsCatalogUnavailable(Exception ex)
        => ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex);

    private readonly record struct PendingRow(int FileId, int LibraryId, string Path, long ModifiedAt);

    private readonly record struct ReadResult(int FileId, int LibraryId, long ModifiedAt, CaptureDateValue? Value);

    public async Task<CaptureDateBackfillReport> RunBackfillPassAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
        var total = await Task.Run(CountPending, token).ConfigureAwait(false);
        if (total == 0)
        {
            if (BackfillStatus != CaptureDateBackfillStatus.Idle)
            {
                PublishStatus(CaptureDateBackfillStatus.Idle, force: true); // z. B. „wartet auf den Katalog“ ausblenden
            }

            return new CaptureDateBackfillReport(0, 0, 0);
        }

        _logger.LogInformation("Aufnahmedatum: {Count} Dateien noch nicht gelesen — werden im Hintergrund nachgetragen", total);
        var offlineRoots = await LoadOfflineRootsAsync(token).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        var cursor = 0;
        var read = 0;
        var withDate = 0;
        var skipped = 0;
        var changed = new Dictionary<int, List<int>>();
        var lastChangedMessage = Stopwatch.StartNew();
        PublishStatus(new CaptureDateBackfillStatus(true, 0, total), force: true);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
                // Hintergrund-Planer (ADR-0074): die Datei-Spur für einen Stapel — wartet beim Einlesen, bei Pause und
                // solange eine wichtigere Aufgabe Dateien liest
                using var turn = _planner is null ? null : await _planner.AcquireAsync(Background.BackgroundJob.CaptureDates, token).ConfigureAwait(false);
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

                var readable = new List<PendingRow>(batch.Count);
                foreach (var row in batch)
                {
                    if (IsUnderAny(row.Path, offlineRoots) || !ThumbnailService.IsLocallyAvailable(row.Path))
                    {
                        skipped++; // fehlt oder nur online: in einer späteren Sitzung
                        continue;
                    }

                    readable.Add(row);
                }

                // Mehrere Dateien gleichzeitig nur auf SSD bzw. Netz (Planer); Festplatte liest eine nach der anderen
                var read1 = new ReadResult[readable.Count];
                var parallelism = readable.Count == 0 ? 1 : _planner?.ReadParallelism(readable[0].Path) ?? 1;
                Parallel.For(0, readable.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = token }, i =>
                {
                    var row = readable[i];
                    read1[i] = new ReadResult(row.FileId, row.LibraryId, row.ModifiedAt, _reader.Read(row.Path));
                });
                var results = read1.ToList();

                cursor = batch[^1].FileId;
                if (results.Count > 0)
                {
                    var written = await WriteBackfillAsync(results, token).ConfigureAwait(false);
                    foreach (var r in written)
                    {
                        if (!changed.TryGetValue(r.LibraryId, out var list))
                        {
                            changed[r.LibraryId] = list = new List<int>();
                        }

                        list.Add(r.FileId);
                    }

                    read += results.Count;
                    withDate += results.Count(r => r.Value is not null);
                }

                PublishStatus(new CaptureDateBackfillStatus(true, Math.Min(total, read + skipped), total, skipped));
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
            PublishStatus(CaptureDateBackfillStatus.Idle, force: true);
            Publish(changed);
        }

        _logger.LogInformation(
            "Aufnahmedatum nachgetragen: {Read} Dateien gelesen ({WithDate} mit Datum), {Skipped} übersprungen (fehlt/nur online) in {Sec:N1} s",
            read, withDate, skipped, sw.Elapsed.TotalSeconds);
        return new CaptureDateBackfillReport(read, withDate, skipped);
    }

    private async Task WaitWhileSuspendedAsync(CancellationToken token)
    {
        while ((_factory as IDatabaseAccessGate)?.IsSuspended == true)
        {
            await Task.Delay(BusyDelay, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wurzeln, die gerade nicht erreichbar sind (Laufwerk/NAS getrennt, Prüfung mit Timeout wie beim Scan) — ihre Dateien
    /// werden in diesem Durchgang ohne Dateizugriff übersprungen (sonst hinge jede Datei am Netzwerk-Timeout).
    /// </summary>
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

        if (offline.Count > 0)
        {
            _logger.LogInformation("Aufnahmedatum: {Count} Wurzel(n) nicht erreichbar — deren Dateien folgen später", offline.Count);
        }

        return offline;
    }

    private static bool IsUnderAny(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int CountPending()
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Files WHERE CapturedAtSource IS NULL AND Missing = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<PendingRow> LoadPending(int afterFileId, int take)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.Id, f.LibraryId, d.Path, f.Name, f.ModifiedAtUtc
            FROM Files f JOIN Folders d ON d.Id = f.FolderId
            WHERE f.Id > $after AND f.CapturedAtSource IS NULL AND f.Missing = 0
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$take", take);
        var rows = new List<PendingRow>(take);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PendingRow(reader.GetInt32(0), reader.GetInt32(1), Path.Combine(reader.GetString(2), reader.GetString(3)), reader.GetInt64(4)));
        }

        return rows;
    }

    /// <summary>Ein Stapel in einer Transaktion; liefert die tatsächlich geschriebenen Zeilen.</summary>
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
                    using var update = CreateUpdateCommand(connection, tx, onlyIfModifiedAt: true);
                    var written = new List<ReadResult>(results.Count);
                    foreach (var r in results)
                    {
                        var values = r.Value is { } v
                            ? new NewValues(new DateTimeOffset(v.Utc).ToUnixTimeSeconds(), (int)v.Source, (int)v.Precision, v.OffsetMinutes)
                            : new NewValues(null, (int)CaptureDateSource.Unknown, (int)CaptureDatePrecision.Exact, null);
                        SetUpdate(update, r.FileId, values);
                        update.Parameters["$modified"].Value = r.ModifiedAt;
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

    private void PublishStatus(CaptureDateBackfillStatus status, bool force = false)
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
            _messenger.Send(new CaptureDateBackfillStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der CaptureDateBackfillStatusMessage warf eine Ausnahme");
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
