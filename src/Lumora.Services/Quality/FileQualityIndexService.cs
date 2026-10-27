using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Faces;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Motifs;
using Lumora.Services.Scanning;

namespace Lumora.Services.Quality;

/// <summary>
/// Hintergrund-Index der Foto-Güte (ADR-0057) im Muster der Bild-Fingerabdrücke (ADR-0053): nach jedem Scan, bei neuen
/// Dateien, beim Wechsel des Bereichs und 20 s nach dem Start; nur Dateien des aktuellen Bereichs (aktive Bibliothek bzw.
/// „Alle verknüpften“, ADR-0048). Erst Schärfe/Belichtung aus dem Vorschaubild (Bilder ohne Vorschaubild aus dem lokalen
/// Original, Videos nur über ihr Vorschaubild), dann die Bildwirkung aus vorhandenen Motiv-Vektoren. Stapel mit einer
/// Transaktion (Single-Writer), ruht bei Scan-Sperre und während die Katalog-DB umzieht, liest keine Cloud-Platzhalter
/// und keine Dateien nicht erreichbarer Wurzeln; Fortschritt in der Statusleiste („Foto-Güte: x von y bewertet“).
/// </summary>
public interface IFileQualityIndexService
{
    FileQualityIndexStatus Status { get; }

    /// <summary>Nach der DB-Initialisierung: Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    void Start();

    /// <summary>Stößt einen Durchgang an; kehrt sofort zurück.</summary>
    void RequestPass();

    /// <summary>Bricht den laufenden Durchgang ab (das Erreichte bleibt gespeichert).</summary>
    void CancelPass();

    /// <summary>Ein Durchgang über den aktuellen Bereich (öffentlich für Tests und Rauchtest).</summary>
    Task<FileQualityIndexReport> RunPassAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IFileQualityIndexService"/>
public sealed partial class FileQualityIndexService : IFileQualityIndexService, IDisposable
{
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryContextFactory _factory;
    private readonly IFaceImageLoader? _imageLoader;
    private readonly ILibraryScope? _scope;
    private readonly IQualityAesthetics? _aesthetics;
    private readonly IScanGate? _scanGate;
    private readonly Background.IBackgroundPlanner? _planner;
    private readonly ILogger<FileQualityIndexService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly SemaphoreSlim _runSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _passCts;
    private int _started;
    private int _runRequested;
    private int _delayedRequest;
    private FileQualityIndexStatus _status = FileQualityIndexStatus.Idle;
    private DateTime _lastStatusUtc;

    /// <param name="imageLoader">WIC-Lader aus dem Wpf-Projekt (wie Gesichter und Fingerabdrücke); ohne ihn keine Bewertung.</param>
    /// <param name="scope">Bereich (ADR-0048); ohne ihn (Tests) alle Bibliotheken des Katalogs.</param>
    /// <param name="aesthetics">Bildwirkung aus Motiv-Vektoren; ohne sie bleibt <c>Aesthetic</c> NULL.</param>
    /// <param name="scanGate">Optional (Tests); produktiv der gemeinsame Singleton.</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public FileQualityIndexService(
        ILibraryContextFactory factory,
        ILogger<FileQualityIndexService> logger,
        IFaceImageLoader? imageLoader = null,
        ILibraryScope? scope = null,
        IQualityAesthetics? aesthetics = null,
        IScanGate? scanGate = null,
        IMessenger? messenger = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _planner = planner;
        _factory = factory;
        _logger = logger;
        _imageLoader = imageLoader;
        _scope = scope;
        _aesthetics = aesthetics;
        _scanGate = scanGate;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>Wartezeit vom Start bis zum ersten Durchgang (nach den Doppelten, die nach 15 s beginnen).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Wartezeit nach neuen Motiv-Vektoren, bevor deren Bildwirkung gerechnet wird (bündelt viele Meldungen).</summary>
    public TimeSpan MotifDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Wartezeit, wenn die Katalog-DB gerade gesperrt war (Umzug).</summary>
    public TimeSpan SuspendedRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Bilder je Stapel (Schärfe/Belichtung).</summary>
    public int PixelBatchSize { get; init; } = 100;

    /// <summary>Vektoren je Stapel (Bildwirkung).</summary>
    public int AestheticBatchSize { get; init; } = 500;

