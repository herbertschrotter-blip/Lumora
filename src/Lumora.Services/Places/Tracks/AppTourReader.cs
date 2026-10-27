using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lumora.Services.Database;
using Lumora.Services.Places.Suggestions;

namespace Lumora.Services.Places.Tracks;

/// <summary>Bestiegener Gipfel einer Tour (aus <c>AppTours.Summits</c>).</summary>
public sealed record AppTourSummit(string Name, int ElevationM, DateTime ArrivalUtc, bool Confirmed);

/// <summary>Tour einer verbundenen App mit der Zahl ihrer Fotos im Bibliotheksbereich (Galerie-Filter „Tour“, Mockup 24).</summary>
public sealed record AppTourInfo(
    int Id,
    string AppId,
    string Name,
    DateTime StartUtc,
    DateTime EndUtc,
    int StartOffsetMinutes,
    string Sport,
    string? Region,
    double DistanceMeters,
    double AscentMeters,
    IReadOnlyList<AppTourSummit> Summits,
    int PhotoCount,
    GeoBounds? Bounds = null)
{
    /// <summary>Beginn in der Ortszeit der Tour.</summary>
    public DateTime LocalStart => DateTime.SpecifyKind(StartUtc.AddMinutes(StartOffsetMinutes), DateTimeKind.Unspecified);

    /// <summary>Ende in der Ortszeit der Tour.</summary>
    public DateTime LocalEnd => DateTime.SpecifyKind(EndUtc.AddMinutes(StartOffsetMinutes), DateTimeKind.Unspecified);
}

/// <summary>Ein Foto einer Tour mit seiner abgeglichenen UTC-Zeit (Zeitstrahl).</summary>
public readonly record struct AppTourPhoto(int FileId, long Utc);

/// <summary>
/// Touren verbundener Apps lesen (App-Verbindung Stufe 7 Teil B, ADR-0073, Mockup 24): Liste mit Fotozahl je Tour, die Fotos
/// einer Tour und die Tour eines Fotos. Ein Foto gehört zur Tour, wenn seine Aufnahmezeit — abgeglichen wie bei „Ungenaue
/// Orte“ (<see cref="PhotoTimeResolver.ResolveCatalog"/>: Zeitzone, GPS-Zeit, Kamera-Abweichung) — zwischen Beginn und Ende
/// liegt, ± der Toleranz aus Einstellungen › Orte. Nur Fotos und Videos mit genauem Aufnahmedatum, nur im Bibliotheksbereich.
/// </summary>
public interface IAppTourReader
{
    /// <summary>Gibt es überhaupt Touren? (Chip „Tour“ erst dann zeigen, wie Alben und Kategorien.)</summary>
    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>Alle Touren, neueste zuerst, mit der Zahl ihrer Fotos in <paramref name="libraryIds"/>.</summary>
    Task<IReadOnlyList<AppTourInfo>> ListAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct = default);

    /// <summary>Die Fotos einer Tour (nach Zeit); null, wenn es die Tour nicht mehr gibt.</summary>
    Task<(AppTourInfo Tour, IReadOnlyList<AppTourPhoto> Photos)?> PhotosAsync(int tourId, IReadOnlyCollection<int> libraryIds, CancellationToken ct = default);

    /// <summary>Die Tour, während der die Datei aufgenommen wurde (Lightbox); null ohne.</summary>
    Task<AppTourInfo?> ForFileAsync(int fileId, CancellationToken ct = default);
}

/// <inheritdoc cref="IAppTourReader"/>
public sealed class AppTourReader(ILibraryContextFactory factory, IGeoTrackSettingsStore settings) : IAppTourReader
{
    /// <summary>Grobe Vorauswahl über die gespeicherte Zeit: ± 1 Tag deckt jede Zeitzone und Kamera-Abweichung ab.</summary>
    private const long Slack = 24 * 60 * 60;

    private const string TourColumns = "t.Id, t.AppId, t.Name, t.StartUtc, t.EndUtc, t.StartOffsetMinutes, t.Sport, t.Region, t.DistanceMeters, t.AscentMeters, t.Summits, t.MinLatitude, t.MinLongitude, t.MaxLatitude, t.MaxLongitude";

