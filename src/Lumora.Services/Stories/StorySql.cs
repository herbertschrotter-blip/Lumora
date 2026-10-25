using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Enums;

namespace Lumora.Services.Stories;

/// <summary>
/// SQL der Storys (ADR-0058) auf rohen Verbindungen: Zeitachse lesen (nur Fotos/Videos, nicht „fehlt“, Aufnahmedatum
/// schon gelesen), Zuhause bestimmen, gespeicherte Storys lesen, Aggregate (Zähler, Zeitraum) nachziehen. Lesen ohne
/// Transaktion (WAL), Schreiben in der Transaktion des Aufrufers.
/// </summary>
internal static class StorySql
{
    /// <summary>Sortier-/Zeitschlüssel wie die Galerie (<c>CaptureDateSortSql.SortKey</c>) — nutzt den Ausdrucks-Index.</summary>
    public const string TimeKey = "COALESCE(f.CapturedAt, f.ModifiedAtUtc)";

    /// <summary>Dateien, die in Storys zählen: Foto/Video, vorhanden, Aufnahmedatum bereits gelesen (sonst stünde es nur vorläufig).</summary>
    public const string Eligible = "f.Missing = 0 AND f.MediaType IN (0, 1) AND f.CapturedAtSource IS NOT NULL";

    private const string TimelineColumns = "f.Id, " + TimeKey + ", f.CapturedAtOffset, f.CapturedAtPrecision, f.Latitude, f.Longitude, f.PlaceId, f.MediaType";

    public static string IdList(IEnumerable<int> ids)
    {
        var text = string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        return text.Length == 0 ? "-1" : text;
    }

