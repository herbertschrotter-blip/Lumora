using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Services.Database;
using Lumora.Services.Duplicates;
using Lumora.Services.Library;

namespace Lumora.Services.Related;

/// <summary>Ergebnis von „Suchen ▸ …“: Beschriftung für den Filter-Chip und die gefundenen Dateien (das Foto selbst eingeschlossen).</summary>
public sealed record RelatedPhotos(string Label, IReadOnlyList<int> FileIds);

/// <summary>
/// „Suchen ▸“ im Rechtsklick-Menü (ADR-0068): Fotos desselben Tages, desselben Ortes, Doppelte (gleicher Inhalt oder ähnliches Bild)
/// und mit denselben bestätigten Personen — nur im aktuellen Bereich (ADR-0048). Null, wenn das Foto die Angabe nicht hat (kein
/// Aufnahmedatum, kein Ort, keine Personen, kein Fingerabdruck). Die Galerie zeigt das Ergebnis als Bedingung mit allen übrigen
/// Filtern (wie die Bildinhalt-Suche, höchstens <see cref="MaxResults"/> Dateien).
/// </summary>
public interface IRelatedPhotoSearch
{
    Task<RelatedPhotos?> SameDayAsync(int fileId, CancellationToken ct = default);

    Task<RelatedPhotos?> SamePlaceAsync(int fileId, CancellationToken ct = default);

    Task<RelatedPhotos?> DuplicatesAsync(int fileId, CancellationToken ct = default);

    Task<RelatedPhotos?> SamePersonsAsync(int fileId, CancellationToken ct = default);
}

/// <summary>Umsetzung von <see cref="IRelatedPhotoSearch"/> über Raw-SQL (lesend, ohne Transaktion).</summary>
public sealed class RelatedPhotoSearch : IRelatedPhotoSearch
{
    /// <summary>Höchstens so viele Treffer (Bindungs-Parameter der Galerie-Abfrage).</summary>
    public const int MaxResults = 2000;

    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryScope _scope;
    private readonly IDuplicateSettingsStore? _duplicateSettings;

    public RelatedPhotoSearch(ILibraryContextFactory factory, ILibraryScope? scope = null, IDuplicateSettingsStore? duplicateSettings = null)
    {
        _factory = factory;
        _scope = scope ?? new LibraryScope(factory);
        _duplicateSettings = duplicateSettings;
    }

