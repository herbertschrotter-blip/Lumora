using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Catalog;
using Lumora.Services.Database;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Places.Tracks;

namespace Lumora.Services.Places.Suggestions;

/// <summary>Ein übernommener Vorschlag (Liste, Lightbox; Ort ggf. von Hand verschoben).</summary>
public sealed record GeoSuggestionApply(int FileId, double Latitude, double Longitude, GeoSource Source);

/// <summary>Kamera im Bereich: Zahl der Fotos und — nur mit eigenem GPS-Zeitstempel — die daraus ermittelte Abweichung.</summary>
public sealed record CameraTimeInfo(string Camera, int PhotoCount, int? SuggestedOffsetSeconds);

/// <summary>Vorschläge für ungenaue Orte und Fotos ohne Ort (ADR-0062) — immer im übergebenen Bibliotheksbereich.</summary>
public interface IGeoSuggestionService
{
    /// <summary>Fotos der Liste samt Vorschlägen (neueste zuerst).</summary>
    Task<GeoSuggestionSet> LoadAsync(LibraryScopeInfo scope, GeoSuggestionListKind kind, CancellationToken ct = default);

    /// <summary>Zähler „n mit geschätzter Position“, „ohne Ort“, Aktivitäten der Gruppe.</summary>
    Task<GeoAccuracyCounts> CountAsync(LibraryScopeInfo scope, CancellationToken ct = default);

    /// <summary>Ein Foto (Lightbox): geschätzt bzw. ohne Ort samt Vorschlag; null, wenn es weder noch ist.</summary>
    Task<GeoSuggestionItem?> GetForFileAsync(int fileId, CancellationToken ct = default);

    /// <summary>Vorschlag ablehnen (bleibt, wird nicht mehr vorgeschlagen) bzw. wieder zulassen.</summary>
    Task SetRejectedAsync(IReadOnlyCollection<int> fileIds, bool rejected, CancellationToken ct = default);

    /// <summary>Übernehmen — nur im Katalog oder auch in die Datei (EXIF/XMP); Rückgängig über die Batch-Id.</summary>
    Task<GeoAssignResult> ApplyAsync(IReadOnlyList<GeoSuggestionApply> items, bool writeToFile, CancellationToken ct = default);

    /// <summary>Kameras des Bereichs (häufigste zuerst, höchstens 30) für Einstellungen › Orte › Zeitabgleich.</summary>
    Task<IReadOnlyList<CameraTimeInfo>> GetCamerasAsync(LibraryScopeInfo scope, CancellationToken ct = default);
}

