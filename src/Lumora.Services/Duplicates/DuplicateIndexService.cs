using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Faces;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;

namespace Lumora.Services.Duplicates;

/// <summary>
/// Hintergrund-Index der Doppelten (ADR-0053): Inhalts-Prüfsummen (SHA-256, nur Dateien gleicher Größe) und
/// Bild-Fingerabdrücke (pHash aus dem Vorschaubild) im Muster des Aufnahmedatums (ADR-0046): nach jedem Scan, bei neuen
/// Dateien und 15 s nach dem Start (bestehende Kataloge einmalig ganz), Stapel mit einer Transaktion (Single-Writer),
/// ruht bei Scan-Sperre und während die Katalog-DB umzieht, liest keine Cloud-Platzhalter (kein Download) und keine
/// Dateien nicht erreichbarer Wurzeln, Fortschritt in der Statusleiste.
/// </summary>
public interface IDuplicateIndexService
{
    DuplicateIndexStatus Status { get; }

    /// <summary>Nach der DB-Initialisierung: Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    void Start();

    /// <summary>Stößt einen Durchgang an; kehrt sofort zurück.</summary>
    void RequestPass();

    /// <summary>Ein Durchgang: erst Prüfsummen, dann Bild-Fingerabdrücke (öffentlich für Tests und Rauchtest).</summary>
    Task<DuplicateIndexReport> RunPassAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IDuplicateIndexService"/>
public sealed partial class DuplicateIndexService : IDuplicateIndexService, IDisposable
{
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryContextFactory _factory;
    private readonly IFaceImageLoader? _imageLoader;
    private readonly IScanGate? _scanGate;
    private readonly Background.IBackgroundPlanner? _planner;
    private readonly ILogger<DuplicateIndexService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, int> _reportedInbox = new();
    private int _started;
    private int _runRequested;
    private DuplicateIndexStatus _status = DuplicateIndexStatus.Idle;
    private DateTime _lastStatusUtc;

    /// <param name="imageLoader">WIC-Lader aus dem Wpf-Projekt (wie die Gesichtserkennung); ohne ihn keine Bild-Fingerabdrücke.</param>
    /// <param name="scanGate">Optional (Tests); produktiv der gemeinsame Singleton.</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public DuplicateIndexService(
        ILibraryContextFactory factory,
        ILogger<DuplicateIndexService> logger,
        IFaceImageLoader? imageLoader = null,
        IScanGate? scanGate = null,
        IMessenger? messenger = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _planner = planner;
        _factory = factory;
        _logger = logger;
        _imageLoader = imageLoader;
        _scanGate = scanGate;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Wartezeit, wenn die Katalog-DB gerade gesperrt war (Umzug).</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Dateien je Prüfsummen-Stapel.</summary>
    public int ContentBatchSize { get; init; } = 100;

    /// <summary>Bilder je Fingerabdruck-Stapel.</summary>
    public int ImageBatchSize { get; init; } = 100;

    public DuplicateIndexStatus Status => Volatile.Read(ref _status);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<DuplicateIndexService, ScanCompletedMessage>(this, static (r, _) => r.RequestPass());
        _messenger.Register<DuplicateIndexService, FilesChangedMessage>(this, static (r, m) =>
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
        catch (Exception ex) when (IsCatalogUnavailable(ex))
        {
            _logger.LogInformation("Doppelte: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", SuspendedRetryDelay.TotalSeconds);
            PublishStatus(new DuplicateIndexStatus(false, string.Empty, 0, 0, "wartet auf den Katalog"), force: true);
            await Task.Delay(SuspendedRetryDelay, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _runRequested, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Doppelte: Durchgang abgebrochen");
            PublishStatus(DuplicateIndexStatus.Idle, force: true);
        }
    }

    private static bool IsCatalogUnavailable(Exception ex)
        => ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex);