    /// <summary>Kalendertag (Ortszeit) einer Aufnahme: eigener Zeitversatz der Datei, sonst die Zeitzone des Rechners.</summary>
    public static int LocalDay(long utcSeconds, int? offsetMinutes, TimeZoneInfo zone)
    {
        long offset = offsetMinutes is { } m
            ? m * 60L
            : (long)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(utcSeconds, -62135596800L, 253402300799L))).TotalSeconds;
        return (int)Math.Floor((utcSeconds + offset) / 86400.0);
    }

    /// <summary>Zeitversatz der Zone je Stunde zwischengespeichert (beim Lesen ganzer Zeitachsen: einmal je Stunde statt je Datei).</summary>
    private sealed class ZoneOffsets(TimeZoneInfo zone)
    {
        private readonly Dictionary<long, int> _byHour = new();

        public int LocalDay(long utcSeconds, int? offsetMinutes)
        {
            if (offsetMinutes is { } m)
            {
                return (int)Math.Floor((utcSeconds + m * 60L) / 86400.0);
            }

            var hour = (long)Math.Floor(utcSeconds / 3600.0);
            if (!_byHour.TryGetValue(hour, out var offset))
            {
                offset = (int)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(hour * 3600, -62135596800L, 253402300799L))).TotalSeconds;
                _byHour[hour] = offset;
            }

            return (int)Math.Floor((utcSeconds + offset) / 86400.0);
        }
    }

    /// <summary>Zeitachse der Bibliotheken, nach (Zeit, Id) sortiert; optional nur [<paramref name="from"/>, <paramref name="to"/>].</summary>
    public static List<StoryTimelineItem> ReadTimeline(SqliteConnection connection, IReadOnlyCollection<int> libraryIds, TimeZoneInfo zone, long? from = null, long? to = null)
    {
        using var cmd = connection.CreateCommand();
        // Ganze Zeitachse: Tabellen-Scan („+“ schaltet den Index auf LibraryId ab — sonst je Zeile ein Sprung in die Tabelle);
        // Zeitraum: über den Ausdrucks-Index IX_Files_Library_Captured.
        cmd.CommandText = from is null
            ? $"SELECT {TimelineColumns} FROM Files f WHERE +f.LibraryId IN ({IdList(libraryIds)}) AND {Eligible};"
            : $"SELECT {TimelineColumns} FROM Files f WHERE f.LibraryId IN ({IdList(libraryIds)}) AND {Eligible} AND {TimeKey} BETWEEN $from AND $to;";
        if (from is { } a)
        {
            cmd.Parameters.AddWithValue("$from", a);
            cmd.Parameters.AddWithValue("$to", to ?? long.MaxValue);
        }

        var list = ReadItems(cmd, zone);
        list.Sort(static (x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.FileId.CompareTo(y.FileId));
        return list;
    }

    /// <summary>Bestimmte Dateien des Bereichs (Vergessene Schätze, ADR-0072), nach (Zeit, Id) sortiert.</summary>
    public static List<StoryTimelineItem> ReadByIds(SqliteConnection connection, IReadOnlyCollection<int> libraryIds, IReadOnlyCollection<int> fileIds, TimeZoneInfo zone)
    {
        if (fileIds.Count == 0)
        {
            return [];
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {TimelineColumns} FROM Files f WHERE f.Id IN ({IdList(fileIds)}) AND f.LibraryId IN ({IdList(libraryIds)}) AND {Eligible};";
        var list = ReadItems(cmd, zone);
        list.Sort(static (x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.FileId.CompareTo(y.FileId));
        return list;
    }

    /// <summary>Alle Dateien der Orte (inkrementelle Orte-Storys).</summary>
    public static List<StoryTimelineItem> ReadByPlaces(SqliteConnection connection, IReadOnlyCollection<int> libraryIds, IReadOnlyCollection<int> placeIds, TimeZoneInfo zone)
    {
        if (placeIds.Count == 0)
        {
            return [];
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {TimelineColumns} FROM Files f WHERE f.LibraryId IN ({IdList(libraryIds)}) AND f.PlaceId IN ({IdList(placeIds)}) AND {Eligible};";
        return ReadItems(cmd, zone);
    }

    /// <summary>Dateien mit bestätigtem Gesicht je (nicht ausgeblendeter) Person der Gruppe; optional nur bestimmte Personen.</summary>
    public static Dictionary<int, List<StoryTimelineItem>> ReadPersonItems(SqliteConnection connection, int groupId, IReadOnlyCollection<int> libraryIds, TimeZoneInfo zone, IReadOnlyCollection<int>? personIds = null)
    {
        using var cmd = connection.CreateCommand();
        var only = personIds is null ? string.Empty : $" AND p.Id IN ({IdList(personIds)})";
        cmd.CommandText = $"""
            SELECT DISTINCT p.Id, {TimelineColumns}
            FROM Persons p
            CROSS JOIN Faces fa ON fa.PersonId = p.Id AND fa.State = 2
            CROSS JOIN Files f ON f.Id = fa.FileId
            WHERE p.GroupId = $g AND p.IsHidden = 0{only} AND f.LibraryId IN ({IdList(libraryIds)}) AND {Eligible};
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        var result = new Dictionary<int, List<StoryTimelineItem>>();
        var offsets = new ZoneOffsets(zone);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var personId = reader.GetInt32(0);
            if (!result.TryGetValue(personId, out var list))
            {
                result[personId] = list = new List<StoryTimelineItem>();
            }

            list.Add(ReadItem(reader, 1, offsets));
        }

        return result;
    }

    /// <summary>
    /// Wie <see cref="ReadPersonItems"/>, aber aus einer schon gelesenen Zeitachse: nur Person + Datei aus dem abdeckenden
    /// Index <c>IX_Faces_Person_State_File</c>, keine Sprünge in die Files-Tabelle (volle Berechnung).
    /// </summary>
    public static Dictionary<int, List<StoryTimelineItem>> PersonItemsFromTimeline(SqliteConnection connection, int groupId, IReadOnlyList<StoryTimelineItem> timeline)
    {
        var byFile = new Dictionary<int, StoryTimelineItem>(timeline.Count);
        foreach (var item in timeline)
        {
            byFile[item.FileId] = item;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fa.PersonId, fa.FileId FROM Persons p CROSS JOIN Faces fa ON fa.PersonId = p.Id AND fa.State = 2
            WHERE p.GroupId = $g AND p.IsHidden = 0;
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        var result = new Dictionary<int, List<StoryTimelineItem>>();
        var seen = new HashSet<(int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var personId = reader.GetInt32(0);
            var fileId = reader.GetInt32(1);
            if (!byFile.TryGetValue(fileId, out var item) || !seen.Add((personId, fileId)))
            {
                continue; // andere Bibliothek, nicht zählend oder zweites Gesicht derselben Person
            }

            if (!result.TryGetValue(personId, out var list))
            {
                result[personId] = list = new List<StoryTimelineItem>();
            }

            list.Add(item);
        }

        return result;
    }

    /// <summary>
    /// Zuhause: eingestellter Ort/Koordinaten, sonst automatisch der häufigste Ort (Schwerpunkt seiner Fotos), ohne Orte
    /// die häufigste 0,1°-Zelle. Null ohne verortete Fotos.
    /// </summary>
    public static StoryHome? ReadHome(SqliteConnection connection, IReadOnlyCollection<int> libraryIds, StoryHomeSetting setting)
    {
        if (setting.Latitude is { } lat && setting.Longitude is { } lon)
        {
            return new StoryHome(lat, lon, setting.PlaceId);
        }

        var libs = IdList(libraryIds);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = setting.PlaceId is { } placeId
                ? $"SELECT PlaceId, AVG(Latitude), AVG(Longitude) FROM Files WHERE LibraryId IN ({libs}) AND PlaceId = {placeId.ToString(CultureInfo.InvariantCulture)} AND Latitude IS NOT NULL GROUP BY PlaceId;"
                : $"""
                   SELECT PlaceId, AVG(Latitude), AVG(Longitude) FROM Files
                   WHERE LibraryId IN ({libs}) AND PlaceId IS NOT NULL AND Latitude IS NOT NULL AND Missing = 0
                   GROUP BY PlaceId ORDER BY COUNT(*) DESC, PlaceId LIMIT 1;
                   """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new StoryHome(reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(0));
            }
        }

        if (setting.PlaceId is not null)
        {
            return null; // gewählter Ort ohne eigene Fotos und ohne Koordinaten
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT AVG(Latitude), AVG(Longitude) FROM Files
                WHERE LibraryId IN ({libs}) AND Latitude IS NOT NULL AND Missing = 0
                GROUP BY CAST(Latitude * 10 AS INTEGER), CAST(Longitude * 10 AS INTEGER) ORDER BY COUNT(*) DESC LIMIT 1;
                """;
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new StoryHome(reader.GetDouble(0), reader.GetDouble(1), null) : null;
        }
    }

    /// <summary>Gespeicherte Storys einer Gruppe samt Einträgen; <paramref name="where"/> schränkt ein (Spalten mit Alias s).</summary>
    public static List<ExistingStory> ReadExisting(SqliteConnection connection, SqliteTransaction? tx, int groupId, string where)
    {
        var stories = new Dictionary<int, ExistingStory>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT s.Id, s.Kind, s.Signature, s.StartAt, s.EndAt, s.PersonId, s.PlaceId, s.IsPinned, s.IsHidden,
                       s.CustomTitle IS NOT NULL, s.CoverIsManual, s.CoverFileId, s.FileCount
                FROM Stories s WHERE s.GroupId = $g AND ({where});
                """;
            cmd.Parameters.AddWithValue("$g", groupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                stories[id] = new ExistingStory
                {
                    Id = id,
                    Kind = (StoryKind)reader.GetInt32(1),
                    Signature = reader.GetString(2),
                    StartAt = reader.GetInt64(3),
                    EndAt = reader.GetInt64(4),
                    PersonId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    PlaceId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    IsPinned = reader.GetBoolean(7),
                    IsHidden = reader.GetBoolean(8),
                    HasCustomTitle = reader.GetBoolean(9),
                    CoverIsManual = reader.GetBoolean(10),
                    CoverFileId = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    FileCount = reader.GetInt32(12),
                };
            }
        }

        if (stories.Count == 0)
        {
            return [];
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT si.StoryId, si.FileId, si.Origin, si.Day FROM Stories s CROSS JOIN StoryItems si ON si.StoryId = s.Id
                WHERE s.GroupId = $g AND ({where});
                """;
            cmd.Parameters.AddWithValue("$g", groupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (stories.TryGetValue(reader.GetInt32(0), out var story))
                {
                    story.Items[reader.GetInt32(1)] = ((StoryItemOrigin)reader.GetInt32(2), reader.GetInt32(3));
                }
            }
        }

        return stories.Values.ToList();
    }

    /// <summary>
    /// Zähler, Zeitraum und Tage der Storys aus ihren zählenden Einträgen (automatisch + hinzugefügt) neu setzen; ein
    /// automatisches Titelbild, das nicht mehr dazugehört, wird geleert (die Erkennung wählt ein neues).
    /// </summary>
    public static void RefreshAggregates(SqliteConnection connection, SqliteTransaction tx, IReadOnlyCollection<int> storyIds, long now)
    {
        if (storyIds.Count == 0)
        {
            return;
        }

        Execute(connection, tx, "DROP TABLE IF EXISTS temp._story_touched; CREATE TEMP TABLE _story_touched (Id INTEGER PRIMARY KEY);");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO temp._story_touched (Id) VALUES ($id);";
            var p = insert.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in storyIds)
            {
                p.Value = id;
                insert.ExecuteNonQuery();
            }
        }

        Execute(connection, tx, $"""
            UPDATE Stories SET FileCount = 0, VideoCount = 0, UpdatedAtUtc = $now WHERE Id IN (SELECT Id FROM temp._story_touched);
            UPDATE Stories SET FileCount = a.c, VideoCount = a.v, StartAt = a.s, EndAt = a.e, FirstDay = a.fd, LastDay = a.ld
            FROM (SELECT si.StoryId AS Id, COUNT(*) AS c, SUM(f.MediaType = 1) AS v, MIN({TimeKey}) AS s, MAX({TimeKey}) AS e,
                         MIN(si.Day) AS fd, MAX(si.Day) AS ld
                  FROM temp._story_touched t CROSS JOIN StoryItems si ON si.StoryId = t.Id CROSS JOIN Files f ON f.Id = si.FileId
                  WHERE si.Origin >= 0 GROUP BY si.StoryId) a
            WHERE Stories.Id = a.Id;
            UPDATE Stories SET CoverFileId = NULL
            WHERE Id IN (SELECT Id FROM temp._story_touched) AND CoverFileId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM StoryItems si WHERE si.StoryId = Stories.Id AND si.FileId = Stories.CoverFileId AND si.Origin >= 0);
            UPDATE Stories SET CoverIsManual = 0 WHERE Id IN (SELECT Id FROM temp._story_touched) AND CoverFileId IS NULL;
            DROP TABLE temp._story_touched;
            """, ("$now", now));
    }

    /// <summary>Datei-Ids einer Story in Aufnahme-Reihenfolge (nur zählende Einträge, nur die angegebenen Bibliotheken).</summary>
    public static List<int> ReadStoryFileIds(SqliteConnection connection, int storyId, IReadOnlyCollection<int>? libraryIds = null)
    {
        using var cmd = connection.CreateCommand();
        var scope = libraryIds is null ? string.Empty : $" AND f.LibraryId IN ({IdList(libraryIds)})";
        cmd.CommandText = $"""
            SELECT f.Id FROM StoryItems si CROSS JOIN Files f ON f.Id = si.FileId
            WHERE si.StoryId = $s AND si.Origin >= 0{scope}
            ORDER BY {TimeKey}, f.Id;
            """;
        cmd.Parameters.AddWithValue("$s", storyId);
        var ids = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>Ein Wert mit Parameter <c>$s</c> als Text (null bei NULL bzw. ohne Zeile).</summary>
    public static string? ExecuteText(SqliteConnection connection, string sql, int id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", id);
        return cmd.ExecuteScalar() is { } value and not DBNull ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static List<StoryTimelineItem> ReadItems(SqliteCommand cmd, TimeZoneInfo zone)
    {
        var list = new List<StoryTimelineItem>();
        var offsets = new ZoneOffsets(zone);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadItem(reader, 0, offsets));
        }

        return list;
    }

    private static StoryTimelineItem ReadItem(SqliteDataReader reader, int o, ZoneOffsets offsets)
    {
        var time = reader.GetInt64(o + 1);
        int? offset = reader.IsDBNull(o + 2) ? null : reader.GetInt32(o + 2);
        return new StoryTimelineItem(
            reader.GetInt32(o),
            time,
            offsets.LocalDay(time, offset),
            (CaptureDatePrecision)reader.GetInt32(o + 3),
            reader.IsDBNull(o + 4) ? null : reader.GetDouble(o + 4),
            reader.IsDBNull(o + 5) ? null : reader.GetDouble(o + 5),
            reader.IsDBNull(o + 6) ? null : reader.GetInt32(o + 6),
            reader.GetInt32(o + 7) == 1);
    }
}