    public FileQualityIndexStatus Status => Volatile.Read(ref _status);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<FileQualityIndexService, ScanCompletedMessage>(this, static (r, _) => r.RequestPass());
        _messenger.Register<FileQualityIndexService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Added)
            {
                r.RequestPass();
            }
        });
        _messenger.Register<FileQualityIndexService, ActiveLibraryChangedMessage>(this, static (r, _) =>
        {
            r.CancelPass();
            r.RequestPass();
        });
        _messenger.Register<FileQualityIndexService, MotifsChangedMessage>(this, static (r, _) => r.RequestPassDelayed());

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

    public void CancelPass()
    {
        lock (_sync)
        {
            try
            {
                _passCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Durchgang gerade zu Ende.
            }
        }
    }

    /// <summary>Ein Durchgang nach <see cref="MotifDelay"/> — höchstens einer steht aus (der Motiv-Index meldet stapelweise).</summary>
    private void RequestPassDelayed()
    {
        if (Interlocked.Exchange(ref _delayedRequest, 1) != 0)
        {
            return;
        }

        var token = _stop.Token;
        _ = Task.Delay(MotifDelay, token).ContinueWith(
            _ =>
            {
                Volatile.Write(ref _delayedRequest, 0);
                RequestPass();
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
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
            // Bereich gewechselt bzw. abgebrochen — ein neuer Durchgang ist ggf. schon angefordert.
            PublishStatus(FileQualityIndexStatus.Idle, force: true);
        }
        catch (Exception ex) when (IsCatalogUnavailable(ex))
        {
            _logger.LogInformation("Foto-Güte: Katalog gerade nicht verfügbar — neuer Versuch in {Seconds:N0} s", SuspendedRetryDelay.TotalSeconds);
            PublishStatus(new FileQualityIndexStatus(false, string.Empty, 0, 0, "wartet auf den Katalog"), force: true);
            await Task.Delay(SuspendedRetryDelay, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _runRequested, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foto-Güte: Durchgang abgebrochen");
            PublishStatus(FileQualityIndexStatus.Idle, force: true);
        }
    }

    private static bool IsCatalogUnavailable(Exception ex)
        => ex is DatabaseSuspendedException || ex.InnerException is DatabaseSuspendedException || SqliteBusyRetry.IsBusy(ex);

    public async Task<FileQualityIndexReport> RunPassAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _passLock.WaitAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            _passCts = linked;
        }

        var touched = new HashSet<int>();
        try
        {
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            var libraries = await ScopeAsync(token).ConfigureAwait(false);
            if (libraries is { Count: 0 })
            {
                PublishStatus(FileQualityIndexStatus.Idle, force: true);
                return new FileQualityIndexReport(0, 0, 0, 0);
            }

            var sw = Stopwatch.StartNew();
            var filter = ScopeFilter(libraries);
            var pixels = await RunPixelPhaseAsync(filter, libraries, touched, token).ConfigureAwait(false);
            var aesthetic = await RunAestheticPhaseAsync(filter, touched, token).ConfigureAwait(false);
            PublishStatus(FileQualityIndexStatus.Idle, force: true);

            var report = new FileQualityIndexReport(pixels.Done, aesthetic, pixels.Skipped, pixels.Failed);
            if (report.Rated + report.Aesthetic + report.Failed > 0)
            {
                _logger.LogInformation(
                    "Foto-Güte: {Rated} bewertet, {Aesthetic} Bildwirkungen, {Skipped} übersprungen (fehlt/nur online), {Failed} nicht lesbar in {Sec:N1} s",
                    report.Rated, report.Aesthetic, report.Skipped, report.Failed, sw.Elapsed.TotalSeconds);
            }

            return report;
        }
        finally
        {
            // auch nach Abbruch oder Fehler: was schon geschrieben ist, melden
            if (touched.Count > 0)
            {
                Send(new FileQualityChangedMessage(touched.ToList()));
            }

            lock (_sync)
            {
                _passCts = null;
            }

            _passLock.Release();
        }
    }

    /// <summary>Bibliotheken des aktuellen Bereichs; null = ohne Bereichsdienst alle (Tests).</summary>
    private async Task<IReadOnlyList<int>?> ScopeAsync(CancellationToken token)
    {
        if (_scope is null)
        {
            return null;
        }

        var current = await _scope.GetCurrentAsync(token).ConfigureAwait(false);
        return current.HasLibrary ? current.LibraryIds : [];
    }

    /// <summary>SQL-Bedingung für <c>f.LibraryId</c> (nur Ganzzahlen aus dem Katalog, daher eingebettet).</summary>
    private static string ScopeFilter(IReadOnlyList<int>? libraries)
        => libraries is null
            ? "1 = 1"
            : "f.LibraryId IN (" + string.Join(",", libraries.Select(id => id.ToString(CultureInfo.InvariantCulture))) + ")";

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
        => _planner is null ? null : await _planner.AcquireAsync(Background.BackgroundJob.Quality, token).ConfigureAwait(false);

    private bool AnyBusy(IEnumerable<int> libraryIds) => _scanGate is { } gate && libraryIds.Distinct().Any(gate.IsBusy);

    /// <summary>
    /// Wurzeln des Bereichs (<paramref name="libraries"/>; null = alle), die gerade nicht erreichbar sind — ihre Dateien
    /// werden ohne Dateizugriff übersprungen.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadOfflineRootsAsync(IReadOnlyList<int>? libraries, CancellationToken token)
    {
        List<string> roots;
        await using (var ctx = _factory.Create())
        {
            var query = ctx.LibraryRoots.AsNoTracking();
            if (libraries is not null)
            {
                query = query.Where(r => libraries.Contains(r.LibraryId));
            }

            roots = await query.Select(r => r.Path).ToListAsync(token).ConfigureAwait(false);
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

    private void PublishStatus(FileQualityIndexStatus status, bool force = false)
    {
        var now = DateTime.UtcNow;
        Volatile.Write(ref _status, status);
        if (!force && now - _lastStatusUtc < StatusInterval)
        {
            return;
        }

        _lastStatusUtc = now;
        Send(new FileQualityIndexStatusMessage(status));
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