    public async Task<RelatedPhotos?> SameDayAsync(int fileId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var day = connection.CreateCommand();
            day.CommandText = $"SELECT {LocalDay("f")}, f.CapturedAt FROM Files f WHERE f.Id = $f AND f.CapturedAt IS NOT NULL;";
            day.Parameters.AddWithValue("$f", fileId);
            string date;
            long captured;
            using (var reader = day.ExecuteReader())
            {
                if (!reader.Read() || reader.IsDBNull(0))
                {
                    return null;
                }

                date = reader.GetString(0);
                captured = reader.GetInt64(1);
            }

            // Vorfilter über den Index IX_Files_Library_Captured (COALESCE(CapturedAt, ModifiedAtUtc)): ein Kalendertag in Ortszeit
            // liegt höchstens 24 h + 2 × 14 h Zeitzonen-Abstand um die eigene Aufnahme — erst danach der genaue Tagesvergleich
            const long window = (24 + 28) * 3600L;
            var ids = ReadIds(connection, $"""
                SELECT f.Id FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND f.CapturedAt IS NOT NULL
                  AND COALESCE(f.CapturedAt, f.ModifiedAtUtc) BETWEEN $from AND $to
                  AND {LocalDay("f")} = $day
                ORDER BY f.CapturedAt, f.Id LIMIT {MaxResults};
                """, ("$day", date), ("$from", captured - window), ("$to", captured + window));
            var label = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? $"am {d.ToString("d. MMMM yyyy", CultureInfo.GetCultureInfo("de-DE"))}"
                : $"am {date}";
            return new RelatedPhotos(label, ids);
        }, ct).ConfigureAwait(false);
    }

    public async Task<RelatedPhotos?> SamePlaceAsync(int fileId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var place = connection.CreateCommand();
            place.CommandText = "SELECT PlaceId FROM Files WHERE Id = $f AND PlaceId IS NOT NULL;";
            place.Parameters.AddWithValue("$f", fileId);
            if (place.ExecuteScalar() is not long placeId)
            {
                return null;
            }

            var ids = ReadIds(connection, $"""
                SELECT f.Id FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND f.PlaceId = $p
                ORDER BY f.Id DESC LIMIT {MaxResults};
                """, ("$p", placeId));
            return new RelatedPhotos("am gleichen Ort", ids);
        }, ct).ConfigureAwait(false);
    }

    public async Task<RelatedPhotos?> DuplicatesAsync(int fileId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var threshold = DuplicateSettings.DefaultSimilarThreshold;
        if (_duplicateSettings is not null)
        {
            try
            {
                threshold = (await _duplicateSettings.LoadAsync(ct).ConfigureAwait(false)).SimilarThreshold;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Standard-Schwelle
            }
        }

        return await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            byte[]? content = null;
            long size = 0;
            long? image = null;
            using (var own = connection.CreateCommand())
            {
                own.CommandText = $"""
                    SELECT CASE WHEN {DuplicateSql.ValidContent("h", "f")} THEN h.ContentHash END, f.Size,
                           CASE WHEN {DuplicateSql.ValidImage("h", "f")} THEN h.ImageHash END
                    FROM Files f JOIN FileFingerprints h ON h.FileId = f.Id WHERE f.Id = $f;
                    """;
                own.Parameters.AddWithValue("$f", fileId);
                using var reader = own.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                content = reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0);
                size = reader.GetInt64(1);
                image = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }

            if (content is null && image is null)
            {
                return null;
            }

            var found = new List<int> { fileId };
            if (content is not null)
            {
                found.AddRange(ReadIds(connection, $"""
                    SELECT f.Id FROM Files f JOIN FileFingerprints h ON h.FileId = f.Id
                    WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND f.Size = $size AND h.ContentHash = $hash
                      AND {DuplicateSql.ValidContent("h", "f")}
                    LIMIT {MaxResults};
                    """, ("$size", size), ("$hash", content)));
            }

            if (image is { } ownImage)
            {
                var ownHash = PerceptualHash.FromStored(ownImage);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT f.Id, h.ImageHash FROM Files f JOIN FileFingerprints h ON h.FileId = f.Id
                    WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0 AND {DuplicateSql.ValidImage("h", "f")};
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && found.Count < MaxResults)
                {
                    if (PerceptualHash.Distance(ownHash, PerceptualHash.FromStored(reader.GetInt64(1))) <= threshold)
                    {
                        found.Add(reader.GetInt32(0));
                    }
                }
            }

            return new RelatedPhotos("Doppelte und ähnliche", found.Distinct().ToList());
        }, ct).ConfigureAwait(false);
    }

    public async Task<RelatedPhotos?> SamePersonsAsync(int fileId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            var persons = new List<(int Id, string Name)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT DISTINCT p.Id, p.Name FROM Faces x JOIN Persons p ON p.Id = x.PersonId
                    WHERE x.FileId = $f AND x.State = 2 AND p.GroupId = $g ORDER BY p.Name COLLATE NOCASE;
                    """;
                cmd.Parameters.AddWithValue("$f", fileId);
                cmd.Parameters.AddWithValue("$g", scope.GroupId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    persons.Add((reader.GetInt32(0), reader.GetString(1)));
                }
            }

            if (persons.Count == 0)
            {
                return null;
            }

            var personList = string.Join(",", persons.Select(p => p.Id.ToString(CultureInfo.InvariantCulture)));
            var ids = ReadIds(connection, $"""
                SELECT x.FileId FROM Faces x JOIN Files f ON f.Id = x.FileId
                WHERE x.State = 2 AND x.PersonId IN ({personList}) AND f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.Missing = 0
                GROUP BY x.FileId HAVING COUNT(DISTINCT x.PersonId) = {persons.Count.ToString(CultureInfo.InvariantCulture)}
                ORDER BY x.FileId DESC LIMIT {MaxResults};
                """);
            var names = persons.Count <= 3 ? string.Join(", ", persons.Select(p => p.Name)) : $"{persons.Count} Personen";
            return new RelatedPhotos($"mit {names}", ids);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Kalendertag der Aufnahme in Ortszeit (Unix-Sekunden + Versatz in Minuten) als „yyyy-MM-dd“.</summary>
    private static string LocalDay(string f) => $"date({f}.CapturedAt + COALESCE({f}.CapturedAtOffset, 0) * 60, 'unixepoch')";

    private static List<int> ReadIds(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }
}
