using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Background;
using Lumora.Services.Database;
using Lumora.Services.Duplicates;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;

namespace Lumora.Services.Thumbnails;

/// <summary>Fortschritt der Vorschaubilder auf Vorrat (gedrosselt). <paramref name="Message"/> ohne <paramref name="IsRunning"/> = Wartegrund.</summary>
public sealed record ThumbnailStockStatus(bool IsRunning, int Done, int Total, string? Message = null)
{
    public static ThumbnailStockStatus Idle { get; } = new(false, 0, 0);
}

/// <summary>Fortschritt der Vorschaubilder auf Vorrat (Aktivitäts-Anzeige).</summary>
public sealed record ThumbnailStockStatusMessage(ThumbnailStockStatus Status);

/// <summary>Der Vorschaubild-Cache wurde geleert (Einstellungen › Speicher &amp; Wartung) — der Vorrat füllt ihn wieder auf.</summary>
public sealed record ThumbnailCacheClearedMessage;

/// <summary>Ergebnis eines Durchgangs (Tests, Rauchtest, Log).</summary>
/// <param name="Skipped">fehlt, Wurzel nicht erreichbar, nur online, schon erledigt oder kein FFmpeg.</param>
public sealed record ThumbnailStockReport(int Generated, int Skipped, int Failed)
{
    public static ThumbnailStockReport Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// Vorschaubilder auf Vorrat (0.5.40, ADR-0074 Nachtrag 0.5.40): erstellt still nach und nach die Vorschaubilder aller lokalen
/// Fotos und Videos, damit Galerie, Storys, Orte, Alben und App-Verbindung später nicht warten. Eine Aufgabe der Datei-Spur
/// mit dem niedrigsten Vorrang (<see cref="BackgroundJob.ThumbnailStock"/>, nach den Prüfsummen): sie arbeitet nur, wenn keine
/// andere Dateiaufgabe lesen will, und gibt die Spur <b>je Datei</b> ab, sobald eine wichtigere wartet, der Benutzer auf
/// Arbeit wartet (Galerie- und Lightbox-Aufträge, <see cref="IForegroundWork"/>) oder Vorschaubilder der Galerie bzw. der Pflege
/// eingereiht sind. Ruht beim Einlesen, bei Scan-Sperre, beim Katalog-Umzug und bei Pause. Erzeugt wird über
/// <see cref="IThumbnailService.CreateStockAsync"/> — gleicher Renderer, gleiche Cache-Datei, gleicher DB-Status. Fotos zuerst,
/// dann Videos, je nach FileId; Archive nie. Fehlende Dateien, nicht erreichbare Wurzeln und Cloud-Platzhalter
/// (<see cref="ThumbnailService.IsLocallyAvailable"/>) werden ohne Dateizugriff übersprungen — es wird nie etwas
/// heruntergeladen. Fotos gleichzeitig nach Laufwerksart (<see cref="IBackgroundPlanner.ReadParallelism"/>), höchstens
/// <see cref="BackgroundCpu.MaxThreads"/>; Videos einzeln. Schalter je Katalog (<see cref="IThumbnailStockSettingsStore"/>).
/// </summary>
public interface IThumbnailStockService
{
    ThumbnailStockStatus Status { get; }

    /// <summary>Schalter „Vorschaubilder auf Vorrat erstellen“ (Standard an; gilt erst nach <see cref="StartAsync"/> aus dem Katalog).</summary>
    bool IsEnabled { get; }

    /// <summary>Nach der DB-Initialisierung: Schalter lesen, Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Schalter setzen und im Katalog speichern; aus beendet einen laufenden Durchgang nach den laufenden Dateien.</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Stößt einen Durchgang an; kehrt sofort zurück.</summary>
    void RequestPass();

    /// <summary>Ein Durchgang über alle offenen Dateien (öffentlich für Tests und Rauchtest); ausgeschaltet: nichts.</summary>
    Task<ThumbnailStockReport> RunPassAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IThumbnailStockService"/>
public sealed class ThumbnailStockService : IThumbnailStockService, IDisposable
{
    /// <summary>Aufgabe im Hintergrund-Planer.</summary>
    public const BackgroundJob Job = BackgroundJob.ThumbnailStock;

