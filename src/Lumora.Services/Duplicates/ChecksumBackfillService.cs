using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Background;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Duplicates;

/// <summary>Fortschritt der Prüfsummen im Leerlauf (gedrosselt).</summary>
public sealed record ChecksumBackfillStatus(bool IsRunning, int Done, int Total, string? Message = null)
{
    public static ChecksumBackfillStatus Idle { get; } = new(false, 0, 0);
}

/// <summary>Fortschritt der Prüfsummen im Leerlauf (Aktivitäts-Anzeige).</summary>
public sealed record ChecksumBackfillStatusMessage(ChecksumBackfillStatus Status);

/// <summary>Ergebnis eines Durchgangs (Tests, Log).</summary>
public sealed record ChecksumBackfillReport(int Hashed, int Skipped, int Failed);

/// <summary>
/// Prüfsummen im Leerlauf (Herbert, ADR-0073 Frage 3): SHA-256 für <b>alle</b> Fotos und Videos, nicht nur für Dateien gleicher
/// Größe wie der Doppelten-Index (ADR-0053). Gebraucht für <c>/photos/by-checksum</c> der App-Verbindung und das Wiederfinden
/// einer Datei nach „Neu aufbauen“ (Vertrag Abschnitt 10). Läuft mit dem niedrigsten Vorrang der Datei-Spur
/// (<see cref="BackgroundJob.Checksums"/>, ADR-0074): erst wenn Datum, Orte, Doppelte und Foto-Güte nichts mehr lesen wollen,
/// ruht beim Einlesen, beim Katalog-Umzug, bei Pause und wenn der Benutzer auf Arbeit wartet. Erst Fotos, dann Videos, je
/// nach FileId; ein Stapel hat höchstens <see cref="BatchSize"/> Dateien und <see cref="MaxBatchBytes"/> Bytes (eine Datei
/// immer), damit die Spur auch bei großen Videos bald wieder frei wird. Keine Cloud-Platzhalter, keine Dateien nicht
/// erreichbarer Wurzeln; ein Lesefehler wird für diese Dateiversion gemerkt.
/// </summary>
public interface IChecksumBackfillService
{
    ChecksumBackfillStatus Status { get; }

    /// <summary>Nach der DB-Initialisierung: Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    void Start();

    /// <summary>Stößt einen Durchgang an; kehrt sofort zurück.</summary>
    void RequestPass();

    /// <summary>Ein Durchgang über alle offenen Dateien (öffentlich für Tests und Rauchtest).</summary>
    Task<ChecksumBackfillReport> RunPassAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IChecksumBackfillService"/>
public sealed class ChecksumBackfillService : IChecksumBackfillService, IDisposable
{
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<ChecksumBackfillService> _logger;
    private readonly IScanGate? _scanGate;
    private readonly IBackgroundPlanner? _planner;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _started;
    private int _runRequested;
    private ChecksumBackfillStatus _status = ChecksumBackfillStatus.Idle;
    private DateTime _lastStatusUtc;