/// <summary>
/// Liest Kandidaten (geschätzt bzw. ohne Ort, nicht abgelehnt), Stützen (genaue Fotos und aus Spuren übernommene Orte
/// im Bereich) und die Spuren der Gruppe, gleicht die Zeiten ab (<see cref="PhotoTimeResolver"/>) und rechnet die
/// Vorschläge (<see cref="GeoSuggestionEngine"/>). Von Hand gesetzte Orte (<c>GeoSource</c> 3–5) sind nie Kandidaten.
/// </summary>
public sealed class GeoSuggestionService : IGeoSuggestionService
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IGpsTrackStore _tracks;
    private readonly IGeoTrackSettingsStore _settings;
    private readonly IGeoAssignService _assign;
    private readonly ILibraryScope _scope;
    private readonly ILogger<GeoSuggestionService> _logger;
    private readonly IMessenger _messenger;

    public GeoSuggestionService(
        ILibraryContextFactory factory,
        IGpsTrackStore tracks,
        IGeoTrackSettingsStore settings,
        IGeoAssignService assign,
        ILibraryScope scope,
        ILogger<GeoSuggestionService> logger,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _tracks = tracks;
        _settings = settings;
        _assign = assign;
        _scope = scope;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    private sealed record Row(
        int FileId, int LibraryId, string Name, int MediaType, long? CapturedAt, int? Offset, int? Source, int Precision,
        int? ExifOffset, long? GpsTime, string? Camera, double? Latitude, double? Longitude, int? Accuracy, int? Reason, int? GeoSource);

    private const string RowColumns = """
        f.Id, f.LibraryId, f.Name, f.MediaType, f.CapturedAt, f.CapturedAtOffset, f.CapturedAtSource, f.CapturedAtPrecision,
        f.ExifTimeOffset, f.GpsTimeUtc, f.CameraModel, f.Latitude, f.Longitude, f.GeoAccuracy, f.GeoAccuracyReason, f.GeoSource
        """;

    private const string InaccurateCondition = "f.GeoSource = 1 AND f.GeoAccuracy = 2 AND f.Latitude IS NOT NULL";

    private const string UnlocatedCondition = "f.GeoSource = 0 AND f.Latitude IS NULL AND f.MediaType IN (0, 1)";

    public async Task<GeoSuggestionSet> LoadAsync(LibraryScopeInfo scope, GeoSuggestionListKind kind, CancellationToken ct = default)
    {
        if (!scope.HasLibrary)
        {
            return GeoSuggestionSet.Empty(kind);
        }

        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var condition = kind == GeoSuggestionListKind.Inaccurate ? InaccurateCondition : UnlocatedCondition;
        var rows = await Task.Run(() => ReadRows(
            $"SELECT {RowColumns} FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND f.GeoSuggestionState = 0 AND {condition};",
            null), ct).ConfigureAwait(false);
        var items = await SuggestAsync(rows, scope, settings, ct).ConfigureAwait(false);
        var trackCount = (await _tracks.GetSummaryAsync(scope.GroupId, ct).ConfigureAwait(false)).Count;
        return new GeoSuggestionSet(kind, items, trackCount);
    }

    public async Task<GeoAccuracyCounts> CountAsync(LibraryScopeInfo scope, CancellationToken ct = default)
    {
        if (!scope.HasLibrary)
        {
            return new GeoAccuracyCounts(0, 0, 0);
        }

        var (estimated, unlocated) = await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT (SELECT COUNT(*) FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND {InaccurateCondition}),
                       (SELECT COUNT(*) FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND {UnlocatedCondition});
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1));
        }, ct).ConfigureAwait(false);
        var activities = (await _tracks.GetSummaryAsync(scope.GroupId, ct).ConfigureAwait(false)).Count;
        return new GeoAccuracyCounts(estimated, unlocated, activities);
    }

    public async Task<GeoSuggestionItem?> GetForFileAsync(int fileId, CancellationToken ct = default)
    {
        var rows = await Task.Run(() => ReadRows(
            $"SELECT {RowColumns} FROM Files f WHERE f.Id = $id AND f.Missing = 0 AND f.GeoSuggestionState = 0 AND (({InaccurateCondition}) OR ({UnlocatedCondition}));",
            fileId), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        // Bereich: der aktuelle, wenn er die Datei enthält — sonst nur ihre Bibliothek (Bibliotheks-Trennung)
        var current = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var scope = current.Contains(rows[0].LibraryId)
            ? current
            : LibraryScopeInfo.ForLibrary(rows[0].LibraryId, await Task.Run(() => ReadGroup(rows[0].LibraryId), ct).ConfigureAwait(false));
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        return (await SuggestAsync(rows, scope, settings, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task SetRejectedAsync(IReadOnlyCollection<int> fileIds, bool rejected, CancellationToken ct = default)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        var libraries = await Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            var libs = new HashSet<int>();
            foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                var ids = CatalogFileTargets.BindIds(cmd, chunk, "$f");
                cmd.CommandText = $"UPDATE Files SET GeoSuggestionState = $s WHERE Id IN ({ids}) RETURNING LibraryId;";
                cmd.Parameters.AddWithValue("$s", rejected ? 1 : 0);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    libs.Add(reader.GetInt32(0));
                }
            }

            tx.Commit();
            return libs;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Orte: {Count} Vorschläge {Action}", fileIds.Count, rejected ? "abgelehnt" : "wieder zugelassen");
        try
        {
            _messenger.Send(new GeoAccuracyChangedMessage(libraries.Order().ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der GeoAccuracyChangedMessage warf eine Ausnahme");
        }
    }

    public Task<GeoAssignResult> ApplyAsync(IReadOnlyList<GeoSuggestionApply> items, bool writeToFile, CancellationToken ct = default)
    {
        var description = items.Count == 1
            ? "Ort aus Vorschlag übernommen (1 Datei)"
            : $"Orte aus Vorschlägen übernommen ({items.Count} Dateien)";
        return _assign.AssignAsync(
            items.Select(i => new GeoLocationAssignment(i.FileId, i.Latitude, i.Longitude, i.Source)).ToList(),
            writeToFile,
            keepModifiedTime: true,
            description + (writeToFile ? string.Empty : " — nur im Katalog"),
            ct);
    }

    public Task<IReadOnlyList<CameraTimeInfo>> GetCamerasAsync(LibraryScopeInfo scope, CancellationToken ct = default) => Task.Run<IReadOnlyList<CameraTimeInfo>>(() =>
    {
        if (!scope.HasLibrary)
        {
            return [];
        }

        using var connection = _factory.OpenSqliteConnection();
        var cameras = new List<(string Camera, int Count)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT CameraModel, COUNT(*) FROM Files
                WHERE LibraryId IN ({scope.SqlLibraryIdList}) AND Missing = 0 AND CameraModel IS NOT NULL
                GROUP BY CameraModel ORDER BY COUNT(*) DESC LIMIT 30;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cameras.Add((reader.GetString(0), reader.GetInt32(1)));
            }
        }

        var result = new List<CameraTimeInfo>(cameras.Count);
        using var samples = connection.CreateCommand();
        samples.CommandText = $"""
            SELECT CapturedAt + COALESCE(CapturedAtOffset, 0) * 60, GpsTimeUtc, ExifTimeOffset FROM Files
            WHERE LibraryId IN ({scope.SqlLibraryIdList}) AND Missing = 0 AND CameraModel = $c AND GpsTimeUtc IS NOT NULL
              AND GeoSource = 1 AND GeoAccuracy = 1 AND CapturedAtSource = 1 AND CapturedAt IS NOT NULL
            LIMIT 500;
            """;
        var pCamera = samples.Parameters.Add("$c", SqliteType.Text);
        foreach (var (camera, count) in cameras)
        {
            ct.ThrowIfCancellationRequested();
            pCamera.Value = camera;
            var list = new List<(long, long, int?)>();
            using (var reader = samples.ExecuteReader())
            {
                while (reader.Read())
                {
                    list.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
                }
            }

            result.Add(new CameraTimeInfo(camera, count, CameraOffsetEstimator.Estimate(list)));
        }

        return result;
    }, ct);

    // ---- Rechnen -------------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<GeoSuggestionItem>> SuggestAsync(IReadOnlyList<Row> rows, LibraryScopeInfo scope, GeoTrackSettings settings, CancellationToken ct)
    {
        var zone = settings.ResolveTimeZone();
        var times = new Dictionary<int, PhotoTime>(rows.Count);
        foreach (var row in rows)
        {
            if (Resolve(row, zone, settings) is { } time)
            {
                times[row.FileId] = time;
            }
        }

        var candidates = times.Select(kv => new SuggestionCandidate(kv.Key, kv.Value.Utc)).ToList();
        IReadOnlyList<SuggestionAnchor> anchors = [];
        IReadOnlyList<GpsTrackInfo> trackInfos = [];
        if (candidates.Count > 0)
        {
            var min = candidates.Min(c => c.Utc);
            var max = candidates.Max(c => c.Utc);
            anchors = await Task.Run(() => ReadAnchors(scope, min, max, zone, settings), ct).ConfigureAwait(false);
            // Rand für den Uhr-Hinweis (Mockup 25): auch Spuren bis 2 h vor bzw. nach den Fotos
            var margin = Math.Max(settings.ToleranceSeconds, GeoSuggestionEngine.NearTrackSeconds);
            trackInfos = await _tracks.FindOverlappingAsync(scope.GroupId, min - margin, max + margin, ct).ConfigureAwait(false);
        }

        var tracks = trackInfos
            .Select(t => new SuggestionTrack(t.Id, t.StartUnix, t.EndUnix, () => LoadPointsSafe(t.Id), preferred: t.Source == GpsTrackSource.App))
            .ToList();
        var byTrack = trackInfos.ToDictionary(t => t.Id);
        var (suggestions, comparisons) = await Task.Run(
            () => (GeoSuggestionEngine.Suggest(candidates, anchors, tracks, settings.ToleranceSeconds),
                   GeoSuggestionEngine.Compare(candidates, anchors, tracks, settings.ToleranceSeconds)),
            ct).ConfigureAwait(false);

        var items = new List<GeoSuggestionItem>(rows.Count);
        foreach (var row in rows)
        {
            times.TryGetValue(row.FileId, out var time);
            var hasTime = times.ContainsKey(row.FileId);
            suggestions.TryGetValue(row.FileId, out var suggestion);
            comparisons.TryGetValue(row.FileId, out var sources);
            var trackId = suggestion?.TrackId ?? sources?.FromTrack?.TrackId ?? sources?.NearTrack?.TrackId;
            DateTime? local = row.CapturedAt is { } at && row.Source is not null and not 0
                ? DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime.AddMinutes(row.Offset ?? 0)
                : null;
            items.Add(new GeoSuggestionItem(
                row.FileId,
                row.LibraryId,
                row.Name,
                row.MediaType,
                local is null ? null : DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified),
                hasTime ? time.Utc : null,
                hasTime ? time.Basis : null,
                row.Latitude,
                row.Longitude,
                (GeoAccuracyReason)(row.Reason ?? 0),
                suggestion,
                trackId is { } id && byTrack.TryGetValue(id, out var info) ? info : null,
                sources));
        }

        return items
            .OrderByDescending(i => i.LocalTime ?? DateTime.MinValue)
            .ThenByDescending(i => i.FileId)
            .ToList();
    }

    private TrackPoint[] LoadPointsSafe(int trackId)
    {
        try
        {
            return _tracks.LoadPointsAsync(trackId).GetAwaiter().GetResult();
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Orte: Spur {TrackId} nicht lesbar", trackId);
            return [];
        }
    }

    /// <summary>UTC eines Fotos (null ohne genaues Aufnahmedatum).</summary>
    private static PhotoTime? Resolve(Row row, TimeZoneInfo zone, GeoTrackSettings settings)
        => PhotoTimeResolver.ResolveCatalog(row.CapturedAt, row.Offset, row.Source, row.Precision, row.ExifOffset, row.GpsTime,
            row.Accuracy, row.GeoSource, row.Camera, zone, settings);

    /// <summary>Stützen im Bereich: genaue EXIF-Orte (UTC = GPS-Zeit, falls vorhanden) und aus Spuren übernommene Orte.</summary>
    private List<SuggestionAnchor> ReadAnchors(LibraryScopeInfo scope, long fromUnix, long toUnix, TimeZoneInfo zone, GeoTrackSettings settings)
    {
        // Grobe Vorauswahl über CapturedAt (± 1 Tag deckt jede Zeitzone ab), genau nach dem Zeitabgleich
        var rows = ReadRows(
            $"""
            SELECT {RowColumns} FROM Files f
            WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND f.Latitude IS NOT NULL
              AND ((f.GeoSource = 1 AND f.GeoAccuracy = 1) OR f.GeoSource = 4)
              AND f.CapturedAt BETWEEN $from AND $to;
            """,
            null,
            ("$from", fromUnix - 86_400),
            ("$to", toUnix + 86_400));
        var anchors = new List<SuggestionAnchor>(rows.Count);
        foreach (var row in rows)
        {
            long? utc = row.GpsTime is { } gps && row.GeoSource == (int)GeoSource.Exif ? gps : Resolve(row, zone, settings)?.Utc;
            if (utc is { } u && row.Latitude is { } lat && row.Longitude is { } lon)
            {
                anchors.Add(new SuggestionAnchor(row.FileId, u, lat, lon));
            }
        }

        anchors.Sort((a, b) => a.Utc != b.Utc ? a.Utc.CompareTo(b.Utc) : a.FileId.CompareTo(b.FileId));
        return anchors;
    }

    private List<Row> ReadRows(string sql, int? fileId, params (string Name, object Value)[] parameters)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (fileId is { } id)
        {
            cmd.Parameters.AddWithValue("$id", id);
        }

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var rows = new List<Row>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3),
                NullableLong(reader, 4),
                NullableInt(reader, 5),
                NullableInt(reader, 6),
                reader.GetInt32(7),
                NullableInt(reader, 8),
                NullableLong(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11),
                reader.IsDBNull(12) ? null : reader.GetDouble(12),
                NullableInt(reader, 13),
                NullableInt(reader, 14),
                NullableInt(reader, 15)));
        }

        return rows;
    }

    private int ReadGroup(int libraryId)
    {
        using var connection = _factory.OpenSqliteConnection();
        return LibraryGroupStore.ReadGroupOf(connection, null, libraryId) ?? 0;
    }

    private static long? NullableLong(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);

    private static int? NullableInt(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
}
