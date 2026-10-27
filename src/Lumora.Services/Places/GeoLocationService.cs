using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Watch;

namespace Lumora.Services.Places;

/// <summary>Nachtragen der Koordinaten im Hintergrund (Statusleiste, App-Start).</summary>
public interface IGeoBackfillService
{
    GeoBackfillStatus BackfillStatus { get; }

    /// <summary>Nach der DB-Initialisierung: Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    void Start();

    /// <summary>Stößt einen Durchgang an; kehrt sofort zurück.</summary>
    void RequestBackfill();

    /// <summary>Ein Durchgang über alle noch nicht gelesenen Dateien (öffentlich für Tests und Messungen).</summary>
    Task<GeoBackfillReport> RunBackfillPassAsync(CancellationToken ct = default);
}

/// <summary>
/// Orte im Katalog (ADR-0050): Koordinaten nachtragen (<c>GeoLocationService.Backfill.cs</c>), „Ort setzen…“ und „Ort
/// entfernen“ mit EXIF- bzw. XMP-Schreiben (<c>.Tagging.cs</c>, <c>.FileWrites.cs</c>) und Rückgängig (<c>.Undo.cs</c>).
/// Alle Katalog-Schreibvorgänge laufen über eine gemeinsame Schreibsperre (Single-Writer), Datei-Schreibvorgänge unter
/// der Scan-Sperre der Bibliothek und angemeldet bei der <see cref="IOwnChangeRegistry"/> (ADR-0025).
/// </summary>
public sealed partial class GeoLocationService : IGeoTagService, IGeoTagUndoService, IGeoBackfillService, IDisposable
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IGeoLocationReader _reader;
    private readonly IReverseGeocoder _geocoder;
    private readonly ILogger<GeoLocationService> _logger;
    private readonly IScanGate? _scanGate;
    private readonly Background.IBackgroundPlanner? _planner;
    private readonly IOwnChangeRegistry? _ownChanges;
    private readonly IRecycleBin? _recycleBin;
    private readonly IMessenger _messenger;

    /// <summary>Single-Writer für Nachtragen, Ort setzen/entfernen und Rückgängig.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="scanGate">Optional (Tests); produktiv der gemeinsame Singleton.</param>
    /// <param name="ownChanges">Optional (Tests); produktiv der gemeinsame Singleton (Ordnerüberwachung).</param>
    /// <param name="recycleBin">Optional (Tests); Rückgängig legt von Lumora angelegte XMP-Dateien in den Papierkorb.</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public GeoLocationService(
        ILibraryContextFactory factory,
        IGeoLocationReader reader,
        IReverseGeocoder geocoder,
        ILogger<GeoLocationService> logger,
        IScanGate? scanGate = null,
        IOwnChangeRegistry? ownChanges = null,
        IRecycleBin? recycleBin = null,
        IMessenger? messenger = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _planner = planner;
        _factory = factory;
        _reader = reader;
        _geocoder = geocoder;
        _logger = logger;
        _scanGate = scanGate;
        _ownChanges = ownChanges;
        _recycleBin = recycleBin;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>Belegt die Bibliotheken (aufsteigend) für einen Datei-Batch; ohne Scan-Sperre (Tests) nichts.</summary>
    private async Task<IReadOnlyList<IDisposable>> AcquireLibrariesAsync(IEnumerable<int> libraryIds, CancellationToken ct)
    {
        var leases = new List<IDisposable>();
        if (_scanGate is null)
        {
            return leases;
        }

        try
        {
            foreach (var id in libraryIds.Distinct().Order())
            {
                leases.Add(await _scanGate.AcquireForFileOperationAsync(id, ct).ConfigureAwait(false));
            }
        }
        catch
        {
            Release(leases);
            throw;
        }

        return leases;
    }

    private static void Release(IReadOnlyList<IDisposable> leases)
    {
        for (var i = leases.Count - 1; i >= 0; i--)
        {
            leases[i].Dispose();
        }
    }

    private void Publish(Dictionary<int, List<int>> byLibrary)
    {
        foreach (var (libraryId, list) in byLibrary)
        {
            if (list.Count == 0)
            {
                continue;
            }

            try
            {
                _messenger.Send(new GeoLocationsChangedMessage(libraryId, list));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der GeoLocationsChangedMessage warf eine Ausnahme");
            }
        }
    }

    private static void AddTo(Dictionary<int, List<int>> byLibrary, int libraryId, int fileId)
    {
        if (!byLibrary.TryGetValue(libraryId, out var list))
        {
            byLibrary[libraryId] = list = new List<int>();
        }

        list.Add(fileId);
    }

    /// <summary>UPDATE der vier Ortsspalten.</summary>
    private static SqliteCommand CreateGeoUpdate(SqliteConnection connection, SqliteTransaction tx, string? extraCondition = null)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Files SET Latitude = $lat, Longitude = $lon, GeoSource = $source, PlaceId = $place WHERE Id = $id" + (extraCondition ?? string.Empty) + ";";
        cmd.Parameters.Add("$lat", SqliteType.Real);
        cmd.Parameters.Add("$lon", SqliteType.Real);
        cmd.Parameters.Add("$source", SqliteType.Integer);
        cmd.Parameters.Add("$place", SqliteType.Integer);
        cmd.Parameters.Add("$id", SqliteType.Integer);
        return cmd;
    }

    private static void SetGeoUpdate(SqliteCommand cmd, int fileId, double? lat, double? lon, int? source, int? placeId)
    {
        cmd.Parameters["$lat"].Value = (object?)lat ?? DBNull.Value;
        cmd.Parameters["$lon"].Value = (object?)lon ?? DBNull.Value;
        cmd.Parameters["$source"].Value = (object?)source ?? DBNull.Value;
        cmd.Parameters["$place"].Value = (object?)placeId ?? DBNull.Value;
        cmd.Parameters["$id"].Value = fileId;
    }

    private int _disposed;

    /// <summary>Mehrfach aufrufbar: die DI gibt den Dienst über mehrere Schnittstellen heraus und entsorgt jede.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopBackfill();
        _writeLock.Dispose();
    }
}