    /// <summary>Wartegrund, solange Galerie oder Pflege Vorschaubilder eingereiht haben.</summary>
    public const string QueueWaitReason = "wartet — zuerst angeforderte Vorschaubilder";

    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryContextFactory _factory;
    private readonly IThumbnailService _thumbnails;
    private readonly ILogger<ThumbnailStockService> _logger;
    private readonly IThumbnailStockSettingsStore? _settings;
    private readonly IBackgroundPlanner? _planner;
    private readonly IScanGate? _scanGate;
    private readonly IVideoThumbnailRenderer? _video;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _statusLock = new();
    private CancellationTokenSource _enabledCts = new(); // abgebrochen, sobald der Schalter ausgeht
    private volatile bool _enabled = ThumbnailStockSettingsStore.DefaultEnabled;
    private int _started;
    private int _runRequested;
    private ThumbnailStockStatus _status = ThumbnailStockStatus.Idle;
    private DateTime _lastStatusUtc;

    /// <summary>In dieser Sitzung als nicht lokal übersprungene Dateien (FileId → Änderungszeit), siehe <see cref="IsLocal"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _notLocal = new();
    private DateTime _notLocalSinceUtc = DateTime.UtcNow;

    /// <param name="settings">Optional (Tests); ohne Store gilt der Standard „an“ und nichts wird gespeichert.</param>
    /// <param name="planner">Optional (Tests); produktiv der gemeinsame Hintergrund-Planer (ADR-0074).</param>
    /// <param name="scanGate">Optional; nur ohne Planer abgefragt (Rückfall wie bei den übrigen Dateidiensten).</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="video">Optional; ohne FFmpeg bleiben Videos außen vor (zählen nicht als offen).</param>
    public ThumbnailStockService(
        ILibraryContextFactory factory,
        IThumbnailService thumbnails,
        ILogger<ThumbnailStockService> logger,
        IThumbnailStockSettingsStore? settings = null,
        IBackgroundPlanner? planner = null,
        IScanGate? scanGate = null,
        IMessenger? messenger = null,
        IVideoThumbnailRenderer? video = null)
    {
        _factory = factory;
        _thumbnails = thumbnails;
        _logger = logger;
        _settings = settings;
        _planner = planner;
        _scanGate = scanGate;
        _video = video;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang — Start, erste Galerie-Seite und die übrigen Dienste gehen vor.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Wie oft geprüft wird, ob Galerie bzw. Pflege ihre Vorschaubilder fertig haben.</summary>
    public TimeSpan QueuePollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Wartezeit, wenn die Katalog-DB gerade gesperrt war (Umzug).</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Höchstens so viele Dateien je Stapel (die Spur wird je Datei abgegeben, wenn jemand Vorrang hat).</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>
    /// So lange überspringen folgende Durchgänge eine als nicht lokal erkannte Dateiversion (Cloud-Platzhalter, gelöscht) ohne
    /// erneutes Abfragen; danach wird wieder nachgesehen (z. B. Ordner inzwischen „immer auf diesem Gerät behalten“).
    /// </summary>
    public TimeSpan NotLocalMemory { get; init; } = TimeSpan.FromMinutes(30);

    public ThumbnailStockStatus Status => Volatile.Read(ref _status);

    public bool IsEnabled => _enabled;

    /// <summary>
    /// Wie viele Vorschaubilder gleichzeitig entstehen: Fotos so viele, wie das Laufwerk gleichzeitig lesen soll (ADR-0074),
    /// höchstens die Kern-Obergrenze der Hintergrund-Aufgaben; Videos einzeln (FFmpeg, ADR-0007).
    /// </summary>
    public static int Parallelism(MediaType mediaType, int readParallelism)
        => mediaType == MediaType.Video ? 1 : Math.Clamp(readParallelism, 1, BackgroundCpu.MaxThreads);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        if (_settings is not null)
        {
            try
            {
                _enabled = await _settings.LoadEnabledAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Vorschaubilder auf Vorrat: Einstellung nicht lesbar — Standard „an“");
            }
        }

        _messenger.Register<ThumbnailStockService, ScanCompletedMessage>(this, static (r, _) => r.RequestPass());
        _messenger.Register<ThumbnailStockService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Added)
            {
                r.RequestPass();
            }
        });
        _messenger.Register<ThumbnailStockService, ThumbnailCacheClearedMessage>(this, static (r, _) => r.RequestPass()); // „Cache leeren“

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

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        // Schalter und Abbruch-Quelle unter derselben Sperre wie in RunPassAsync: ein gerade beginnender Durchgang sieht
        // entweder „aus“ oder bekommt die Quelle, die hier abgebrochen wird
        CancellationTokenSource? old = null;
        lock (_statusLock)
        {
            _enabled = enabled;
            if (!enabled)
            {
                old = _enabledCts;
                _enabledCts = new CancellationTokenSource();
            }
        }

