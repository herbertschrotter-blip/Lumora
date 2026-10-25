using System.Drawing;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Enums;
using Lumora.Services.Faces.Recognition;
using Lumora.Services.Library;

namespace Lumora.Services.Faces.Assignment;

/// <summary>Ein Gesicht mit Fingerabdruck und aktuellem Zustand (Eingabe für Vorschläge, Prüfungen, Gruppen).</summary>
internal sealed record StoredFace(
    long FaceId,
    int FileId,
    FaceState State,
    int? PersonId,
    int? SuggestedPersonId,
    float? SuggestionScore,
    FaceSample Sample,
    string ModelVersion);

/// <summary>Neuer Vorschlags-Zustand eines Gesichts (Zustand 1 mit Person/Score oder 0 ohne).</summary>
internal readonly record struct SuggestionWrite(long FaceId, int? PersonId, float? Score)
{
    public FaceState State => PersonId is null ? FaceState.Unconfirmed : FaceState.Suggested;
}

/// <summary>
/// SQL des Zuordnungsdienstes (ADR-0037) auf rohen Verbindungen. Lesen ohne Transaktion (WAL: nie gesperrt), Schreiben
/// immer in der Transaktion des Aufrufers. Bereich (ADR-0048) = Files.LibraryId IN (Bibliotheken des Bereichs); Personen
/// gehören einer Gruppe (Persons.GroupId) — gelesen, gelernt und vorgeschlagen wird nur innerhalb des Bereichs.
/// </summary>
internal static class FaceAssignmentStore
{
    private const int ChunkSize = 500;

    private const string FaceColumns =
        "x.Id, x.FileId, x.State, x.PersonId, x.SuggestedPersonId, x.SuggestionScore, x.Quality, x.Embedding, x.ModelVersion";

    /// <summary>
    /// Modell-Kennung, mit der die meisten Gesichter der Bibliothek analysiert sind. Nur Fingerabdrücke derselben Kennung
    /// sind vergleichbar (ADR-0035); bestätigte Gesichter mit alter Kennung, die ein neues Modell nicht wiederfand, bleiben
    /// stehen, lernen aber nicht mit.
    /// </summary>
    public static string? ResolveModelVersion(SqliteConnection connection, LibraryScopeInfo scope)
    {
        using var cmd = Command(connection, null, $"""
            SELECT x.ModelVersion FROM Faces x JOIN Files f ON f.Id = x.FileId
            WHERE f.LibraryId IN ({scope.SqlLibraryIdList})
            GROUP BY x.ModelVersion ORDER BY COUNT(*) DESC, x.ModelVersion LIMIT 1;
            """);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Bestätigte Gesichter (Zustand 2) der Bibliothek mit der Kennung — die Lernbeispiele.</summary>
    public static List<FaceSample> ReadConfirmedSamples(SqliteConnection connection, LibraryScopeInfo scope, string modelVersion)
    {
        using var cmd = Command(connection, null, $"""
            SELECT {FaceColumns} FROM Faces x JOIN Files f ON f.Id = x.FileId
            WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND x.State = 2 AND x.PersonId IS NOT NULL AND x.ModelVersion = $ver
            ORDER BY x.Id;
            """, ("$ver", modelVersion));
        return ReadFaces(cmd).Select(f => f.Sample).ToList();
    }

    /// <summary>Ablehnungen für Gesichter der Bibliothek.</summary>
    public static List<FaceRejection> ReadRejections(SqliteConnection connection, LibraryScopeInfo scope)
    {
        using var cmd = Command(connection, null, $"""
            SELECT r.FaceId, r.PersonId FROM FaceRejections r
            JOIN Faces x ON x.Id = r.FaceId JOIN Files f ON f.Id = x.FileId
            WHERE f.LibraryId IN ({scope.SqlLibraryIdList});
            """);
        var list = new List<FaceRejection>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FaceRejection(reader.GetInt64(0), reader.GetInt32(1)));
        }