    public async Task<DuplicateIndexReport> RunPassAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _passLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var offlineRoots = await LoadOfflineRootsAsync(token).ConfigureAwait(false);
            var touched = new HashSet<int>();
            var content = await RunContentPhaseAsync(offlineRoots, touched, token).ConfigureAwait(false);
            var images = await RunImagePhaseAsync(offlineRoots, touched, token).ConfigureAwait(false);
            PublishStatus(DuplicateIndexStatus.Idle, force: true);

            var report = new DuplicateIndexReport(content.Done, images.Done, content.Skipped + images.Skipped, content.Failed + images.Failed);
            if (report.Hashed + report.Fingerprinted + report.Failed > 0)
            {
                _logger.LogInformation(
                    "Doppelte: {Hashed} Prüfsummen, {Images} Bild-Fingerabdrücke, {Skipped} übersprungen (fehlt/nur online), {Failed} nicht lesbar in {Sec:N1} s",
                    report.Hashed, report.Fingerprinted, report.Skipped, report.Failed, sw.Elapsed.TotalSeconds);
            }

            if (touched.Count > 0)
            {
                Send(new DuplicatesChangedMessage(touched.ToList()));
                await ReportInboxDuplicatesAsync(touched, token).ConfigureAwait(false);
            }

            return report;
        }
        finally
        {
            _passLock.Release();
        }
    }

    /// <summary>
    /// Hinweis „N Dateien im Eingang sind schon in der Bibliothek“ für Bibliotheken mit neuen Prüfsummen — nur, wenn die Zahl
    /// gegenüber dem letzten Hinweis gestiegen ist (kein Nörgeln nach jedem Durchgang).
    /// </summary>
    private async Task ReportInboxDuplicatesAsync(IEnumerable<int> libraryIds, CancellationToken token)
    {
        foreach (var libraryId in libraryIds)
        {
            var count = await Task.Run(
                () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    return DuplicateSql.CountInboxAlreadyInLibrary(connection, [libraryId]);
                },
                token).ConfigureAwait(false);

            int previous;
            lock (_reportedInbox)
            {
                previous = _reportedInbox.GetValueOrDefault(libraryId);
                _reportedInbox[libraryId] = count;
            }

            if (count > previous)
            {
                _logger.LogInformation("Doppelte: {Count} Dateien im Eingang liegen schon in der Bibliothek {LibraryId}", count, libraryId);
                Send(new InboxDuplicatesFoundMessage(libraryId, count));
            }
        }
    }

    private async Task WaitWhileSuspendedAsync(CancellationToken token)
    {
        while ((_factory as IDatabaseAccessGate)?.IsSuspended == true)
        {
            await Task.Delay(BusyDelay, token).ConfigureAwait(false);
        }
    }

    /// <summary>Scan oder Datei-Batch belegt eine Bibliothek des Stapels → später noch einmal.</summary>
    /// <summary>Datei-Spur des Hintergrund-Planers für einen Stapel (ADR-0074); ohne Planer null.</summary>
    private async ValueTask<IDisposable?> TurnAsync(CancellationToken token)
        => _planner is null ? null : await _planner.AcquireAsync(Background.BackgroundJob.Duplicates, token).ConfigureAwait(false);

    /// <summary>Alte Abfrage ohne Planer (Tests); mit Planer wartet <see cref="TurnAsync"/> schon auf das Einlesen.</summary>
    private bool AnyBusy(IEnumerable<int> libraryIds) => _planner is null && _scanGate is { } gate && libraryIds.Distinct().Any(gate.IsBusy);

    /// <summary>Wurzeln, die gerade nicht erreichbar sind — ihre Dateien werden ohne Dateizugriff übersprungen.</summary>
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

    private static bool IsUnderAny(string path, IReadOnlyList<string> roots)
        => roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private void PublishStatus(DuplicateIndexStatus status, bool force = false)
    {
        var now = DateTime.UtcNow;
        Volatile.Write(ref _status, status);
        if (!force && now - _lastStatusUtc < StatusInterval)
        {
            return;
        }

        _lastStatusUtc = now;
        Send(new DuplicateIndexStatusMessage(status));
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