        if (old is not null)
        {
            // ein laufender Durchgang endet nach den Dateien, die gerade entstehen. Synchron abbrechen, damit der verknüpfte Token
            // des Durchgangs schon vor der nächsten Datei abgebrochen ist; nicht entsorgen: verknüpfte Quellen hängen noch daran
            old.Cancel();
            PublishStatus(ThumbnailStockStatus.Idle, force: true);
        }

        _logger.LogInformation("Vorschaubilder auf Vorrat {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
        if (_settings is not null)
        {
            await _settings.SaveEnabledAsync(enabled, ct).ConfigureAwait(false);
        }

        if (enabled && Volatile.Read(ref _started) == 1)
        {
            RequestPass();
        }
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
        catch (OperationCanceledException)
        {
            // Schalter aus, während der Durchgang noch wartete — kein Fehler
        }
        catch (Exception ex) when (ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex))
        {
            _logger.LogInformation("Vorschaubilder auf Vorrat: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", SuspendedRetryDelay.TotalSeconds);
            PublishStatus(new ThumbnailStockStatus(false, 0, 0, "wartet auf den Katalog"), force: true);
            await Task.Delay(SuspendedRetryDelay, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _runRequested, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vorschaubilder auf Vorrat: Durchgang abgebrochen");
            PublishStatus(ThumbnailStockStatus.Idle, force: true);
        }
    }

    public async Task<ThumbnailStockReport> RunPassAsync(CancellationToken ct = default)
    {
        CancellationToken enabledToken;
        lock (_statusLock)
        {
            if (!_enabled)
            {
                return ThumbnailStockReport.Empty;
            }

            enabledToken = _enabledCts.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, enabledToken);
        var token = linked.Token;
        var counts = new Counts();
        var locked = false;
        try
        {
            await _passLock.WaitAsync(token).ConfigureAwait(false);
            locked = true;
            ForgetNotLocalIfStale();
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            using var reader = _factory.OpenSqliteConnection();
            var mediaTypes = _video is { IsAvailable: false } ? new[] { MediaType.Image } : [MediaType.Image, MediaType.Video];
            var total = await Task.Run(() => CountOpen(reader, mediaTypes), token).ConfigureAwait(false);
            if (total == 0)
            {
                return ThumbnailStockReport.Empty;
            }

            _logger.LogInformation("Vorschaubilder auf Vorrat: {Count} Fotos und Videos noch ohne Vorschaubild", total);
            var sw = Stopwatch.StartNew();
            var offlineRoots = await ContentHashBatch.LoadOfflineRootsAsync(_factory, token).ConfigureAwait(false);
            PublishStatus(new ThumbnailStockStatus(true, 0, total), force: true);
            foreach (var mediaType in mediaTypes)
            {
                await RunMediaTypeAsync(reader, mediaType, total, offlineRoots, counts, token).ConfigureAwait(false);
            }

            if (counts.Generated + counts.Failed > 0)
            {
                _logger.LogInformation(
                    "Vorschaubilder auf Vorrat: {Generated} erzeugt, {Skipped} übersprungen (fehlt/nur online/erledigt), {Failed} nicht lesbar in {Sec:N1} s",
                    counts.Generated, counts.Skipped, counts.Failed, sw.Elapsed.TotalSeconds);
            }

            return counts.ToReport();
        }
        catch (OperationCanceledException) when (enabledToken.IsCancellationRequested && !ct.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            _logger.LogInformation("Vorschaubilder auf Vorrat: ausgeschaltet — Durchgang nach {Generated} Vorschaubildern beendet", counts.Generated);
            return counts.ToReport();
        }
        finally
        {
            if (locked)
            {
                if (Status.IsRunning || Status.Message is not null)
                {
                    PublishStatus(ThumbnailStockStatus.Idle, force: true);
                }

                _passLock.Release();
            }
        }
    }