        return list;
    }

    /// <summary>Abgelehnte Personen je Gesicht (für die Vorschläge zwischen zwei Trainings).</summary>
    public static Dictionary<long, List<int>> ReadRejectionsFor(SqliteConnection connection, IEnumerable<long> faceIds)
    {
        var map = new Dictionary<long, List<int>>();
        foreach (var chunk in faceIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = Command(connection, null, $"SELECT FaceId, PersonId FROM FaceRejections WHERE FaceId IN ({IdList(chunk)});");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var faceId = reader.GetInt64(0);
                if (!map.TryGetValue(faceId, out var list))
                {
                    map[faceId] = list = [];
                }

                list.Add(reader.GetInt32(1));
            }
        }

        return map;
    }

    /// <summary>Nächster Stapel offener Gesichter (Zustand 0/1) der Bibliothek mit der Kennung, Id aufsteigend nach <paramref name="afterId"/>.</summary>
    public static List<StoredFace> ReadOpenBatch(SqliteConnection connection, LibraryScopeInfo scope, string modelVersion, long afterId, int limit)
    {
        using var cmd = Command(connection, null, $"""
            SELECT {FaceColumns} FROM Faces x JOIN Files f ON f.Id = x.FileId
            WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND x.State IN (0, 1) AND x.ModelVersion = $ver AND x.Id > $after
            ORDER BY x.Id LIMIT $limit;
            """, ("$ver", modelVersion), ("$after", afterId), ("$limit", limit));
        return ReadFaces(cmd);
    }

    /// <summary>Offene Gesichter (Zustand 0/1) der Dateien mit der Kennung — nur Dateien der Bibliothek des Modells.</summary>
    public static List<StoredFace> ReadOpenForFiles(SqliteConnection connection, LibraryScopeInfo scope, IEnumerable<int> fileIds, string modelVersion)
    {
        var list = new List<StoredFace>();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = Command(connection, null, $"""
                SELECT {FaceColumns} FROM Faces x JOIN Files f ON f.Id = x.FileId
                WHERE x.FileId IN ({IdList(chunk)}) AND f.LibraryId IN ({scope.SqlLibraryIdList}) AND x.State IN (0, 1) AND x.ModelVersion = $ver
                ORDER BY x.Id;
                """, ("$ver", modelVersion));
            list.AddRange(ReadFaces(cmd));
        }

        return list;
    }

    /// <summary>Unbekannte Gesichter (Zustand 0, ohne Vorschlag) für die Gruppierung — neueste zuerst.</summary>
    public static List<StoredFace> ReadUnknownForClustering(SqliteConnection connection, LibraryScopeInfo scope, string modelVersion, float minQuality)
    {
        using var cmd = Command(connection, null, $"""
            SELECT {FaceColumns} FROM Faces x JOIN Files f ON f.Id = x.FileId
            WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND x.State = 0 AND x.ModelVersion = $ver AND x.Quality >= $q
            ORDER BY x.Id DESC;
            """, ("$ver", modelVersion), ("$q", minQuality));
        return ReadFaces(cmd);
    }

    /// <summary>Anzahl unbekannter Gesichter (Zustand 0) der Bibliothek.</summary>
    public static int CountUnknown(SqliteConnection connection, LibraryScopeInfo scope)
        => (int)Scalar(connection, null,
            $"SELECT COUNT(*) FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND x.State = 0;");

    /// <summary>Gesichter nach Id (beliebige Bibliothek), in Id-Reihenfolge.</summary>
    public static List<StoredFace> ReadFaces(SqliteConnection connection, SqliteTransaction? tx, IEnumerable<long> faceIds)
    {
        var list = new List<StoredFace>();
        foreach (var chunk in faceIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = Command(connection, tx, $"SELECT {FaceColumns} FROM Faces x WHERE x.Id IN ({IdList(chunk)}) ORDER BY x.Id;");
            list.AddRange(ReadFaces(cmd));
        }

        return list;
    }

    /// <summary>
    /// Schreibt Vorschläge — nur, wenn das Gesicht noch offen ist (0/1), die Person nicht inzwischen abgelehnt wurde und
    /// sich etwas ändert. Eine Bestätigung oder Ablehnung, die zwischen Lesen und Schreiben kam, gewinnt immer.
    /// </summary>
    public static int WriteSuggestions(SqliteConnection connection, SqliteTransaction tx, IEnumerable<SuggestionWrite> writes)
    {
        using var cmd = Command(connection, tx, """
            UPDATE Faces SET State = $state, SuggestedPersonId = $sp, SuggestionScore = $score
            WHERE Id = $id AND State IN (0, 1)
              AND ($sp IS NULL OR NOT EXISTS (SELECT 1 FROM FaceRejections r WHERE r.FaceId = $id AND r.PersonId = $sp))
              AND ($sp IS NULL OR EXISTS (SELECT 1 FROM Persons p WHERE p.Id = $sp
                                          AND p.GroupId = (SELECT l.GroupId FROM Faces fx JOIN Files fl ON fl.Id = fx.FileId
                                                           JOIN Libraries l ON l.Id = fl.LibraryId WHERE fx.Id = $id)))
              AND (State <> $state OR SuggestedPersonId IS NOT $sp OR SuggestionScore IS NOT $score);
            """);
        var pState = cmd.Parameters.Add("$state", SqliteType.Integer);
        var pPerson = cmd.Parameters.Add("$sp", SqliteType.Integer);
        var pScore = cmd.Parameters.Add("$score", SqliteType.Real);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var changed = 0;
        foreach (var write in writes)
        {
            pState.Value = (int)write.State;
            pPerson.Value = (object?)write.PersonId ?? DBNull.Value;
            pScore.Value = write.Score is { } s ? (double)s : DBNull.Value;
            pId.Value = write.FaceId;
            changed += cmd.ExecuteNonQuery();
        }

        return changed;
    }

    // ---- Personen ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Personen der Gruppe des Bereichs mit Zählern über die Bibliotheken des Bereichs (ADR-0048);
    /// <paramref name="personId"/> = nur diese eine (null, wenn sie zu einer anderen Gruppe gehört).
    /// </summary>
    public static List<PersonOverview> ReadPersons(SqliteConnection connection, LibraryScopeInfo scope, int? personId = null)
    {
        Covers.FaceCoverScoring.Register(connection); // automatisches Titelbild: Gesichts- + Foto-Güte (ADR-0068)
        using var cmd = Command(connection, null, $"""
            SELECT p.Id, p.Name, p.IsHidden,
                (SELECT COUNT(*) FROM Faces x JOIN Files f ON f.Id = x.FileId
                    WHERE x.PersonId = p.Id AND x.State = 2 AND f.LibraryId IN ({scope.SqlLibraryIdList})),
                (SELECT COUNT(DISTINCT x.FileId) FROM Faces x JOIN Files f ON f.Id = x.FileId
                    WHERE x.PersonId = p.Id AND x.State = 2 AND f.LibraryId IN ({scope.SqlLibraryIdList})),
                (SELECT x.Id FROM Faces x JOIN Files f ON f.Id = x.FileId
                    WHERE x.Id = p.CoverFaceId AND x.PersonId = p.Id AND x.State = 2 AND f.LibraryId IN ({scope.SqlLibraryIdList})),
                (SELECT x.Id FROM Faces x JOIN Files f ON f.Id = x.FileId LEFT JOIN FileQuality q ON q.FileId = x.FileId
                    WHERE x.PersonId = p.Id AND x.State = 2 AND f.LibraryId IN ({scope.SqlLibraryIdList})
                    ORDER BY {Covers.FaceCoverScoring.SqlScore("x", "f", "q")} DESC, x.Id LIMIT 1),
                (SELECT COUNT(*) FROM Faces x JOIN Files f ON f.Id = x.FileId
                    WHERE x.SuggestedPersonId = p.Id AND x.State = 1 AND f.LibraryId IN ({scope.SqlLibraryIdList})),
                {PersonDetailsStore.DetailColumns}
            FROM Persons p LEFT JOIN PersonRelations r ON r.Id = p.RelationId
            WHERE p.GroupId = $group AND ($person IS NULL OR p.Id = $person)
            ORDER BY p.Name COLLATE NOCASE, p.Id;
            """, ("$group", scope.GroupId), ("$person", (object?)personId ?? DBNull.Value));
        var list = new List<PersonOverview>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long? cover = reader.IsDBNull(5) ? reader.IsDBNull(6) ? null : reader.GetInt64(6) : reader.GetInt64(5);
            var summary = new PersonSummary(
                reader.GetInt32(0), reader.GetString(1), reader.GetInt32(3), reader.GetInt32(4), cover, reader.GetBoolean(2))
            {
                Details = PersonDetailsStore.ReadDetails(reader, reader.GetInt32(0), 8), // ADR-0045
                CoverIsManual = !reader.IsDBNull(5), // ADR-0068: gültiges selbst gewähltes Gesicht
            };
            list.Add(new PersonOverview(summary, reader.GetInt32(7)));
        }

        return list;
    }

    public static List<PersonFaceInfo> ReadPersonFaces(SqliteConnection connection, LibraryScopeInfo scope, int personId, bool suggested)
    {
        var sql = suggested
            ? $"""
              SELECT x.Id, x.FileId, x.BoxX, x.BoxY, x.BoxW, x.BoxH, x.State, x.Quality, x.SuggestionScore
              FROM Faces x JOIN Files f ON f.Id = x.FileId
              WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND x.State = 1 AND x.SuggestedPersonId = $p
              ORDER BY x.SuggestionScore DESC, x.Id;
              """
            : $"""
              SELECT x.Id, x.FileId, x.BoxX, x.BoxY, x.BoxW, x.BoxH, x.State, x.Quality, x.SuggestionScore
              FROM Faces x JOIN Files f ON f.Id = x.FileId
              WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND x.State = 2 AND x.PersonId = $p
              ORDER BY x.FileId DESC, x.Id;
              """;
        using var cmd = Command(connection, null, sql, ("$p", personId));
        var list = new List<PersonFaceInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PersonFaceInfo(
                reader.GetInt64(0),
                reader.GetInt32(1),
                new RectangleF(reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)),
                (FaceState)reader.GetInt32(6),
                reader.GetFloat(7),
                reader.IsDBNull(8) ? null : reader.GetFloat(8)));
        }

        return list;
    }

    /// <summary>Namen der Personen einer Gruppe (ADR-0048).</summary>
    public static Dictionary<int, string> ReadPersonNames(SqliteConnection connection, int groupId)
    {
        using var cmd = Command(connection, null, "SELECT Id, Name FROM Persons WHERE GroupId = $g;", ("$g", groupId));
        var map = new Dictionary<int, string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt32(0)] = reader.GetString(1);
        }

        return map;
    }

    /// <summary>Person dieses Anzeigenamens in der Gruppe (ohne Groß-/Kleinschreibung) oder null (ADR-0048: je Gruppe eindeutig).</summary>
    public static int? FindPersonByName(SqliteConnection connection, SqliteTransaction? tx, int groupId, string name, int? exceptId = null)
    {
        using var cmd = Command(connection, tx,
            "SELECT Id FROM Persons WHERE GroupId = $g AND Name = $n AND Id <> $except ORDER BY Id LIMIT 1;",
            ("$g", groupId), ("$n", name), ("$except", (object?)exceptId ?? -1));
        return cmd.ExecuteScalar() is long id ? (int)id : null;
    }

    public static bool PersonExists(SqliteConnection connection, SqliteTransaction? tx, int personId)
        => Scalar(connection, tx, "SELECT COUNT(*) FROM Persons WHERE Id = $p;", ("$p", personId)) > 0;

    /// <summary>Gruppe der Person oder null, wenn es sie nicht gibt.</summary>
    public static int? ReadPersonGroup(SqliteConnection connection, SqliteTransaction? tx, int personId)
    {
        using var cmd = Command(connection, tx, "SELECT GroupId FROM Persons WHERE Id = $p;", ("$p", personId));
        return cmd.ExecuteScalar() is long g ? (int)g : null;
    }

    /// <summary>Neue Person aus einem Einzelnamen: Vor-/Nachname am ersten Leerzeichen geteilt (ADR-0045), in der Gruppe.</summary>
    public static int InsertPerson(SqliteConnection connection, SqliteTransaction tx, int groupId, string name)
    {
        var (first, last) = Domain.Entities.PersonNames.Split(name);
        return PersonDetailsStore.InsertPerson(connection, tx, groupId, new PersonDetailsInput(first, last), relationId: null);
    }

    // ---- Dateien und Gesichter für die Oberfläche ----------------------------------------------------------------

    public static Dictionary<int, IReadOnlyList<FacePersonInfo>> ReadFacesForFiles(SqliteConnection connection, IReadOnlyCollection<int> fileIds)
    {
        var map = new Dictionary<int, IReadOnlyList<FacePersonInfo>>();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = Command(connection, null, $"""
                SELECT x.FileId, x.Id, x.BoxX, x.BoxY, x.BoxW, x.BoxH, x.State, x.PersonId, p.Name,
                       x.SuggestedPersonId, s.Name, x.SuggestionScore
                FROM Faces x
                LEFT JOIN Persons p ON p.Id = x.PersonId
                LEFT JOIN Persons s ON s.Id = x.SuggestedPersonId
                WHERE x.FileId IN ({IdList(chunk)})
                ORDER BY x.FileId, x.BoxX, x.Id;
                """);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var fileId = reader.GetInt32(0);
                var state = (FaceState)reader.GetInt32(6);
                var confirmed = state == FaceState.Confirmed && !reader.IsDBNull(7);
                var suggested = state == FaceState.Suggested && !reader.IsDBNull(9);
                var info = new FacePersonInfo(
                    reader.GetInt64(1),
                    new RectangleF(reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)),
                    confirmed ? reader.GetInt32(7) : null,
                    confirmed && !reader.IsDBNull(8) ? reader.GetString(8) : null,
                    state,
                    suggested ? reader.GetInt32(9) : null,
                    suggested && !reader.IsDBNull(10) ? reader.GetString(10) : null,
                    suggested && !reader.IsDBNull(11) ? reader.GetFloat(11) : null);
                if (!map.TryGetValue(fileId, out var list))
                {
                    map[fileId] = list = new List<FacePersonInfo>();
                }

                ((List<FacePersonInfo>)list).Add(info);
            }
        }

        return map;
    }

    public static List<FaceFileInfo> ReadFiles(SqliteConnection connection, IReadOnlyList<int> fileIds)
    {
        var map = new Dictionary<int, FaceFileInfo>();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = Command(connection, null, $"""
                SELECT f.Id, d.Path, f.Name, f.MediaType, f.IsFavorite, f.Rating, f.Size, f.ModifiedAtUtc
                FROM Files f JOIN Folders d ON d.Id = f.FolderId
                WHERE f.Id IN ({IdList(chunk)});
                """);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                map[id] = new FaceFileInfo(
                    id,
                    Path.Combine(reader.GetString(1), reader.GetString(2)),
                    reader.GetString(2),
                    (MediaType)reader.GetInt32(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : (byte)reader.GetInt32(5),
                    reader.GetInt64(6),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)).UtcDateTime);
            }
        }

        var list = new List<FaceFileInfo>(map.Count);
        var seen = new HashSet<int>();
        foreach (var id in fileIds)
        {
            if (seen.Add(id) && map.TryGetValue(id, out var info))
            {
                list.Add(info);
            }
        }

        return list;
    }

    // ---- Hilfen -----------------------------------------------------------------------------------------------------

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    public static string IdList<T>(IEnumerable<T> ids)
        where T : IFormattable
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(id.ToString(null, CultureInfo.InvariantCulture));
        }

        return sb.Length == 0 ? "NULL" : sb.ToString();
    }

    private static List<StoredFace> ReadFaces(SqliteCommand cmd)
    {
        var list = new List<StoredFace>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var faceId = reader.GetInt64(0);
            var personId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
            var state = (FaceState)reader.GetInt32(2);
            var quality = reader.GetFloat(6);
            var blob = reader.IsDBNull(7) ? [] : (byte[])reader.GetValue(7);
            // Ungültige Länge → leerer Fingerabdruck: Lernen, Vorschläge und Gruppen überspringen ihn (ADR-0036).
            float[] embedding = blob.Length == FaceEmbeddingCodec.ByteLength ? FaceEmbeddingCodec.Decode(blob) : [];
            var sample = new FaceSample(faceId, embedding, quality, state == FaceState.Confirmed ? personId : null);
            list.Add(new StoredFace(
                faceId,
                reader.GetInt32(1),
                state,
                personId,
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetFloat(5),
                sample,
                reader.GetString(8)));
        }

        return list;
    }
}
