using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Enums;
using Lumora.Services.Database;

namespace Lumora.Services.Places.Tracks;

/// <summary>Kopf einer gespeicherten Spur (ohne Punkte).</summary>
public sealed record GpsTrackInfo(
    int Id,
    DateTime StartUtc,
    DateTime EndUtc,
    int PointCount,
    double DistanceMeters,
    string? Sport,
    string? Device,
    string? Name,
    GpsTrackSource Source,
    GeoBounds Bounds)
{
    public long StartUnix => new DateTimeOffset(StartUtc).ToUnixTimeSeconds();

    public long EndUnix => new DateTimeOffset(EndUtc).ToUnixTimeSeconds();

    /// <summary>„Wandern“ bzw. „Aktivität“ für Listen und Tooltips.</summary>
    public string Title => Name ?? Sport ?? "Aktivität";
}

/// <summary>Stand der Spuren einer Gruppe (Einstellungen › Orte, Statusleiste).</summary>
public sealed record GpsTrackSummary(int Count, DateTime? FirstStartUtc, DateTime? LastEndUtc, IReadOnlyList<string> Devices, DateTime? LastImportedUtc)
{
    public static GpsTrackSummary Empty { get; } = new(0, null, null, [], null);
}

/// <summary>Eine neue Spur zum Speichern; <paramref name="AppTourId"/> = Tour einer verbundenen App (ADR-0073 Stufe 7).</summary>
public sealed record NewGpsTrack(ParsedTrack Track, byte[] Checksum, int? AppTourId = null);

/// <summary>Lesen und Schreiben der Tabelle <c>GpsTracks</c> (ADR-0062) — immer je Bibliotheks-Gruppe.</summary>
public interface IGpsTrackStore
{
    Task<GpsTrackSummary> GetSummaryAsync(int groupId, CancellationToken ct = default);

    Task<IReadOnlyList<GpsTrackInfo>> ListAsync(int groupId, CancellationToken ct = default);

    /// <summary>Spuren, deren Zeitraum [<paramref name="fromUnix"/>, <paramref name="toUnix"/>] berührt.</summary>
    Task<IReadOnlyList<GpsTrackInfo>> FindOverlappingAsync(int groupId, long fromUnix, long toUnix, CancellationToken ct = default);

    Task<TrackPoint[]> LoadPointsAsync(int trackId, CancellationToken ct = default);

    /// <summary>Prüfsummen (hex) der Spuren der Gruppe — für die Doppel-Erkennung beim Einlesen.</summary>
    Task<HashSet<string>> LoadChecksumsAsync(int groupId, CancellationToken ct = default);

    /// <summary>Spuren in einer Transaktion speichern; schon vorhandene (gleiche Prüfsumme) zählen als übersprungen.</summary>
    Task<int> InsertAsync(int groupId, IReadOnlyList<NewGpsTrack> tracks, CancellationToken ct = default);

    /// <summary>Alle Spuren der Gruppe entfernen; liefert die Zahl.</summary>
    Task<int> DeleteAllAsync(int groupId, CancellationToken ct = default);
}

/// <inheritdoc cref="IGpsTrackStore"/>
public sealed class GpsTrackStore(ILibraryContextFactory factory) : IGpsTrackStore
{
    private const string Columns = "Id, StartUtc, EndUtc, PointCount, DistanceMeters, Sport, Device, Name, Source, MinLatitude, MinLongitude, MaxLatitude, MaxLongitude";

    public Task<GpsTrackSummary> GetSummaryAsync(int groupId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(StartUtc), MAX(EndUtc), MAX(ImportedAtUtc) FROM GpsTracks WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        int count;
        DateTime? first = null, last = null, imported = null;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            count = reader.GetInt32(0);
            if (count == 0)
            {
                return GpsTrackSummary.Empty;
            }

            first = FromUnix(reader.GetInt64(1));
            last = FromUnix(reader.GetInt64(2));
            imported = FromUnix(reader.GetInt64(3));
        }