    /// <param name="scanGate">Optional (Tests); produktiv der gemeinsame Singleton (nur ohne Planer abgefragt).</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public ChecksumBackfillService(
        ILibraryContextFactory factory,
        ILogger<ChecksumBackfillService> logger,
        IScanGate? scanGate = null,
        IMessenger? messenger = null,
        IBackgroundPlanner? planner = null)
    {
        _factory = factory;
        _logger = logger;
        _scanGate = scanGate;
        _planner = planner;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang (nach dem Doppelten-Index, der 15 s wartet).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Wartezeit, wenn die Katalog-DB gerade gesperrt war (Umzug).</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Höchstens so viele Dateien je Stapel.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Höchstens so viele Bytes je Stapel (mindestens eine Datei) — große Videos halten die Spur nicht lange.</summary>
    public long MaxBatchBytes { get; init; } = 256L * 1024 * 1024;

    public ChecksumBackfillStatus Status => Volatile.Read(ref _status);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<ChecksumBackfillService, ScanCompletedMessage>(this, static (r, _) => r.RequestPass());
        _messenger.Register<ChecksumBackfillService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Added)
            {
                r.RequestPass();
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
                        await RunPassSafelyAsync(token).ConfigureAwait(false);
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
            await RunPassAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex))
        {
            _logger.LogInformation("Prüfsummen: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", SuspendedRetryDelay.TotalSeconds);
            PublishStatus(new ChecksumBackfillStatus(false, 0, 0, "wartet auf den Katalog"), force: true);
            await Task.Delay(SuspendedRetryDelay, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _runRequested, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prüfsummen: Durchgang abgebrochen");
            PublishStatus(ChecksumBackfillStatus.Idle, force: true);
        }
    }

    public async Task<ChecksumBackfillReport> RunPassAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _passLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            using var reader = _factory.OpenSqliteConnection();
            var total = await Task.Run(() => CountOpen(reader), token).ConfigureAwait(false);
            if (total == 0)
            {
                return new ChecksumBackfillReport(0, 0, 0);
            }

            _logger.LogInformation("Prüfsummen: {Count} Fotos und Videos noch ohne Prüfsumme", total);
            var sw = Stopwatch.StartNew();
            var offlineRoots = await ContentHashBatch.LoadOfflineRootsAsync(_factory, token).ConfigureAwait(false);
            int done = 0, skipped = 0, failed = 0;
            PublishStatus(new ChecksumBackfillStatus(true, 0, total), force: true);
            foreach (var mediaType in new[] { MediaType.Image, MediaType.Video })
            {
                var cursor = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
                    using var turn = await TurnAsync(token).ConfigureAwait(false);
                    var batch = await Task.Run(() => LoadBatch(reader, mediaType, cursor), token).ConfigureAwait(false);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    if (AnyBusy(batch.Select(r => r.LibraryId)))
                    {
                        await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                        continue; // denselben Stapel später noch einmal
                    }

                    var readable = new List<ContentHashRow>(batch.Count);
                    foreach (var row in batch)
                    {
                        if (ContentHashBatch.IsUnderAny(row.Path, offlineRoots) || !ThumbnailService.IsLocallyAvailable(row.Path))
                        {
                            skipped++; // fehlt oder nur online: in einer späteren Sitzung
                            continue;
                        }

                        readable.Add(row);
                    }

                    var parallelism = readable.Count == 0 ? 1 : _planner?.ReadParallelism(readable[0].Path) ?? 1;
                    var (results, batchFailed, batchSkipped) = await ContentHashBatch.HashAsync(readable, parallelism, _logger, "Prüfsummen", token).ConfigureAwait(false);
                    failed += batchFailed;
                    skipped += batchSkipped;
                    cursor = batch[^1].FileId;
                    if (results.Count > 0)
                    {
                        var written = await ContentHashBatch.WriteAsync(_factory, results, token).ConfigureAwait(false);
                        done += written.Count(r => r.Hash is not null);
                    }

                    PublishStatus(new ChecksumBackfillStatus(true, Math.Min(total, done + skipped + failed), total));
                }
            }

            PublishStatus(ChecksumBackfillStatus.Idle, force: true);
            if (done + failed > 0)
            {
                _logger.LogInformation(
                    "Prüfsummen: {Hashed} berechnet, {Skipped} übersprungen (fehlt/nur online), {Failed} nicht lesbar in {Sec:N1} s",
                    done, skipped, failed, sw.Elapsed.TotalSeconds);
            }

            return new ChecksumBackfillReport(done, skipped, failed);
        }
        finally
        {
            _passLock.Release();
        }
    }

    private const string OpenFilter = $"f.Missing = 0 AND f.Size > 0 AND {ContentHashBatch.Stale}";

    private static int CountOpen(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM Files f
            LEFT JOIN FileFingerprints h ON h.FileId = f.Id
            WHERE f.MediaType IN (0, 1) AND {OpenFilter};
            """;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Nächste offene Dateien nach <paramref name="afterFileId"/>, gekürzt auf <see cref="MaxBatchBytes"/>.</summary>
    private List<ContentHashRow> LoadBatch(SqliteConnection connection, MediaType mediaType, int afterFileId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.Id, f.LibraryId, d.Path, f.Name, f.Size, f.ModifiedAtUtc
            FROM Files f
            JOIN Folders d ON d.Id = f.FolderId
            LEFT JOIN FileFingerprints h ON h.FileId = f.Id
            WHERE f.Id > $after AND f.MediaType = $type AND {OpenFilter}
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$type", (int)mediaType);
        cmd.Parameters.AddWithValue("$take", BatchSize);
        var rows = ContentHashBatch.ReadRows(cmd, BatchSize);
        long bytes = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            bytes += rows[i].Size;
            if (i > 0 && bytes > MaxBatchBytes)
            {
                rows.RemoveRange(i, rows.Count - i);
                break;
            }
        }

        return rows;
    }

    private async Task WaitWhileSuspendedAsync(CancellationToken token)
    {
        while ((_factory as IDatabaseAccessGate)?.IsSuspended == true)
        {
            await Task.Delay(BusyDelay, token).ConfigureAwait(false);
        }
    }

    /// <summary>Datei-Spur des Hintergrund-Planers für einen Stapel (ADR-0074); ohne Planer null.</summary>
    private async ValueTask<IDisposable?> TurnAsync(CancellationToken token)
        => _planner is null ? null : await _planner.AcquireAsync(BackgroundJob.Checksums, token).ConfigureAwait(false);

    /// <summary>Alte Abfrage ohne Planer (Tests); mit Planer wartet <see cref="TurnAsync"/> schon auf das Einlesen.</summary>
    private bool AnyBusy(IEnumerable<int> libraryIds) => _planner is null && _scanGate is { } gate && libraryIds.Distinct().Any(gate.IsBusy);

    private void PublishStatus(ChecksumBackfillStatus status, bool force = false)
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
            _messenger.Send(new ChecksumBackfillStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der {Message} warf eine Ausnahme", nameof(ChecksumBackfillStatusMessage));
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