    private const string FileColumns = """
        f.Id, f.CapturedAt, f.CapturedAtOffset, f.CapturedAtSource, f.CapturedAtPrecision, f.ExifTimeOffset, f.GpsTimeUtc,
        f.GeoAccuracy, f.GeoSource, f.CameraModel
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<bool> AnyAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM AppTours);";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }, ct);

    public async Task<IReadOnlyList<AppTourInfo>> ListAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct = default)
    {
        var timing = await settings.LoadAsync(ct).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<AppTourInfo>>(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            var tours = ReadTours(connection, "1 = 1", []);
            if (tours.Count == 0 || libraryIds.Count == 0)
            {
                return tours;
            }

            var counts = new Dictionary<int, int>();
            var zone = timing.ResolveTimeZone();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT t.Id, t.StartUtc, t.EndUtc, {FileColumns}
                FROM AppTours t
                JOIN Files f ON f.LibraryId IN ({Ids(libraryIds)}) AND f.Missing = 0 AND f.MediaType IN (0, 1)
                    AND COALESCE(f.CapturedAt, f.ModifiedAtUtc) BETWEEN t.StartUtc - {Slack} AND t.EndUtc + {Slack};
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (Within(reader, 3, reader.GetInt64(1), reader.GetInt64(2), zone, timing) is not null)
                {
                    var id = reader.GetInt32(0);
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                }
            }

            return tours.Select(t => t with { PhotoCount = counts.GetValueOrDefault(t.Id) }).ToList();
        }, ct).ConfigureAwait(false);
    }

    public async Task<(AppTourInfo Tour, IReadOnlyList<AppTourPhoto> Photos)?> PhotosAsync(int tourId, IReadOnlyCollection<int> libraryIds, CancellationToken ct = default)
    {
        var timing = await settings.LoadAsync(ct).ConfigureAwait(false);
        return await Task.Run<(AppTourInfo, IReadOnlyList<AppTourPhoto>)?>(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            if (ReadTours(connection, "t.Id = $id", [("$id", tourId)]).FirstOrDefault() is not { } tour)
            {
                return null;
            }

            var photos = new List<AppTourPhoto>();
            if (libraryIds.Count > 0)
            {
                var start = Unix(tour.StartUtc);
                var end = Unix(tour.EndUtc);
                var zone = timing.ResolveTimeZone();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT {FileColumns} FROM Files f
                    WHERE f.LibraryId IN ({Ids(libraryIds)}) AND f.Missing = 0 AND f.MediaType IN (0, 1)
                      AND COALESCE(f.CapturedAt, f.ModifiedAtUtc) BETWEEN $from AND $to;
                    """;
                cmd.Parameters.AddWithValue("$from", start - Slack);
                cmd.Parameters.AddWithValue("$to", end + Slack);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (Within(reader, 0, start, end, zone, timing) is { } utc)
                    {
                        photos.Add(new AppTourPhoto(reader.GetInt32(0), utc));
                    }
                }
            }

            photos.Sort((a, b) => a.Utc != b.Utc ? a.Utc.CompareTo(b.Utc) : a.FileId.CompareTo(b.FileId));
            return (tour with { PhotoCount = photos.Count }, photos);
        }, ct).ConfigureAwait(false);
    }

    public async Task<AppTourInfo?> ForFileAsync(int fileId, CancellationToken ct = default)
    {
        var timing = await settings.LoadAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT t.Id, t.StartUtc, t.EndUtc, {FileColumns}
                FROM Files f
                JOIN AppTours t ON COALESCE(f.CapturedAt, f.ModifiedAtUtc) BETWEEN t.StartUtc - {Slack} AND t.EndUtc + {Slack}
                WHERE f.Id = $id
                ORDER BY t.StartUtc DESC;
                """;
            cmd.Parameters.AddWithValue("$id", fileId);
            var zone = timing.ResolveTimeZone();
            int? match = null;
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read() && match is null)
                {
                    if (Within(reader, 3, reader.GetInt64(1), reader.GetInt64(2), zone, timing) is not null)
                    {
                        match = reader.GetInt32(0);
                    }
                }
            }

            return match is { } id ? ReadTours(connection, "t.Id = $id", [("$id", id)]).FirstOrDefault() : null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Abgeglichene UTC-Zeit, wenn sie in [Beginn − Toleranz, Ende + Toleranz] liegt; Spalten ab <paramref name="first"/>.</summary>
    private static long? Within(SqliteDataReader r, int first, long start, long end, TimeZoneInfo zone, GeoTrackSettings timing)
    {
        static long? L(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        static int? I(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        var time = PhotoTimeResolver.ResolveCatalog(
            L(r, first + 1), I(r, first + 2), I(r, first + 3), I(r, first + 4) ?? 0, I(r, first + 5), L(r, first + 6),
            I(r, first + 7), I(r, first + 8), r.IsDBNull(first + 9) ? null : r.GetString(first + 9), zone, timing);
        return time is { Utc: var utc } && utc >= start - timing.ToleranceSeconds && utc <= end + timing.ToleranceSeconds ? utc : null;
    }

    private static List<AppTourInfo> ReadTours(SqliteConnection connection, string where, IReadOnlyList<(string Name, object Value)> args)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {TourColumns} FROM AppTours t WHERE {where} ORDER BY t.StartUtc DESC, t.Id DESC;";
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var list = new List<AppTourInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AppTourInfo(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).UtcDateTime,
                reader.GetInt32(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetDouble(8), reader.GetDouble(9), Summits(reader.GetString(10)), 0, Bounds(reader)));
        }

        return list;
    }

    /// <summary>Rahmen der Tour; null, wenn die Quelle keinen genannt hat (alles 0).</summary>
    private static GeoBounds? Bounds(SqliteDataReader r)
        => r.GetDouble(11) == 0 && r.GetDouble(12) == 0 && r.GetDouble(13) == 0 && r.GetDouble(14) == 0
            ? null
            : new GeoBounds(r.GetDouble(11), r.GetDouble(12), r.GetDouble(13), r.GetDouble(14));

    private static IReadOnlyList<AppTourSummit> Summits(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AppTourSummit>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string Ids(IEnumerable<int> ids) => string.Join(",", ids.Distinct().Select(i => i.ToString(CultureInfo.InvariantCulture)));
}