    /// <summary>Alle offenen Dateien einer Medienart, Stapel für Stapel nach FileId.</summary>
    private async Task RunMediaTypeAsync(
        SqliteConnection reader, MediaType mediaType, int total, IReadOnlyList<string> offlineRoots, Counts counts, CancellationToken token)
    {
        var cursor = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            await WaitForThumbnailQueueAsync(counts, total, token).ConfigureAwait(false);
            using var turn = await TurnAsync(token).ConfigureAwait(false);
            var batch = await Task.Run(() => LoadBatch(reader, mediaType, cursor), token).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return;
            }

            if (AnyBusy(batch))
            {
                await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                continue; // denselben Stapel später noch einmal
            }

            var (handled, deferred) = await ProcessBatchAsync(batch, mediaType, offlineRoots, counts, total, token).ConfigureAwait(false);
            if (handled > 0)
            {
                cursor = batch[handled - 1].FileId;
            }

            PublishStatus(new ThumbnailStockStatus(true, Math.Min(total, counts.Done), total));
            if (deferred)
            {
                // Vorschaubild-Dienst pausiert (Katalog-Umzug): Spur abgeben und später dieselbe Datei noch einmal
                turn?.Dispose();
                await Task.Delay(BusyDelay, token).ConfigureAwait(false);
            }