        using var devices = connection.CreateCommand();
        devices.CommandText = "SELECT Device, COUNT(*) FROM GpsTracks WHERE GroupId = $g AND Device IS NOT NULL GROUP BY Device ORDER BY MIN(StartUtc);";
        devices.Parameters.AddWithValue("$g", groupId);
        var list = new List<string>();
        using (var reader = devices.ExecuteReader())
        {
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }
        }

        return new GpsTrackSummary(count, first, last, list, imported);
    }, ct);

    public Task<IReadOnlyList<GpsTrackInfo>> ListAsync(int groupId, CancellationToken ct = default) => Task.Run<IReadOnlyList<GpsTrackInfo>>(() =>
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM GpsTracks WHERE GroupId = $g ORDER BY StartUtc DESC;";
        cmd.Parameters.AddWithValue("$g", groupId);
        return ReadInfos(cmd);
    }, ct);

    public Task<IReadOnlyList<GpsTrackInfo>> FindOverlappingAsync(int groupId, long fromUnix, long toUnix, CancellationToken ct = default) => Task.Run<IReadOnlyList<GpsTrackInfo>>(() =>
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM GpsTracks WHERE GroupId = $g AND StartUtc <= $to AND EndUtc >= $from ORDER BY StartUtc;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$from", fromUnix);
        cmd.Parameters.AddWithValue("$to", toUnix);
        return ReadInfos(cmd);
    }, ct);

    public Task<TrackPoint[]> LoadPointsAsync(int trackId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Points FROM GpsTracks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", trackId);
        return cmd.ExecuteScalar() is byte[] data ? TrackCodec.Decode(data) : [];
    }, ct);

    public Task<HashSet<string>> LoadChecksumsAsync(int groupId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Checksum FROM GpsTracks WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add(Convert.ToHexString((byte[])reader.GetValue(0)));
        }

        return set;
    }, ct);

    public async Task<int> InsertAsync(int groupId, IReadOnlyList<NewGpsTrack> tracks, CancellationToken ct = default)
    {
        if (tracks.Count == 0)
        {
            return 0;
        }

        return await Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO GpsTracks (GroupId, StartUtc, EndUtc, MinLatitude, MinLongitude, MaxLatitude, MaxLongitude, PointCount,
                                                 DistanceMeters, Sport, Device, Name, Source, Checksum, Points, ImportedAtUtc, AppTourId)
                VALUES ($g, $start, $end, $minLat, $minLon, $maxLat, $maxLon, $count, $dist, $sport, $device, $name, $source, $checksum, $points, $now, $tour);
                """;
            cmd.Parameters.AddWithValue("$g", groupId);
            var names = new[] { "$start", "$end", "$minLat", "$minLon", "$maxLat", "$maxLon", "$count", "$dist", "$sport", "$device", "$name", "$source", "$checksum", "$points" };
            var p = names.ToDictionary(n => n, n => cmd.Parameters.Add(n, n switch
            {
                "$minLat" or "$minLon" or "$maxLat" or "$maxLon" or "$dist" => SqliteType.Real,
                "$sport" or "$device" or "$name" => SqliteType.Text,
                "$checksum" or "$points" => SqliteType.Blob,
                _ => SqliteType.Integer,
            }));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var tour = cmd.Parameters.Add("$tour", SqliteType.Integer);
            var inserted = 0;
            foreach (var (track, checksum, appTourId) in tracks)
            {
                tour.Value = (object?)appTourId ?? DBNull.Value;
                ct.ThrowIfCancellationRequested();
                var points = track.Points;
                p["$start"].Value = points[0].Utc;
                p["$end"].Value = points[^1].Utc;
                p["$minLat"].Value = points.Min(x => x.Latitude);
                p["$minLon"].Value = points.Min(x => x.Longitude);
                p["$maxLat"].Value = points.Max(x => x.Latitude);
                p["$maxLon"].Value = points.Max(x => x.Longitude);
                p["$count"].Value = points.Count;
                p["$dist"].Value = Length(points);
                p["$sport"].Value = (object?)track.Sport ?? DBNull.Value;
                p["$device"].Value = (object?)track.Device ?? DBNull.Value;
                p["$name"].Value = (object?)track.Name ?? DBNull.Value;
                p["$source"].Value = (int)track.Source;
                p["$checksum"].Value = checksum;
                p["$points"].Value = TrackCodec.Encode(points);
                inserted += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return inserted;
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllAsync(int groupId, CancellationToken ct = default)
        => await Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM GpsTracks WHERE GroupId = $g;";
            cmd.Parameters.AddWithValue("$g", groupId);
            var deleted = cmd.ExecuteNonQuery();
            tx.Commit();
            return deleted;
        }, ct).ConfigureAwait(false);

    /// <summary>Länge der Spur in Metern.</summary>
    public static double Length(IReadOnlyList<TrackPoint> points)
    {
        double sum = 0;
        for (var i = 1; i < points.Count; i++)
        {
            sum += GeoMath.DistanceMeters(points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);
        }

        return sum;
    }

    private static List<GpsTrackInfo> ReadInfos(SqliteCommand cmd)
    {
        var list = new List<GpsTrackInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GpsTrackInfo(
                reader.GetInt32(0),
                FromUnix(reader.GetInt64(1)),
                FromUnix(reader.GetInt64(2)),
                reader.GetInt32(3),
                reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                (GpsTrackSource)reader.GetInt32(8),
                new GeoBounds(reader.GetDouble(9), reader.GetDouble(10), reader.GetDouble(11), reader.GetDouble(12))));
        }

        return list;
    }

    private static DateTime FromUnix(long value) => DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;

    internal static string Hex(byte[] checksum) => Convert.ToHexString(checksum);

    /// <summary>„1 284 Aktivitäten · 2014–2026 · Fenix 5, Fenix 7 · zuletzt eingelesen 22.10.2026“.</summary>
    public static string Describe(GpsTrackSummary summary)
    {
        if (summary.Count == 0)
        {
            return "Noch keine Aktivitäten eingelesen.";
        }

        var de = CultureInfo.GetCultureInfo("de-DE");
        var years = summary.FirstStartUtc!.Value.Year == summary.LastEndUtc!.Value.Year
            ? summary.FirstStartUtc.Value.Year.ToString(de)
            : $"{summary.FirstStartUtc.Value.Year}–{summary.LastEndUtc.Value.Year}";
        var parts = new List<string> { $"{summary.Count.ToString("#,##0", de)} {(summary.Count == 1 ? "Aktivität" : "Aktivitäten")}", years };
        if (summary.Devices.Count > 0)
        {
            parts.Add(string.Join(", ", summary.Devices.Take(4)) + (summary.Devices.Count > 4 ? " …" : string.Empty));
        }

        if (summary.LastImportedUtc is { } imported)
        {
            parts.Add("zuletzt eingelesen " + imported.ToLocalTime().ToString("dd.MM.yyyy", de));
        }

        return string.Join(" · ", parts);
    }
}