            // handled < batch.Count: Spur abgegeben (Vorrang, Pause, Einlesen) — die Schleife wartet oben erneut
        }
    }

    /// <summary>
    /// Arbeitet einen Stapel mit <see cref="Parallelism"/> Arbeitern der Reihe nach ab. Vor jeder Datei wird geprüft, ob die
    /// Aufgabe weiter darf (<see cref="MayContinue"/>); sonst nimmt kein Arbeiter mehr eine neue Datei. Rückgabe: Anzahl der
    /// erledigten Dateien vom Anfang des Stapels (lückenlos, weil Dateien nur in Reihenfolge vergeben werden; eine wegen Pause
    /// des Vorschaubild-Dienstes zurückgestellte Datei und alles danach zählt nicht) und ob eine zurückgestellt wurde.
    /// </summary>
    private async Task<(int Handled, bool Deferred)> ProcessBatchAsync(
        List<StockRow> batch, MediaType mediaType, IReadOnlyList<string> offlineRoots, Counts counts, int total, CancellationToken token)
    {
        var parallelism = Parallelism(mediaType, _planner?.ReadParallelism(batch[0].Path) ?? 1);
        var gate = new Lock();
        var next = 0;
        var stop = false;
        var firstDeferred = int.MaxValue;

        async Task WorkerAsync()
        {
            while (true)
            {
                int index;
                lock (gate)
                {
                    if (stop || next >= batch.Count)
                    {
                        return;
                    }

                    if (!MayContinue(batch[next].LibraryId, token))
                    {
                        stop = true;
                        return;
                    }

                    index = next++;
                }

                var row = batch[index];
                try
                {
                    if (!IsLocal(row, offlineRoots))
                    {
                        counts.Add(ThumbnailStockOutcome.Skipped); // fehlt, Wurzel offline oder nur online: kein Download
                    }
                    else
                    {
                        var outcome = await _thumbnails.CreateStockAsync(
                            new ThumbnailRequest(row.LibraryId, row.FileId, row.Path, mediaType, row.ModifiedAtUtc), token).ConfigureAwait(false);
                        counts.Add(outcome);
                        if (outcome == ThumbnailStockOutcome.Deferred)
                        {
                            lock (gate)
                            {
                                stop = true; // Dienst pausiert (Katalog-Umzug): Spur abgeben, diese Datei später noch einmal
                                firstDeferred = Math.Min(firstDeferred, index);
                            }
                        }
                    }
                }
                catch
                {
                    lock (gate)
                    {
                        stop = true; // die übrigen Arbeiter nehmen keine neue Datei mehr
                    }

                    throw;
                }

                PublishStatus(new ThumbnailStockStatus(true, Math.Min(total, counts.Done), total));
            }
        }

        var workers = new Task[Math.Min(parallelism, batch.Count)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(WorkerAsync, CancellationToken.None);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return (Math.Min(next, firstDeferred), firstDeferred != int.MaxValue);
    }

    /// <summary>
    /// Liegt die Datei lokal? Nein bei Wurzel offline (ohne Dateizugriff), bei einer in dieser Sitzung schon als nicht lokal
    /// gemerkten Dateiversion (ohne Dateizugriff) und wenn die Attribute fehlen bzw. einen Cloud-Platzhalter zeigen
    /// (<see cref="ThumbnailService.IsLocallyAvailable"/> liest nur Attribute — kein Download). Letzteres wird gemerkt, damit
    /// Durchgänge nach jedem Einlesen nicht jedes Mal alle Platzhalter abfragen.
    /// </summary>
    private bool IsLocal(StockRow row, IReadOnlyList<string> offlineRoots)
    {
        if (ContentHashBatch.IsUnderAny(row.Path, offlineRoots)
            || (_notLocal.TryGetValue(row.FileId, out var modified) && modified == row.ModifiedAtUtc))
        {
            return false;
        }

        if (ThumbnailService.IsLocallyAvailable(row.Path))
        {
            return true;
        }

        _notLocal[row.FileId] = row.ModifiedAtUtc;
        return false;
    }

    /// <summary>Gemerkte „nicht lokal“-Dateien nach <see cref="NotLocalMemory"/> vergessen (z. B. inzwischen auf dem Gerät behalten).</summary>
    private void ForgetNotLocalIfStale()
    {
        var now = DateTime.UtcNow;
        if (now - _notLocalSinceUtc >= NotLocalMemory)
        {
            _notLocal.Clear();
            _notLocalSinceUtc = now;
        }
    }

    /// <summary>
    /// Darf die nächste Datei beginnen? Nicht bei Abbruch/Aus, solange Galerie oder Pflege Vorschaubilder eingereiht haben,
    /// solange der Planer die Aufgabe anhält (Pause, alle pausiert, Einlesen, Katalog-Umzug, Vorrang für Arbeit des Benutzers)
    /// und solange eine wichtigere Dateiaufgabe auf die Spur wartet. Ohne Planer: Scan-Sperre und Katalog-Umzug.
    /// </summary>
    private bool MayContinue(int libraryId, CancellationToken token)
    {
        if (token.IsCancellationRequested || _thumbnails.PendingCount > 0)
        {
            return false;
        }

        if (_planner is not null)
        {
            return _planner.CanRun(Job) && !_planner.HasWaitingAhead(Job);
        }

        return (_factory as IDatabaseAccessGate)?.IsSuspended != true && _scanGate?.IsBusy(libraryId) != true;
    }

    /// <summary>Galerie- und Pflege-Aufträge zuerst: warten, bis die Warteschlange des Vorschaubild-Dienstes leer ist.</summary>
    private async Task WaitForThumbnailQueueAsync(Counts counts, int total, CancellationToken token)
    {
        if (_thumbnails.PendingCount <= 0)
        {
            return;
        }

        PublishStatus(new ThumbnailStockStatus(false, Math.Min(total, counts.Done), total, QueueWaitReason), force: true);
        while (_thumbnails.PendingCount > 0)
        {
            await Task.Delay(QueuePollInterval, token).ConfigureAwait(false);
        }

        PublishStatus(new ThumbnailStockStatus(true, Math.Min(total, counts.Done), total), force: true);
    }

    // ----- Abfragen (rohe SQL über eine eigene Leseverbindung, wie die Prüfsummen) -----

    /// <summary>
    /// Offen = nicht „fehlt“ und kein gültiges Vorschaubild der aktuellen Größe: keine Zeile, „Generated“ für eine andere
    /// Dateiversion, oder Pending/Stale/Failed — außer endgültig gescheitert (<see cref="ThumbnailService.MaxAttempts"/>,
    /// wie die Galerie). Alias f = Files, t = Thumbnails.
    /// </summary>
    private const string OpenFilter =
        "f.Missing = 0 AND (t.FileId IS NULL OR (t.Status = 2 AND t.SourceModifiedAtUtc IS NOT f.ModifiedAtUtc)" +
        " OR (t.Status <> 2 AND NOT (t.Status = 3 AND t.AttemptCount >= $maxAttempts)))";

    private const string ThumbnailJoin = "LEFT JOIN Thumbnails t ON t.FileId = f.Id AND t.Size = $size AND t.Kind = 0";

    private static void AddFilterParameters(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$size", ThumbnailService.Size);
        cmd.Parameters.AddWithValue("$maxAttempts", ThumbnailService.MaxAttempts);
    }

    private static int CountOpen(SqliteConnection connection, IReadOnlyList<MediaType> mediaTypes)
    {
        using var cmd = connection.CreateCommand();
        var types = string.Join(", ", mediaTypes.Select(t => ((int)t).ToString(CultureInfo.InvariantCulture)));
        cmd.CommandText = $"SELECT COUNT(*) FROM Files f {ThumbnailJoin} WHERE f.MediaType IN ({types}) AND {OpenFilter};";
        AddFilterParameters(cmd);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Nächste offene Dateien einer Medienart nach <paramref name="afterFileId"/>.</summary>
    private List<StockRow> LoadBatch(SqliteConnection connection, MediaType mediaType, int afterFileId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.Id, f.LibraryId, d.Path, f.Name, f.ModifiedAtUtc
            FROM Files f
            JOIN Folders d ON d.Id = f.FolderId
            {ThumbnailJoin}
            WHERE f.Id > $after AND f.MediaType = $type AND {OpenFilter}
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$type", (int)mediaType);
        cmd.Parameters.AddWithValue("$take", BatchSize);
        AddFilterParameters(cmd);
        var rows = new List<StockRow>(BatchSize);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new StockRow(
                r.GetInt32(0),
                r.GetInt32(1),
                Path.Combine(r.GetString(2), r.GetString(3)),
                DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).UtcDateTime, DateTimeKind.Utc)));
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
        => _planner is null ? null : await _planner.AcquireAsync(Job, token).ConfigureAwait(false);

    /// <summary>Alte Abfrage ohne Planer (Tests); mit Planer wartet <see cref="TurnAsync"/> schon auf das Einlesen.</summary>
    private bool AnyBusy(IReadOnlyList<StockRow> batch)
        => _planner is null && _scanGate is { } gate && batch.Select(r => r.LibraryId).Distinct().Any(gate.IsBusy);

    private void PublishStatus(ThumbnailStockStatus status, bool force = false)
    {
        lock (_statusLock)
        {
            var now = DateTime.UtcNow;
            Volatile.Write(ref _status, status);
            if (!force && now - _lastStatusUtc < StatusInterval)
            {
                return;
            }

            _lastStatusUtc = now;
        }

        try
        {
            _messenger.Send(new ThumbnailStockStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der {Message} warf eine Ausnahme", nameof(ThumbnailStockStatusMessage));
        }
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _stop.Cancel();
        _stop.Dispose();
        _runSignal.Dispose();
        lock (_statusLock)
        {
            _enabledCts.Dispose();
        }
    }

    private readonly record struct StockRow(int FileId, int LibraryId, string Path, DateTime ModifiedAtUtc);

    /// <summary>Zähler eines Durchgangs (mehrere Arbeiter).</summary>
    private sealed class Counts
    {
        private int _generated;
        private int _skipped;
        private int _failed;

        public int Generated => Volatile.Read(ref _generated);

        public int Skipped => Volatile.Read(ref _skipped);

        public int Failed => Volatile.Read(ref _failed);

        public int Done => Generated + Skipped + Failed;

        public void Add(ThumbnailStockOutcome outcome)
        {
            switch (outcome)
            {
                case ThumbnailStockOutcome.Generated:
                    Interlocked.Increment(ref _generated);
                    break;
                case ThumbnailStockOutcome.Failed:
                    Interlocked.Increment(ref _failed);
                    break;
                case ThumbnailStockOutcome.Deferred:
                    break; // zurückgestellt: dieselbe Datei kommt noch einmal dran
                default:
                    Interlocked.Increment(ref _skipped);
                    break;
            }
        }

        public ThumbnailStockReport ToReport() => new(Generated, Skipped, Failed);
    }
}
