using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Lumora.Data;

namespace Lumora.Services.Albums;

/// <summary>
/// Verknüpfungen über das Entfernen einer Files-Zeile hinweg (ADR-0054, ADR-0012). Wer eine Files-Zeile löscht, nimmt
/// per Fremdschlüssel-Kaskade auch ihre Album- und Ordner-Verknüpfungen (seit ADR-0058 auch manuelle Story-Einträge, seit ADR-0071 Fassungen, seit ADR-0078 zusammengefügte Ergebnisse) mit. Datei-Operationen (Papierkorb, aus der
/// Bibliothek heraus verschieben, Reste einer Ordner-Übernahme) sichern sie VORHER mit <see cref="CaptureAsync"/> in
/// <c>FileLinkUndo</c> zur protokollierten <c>FileOperation</c> — mit Pfad, Größe und Änderungszeit der Datei.
/// „Rückgängig“ (neue Files-Zeile) stellt sie über die Operation wieder her; kehrt die Datei sonst zurück (Windows
/// „Wiederherstellen“ + Scan, <see cref="AlbumService"/>), nur, wenn am alten Pfad eine Datei gleicher Größe und
/// Änderungszeit auftaucht — ein neuer Import gleichen Namens erbt nichts. Datei-Verknüpfungen (Fassungen, Zusammenfügen)
/// zu einer Datei, die selbst noch fehlt, bleiben offen und werden nachgeholt, wenn sie zurückkehrt — auch mit neuer Id
/// (0.5.48). Alle Aufrufe in der Transaktion des Aufrufers.
/// </summary>
internal static class FileLinkUndoStore
{
    private const string CaptureSql = """
        INSERT INTO FileLinkUndo (FileOperationId, Kind, TargetId, AddedAtUtc, SourcePath, FileSize, FileModifiedAtUtc)
        SELECT $op, 0, a.AlbumId, a.AddedAtUtc, d.Path || '\' || f.Name, f.Size, f.ModifiedAtUtc
        FROM AlbumItems a JOIN Files f ON f.Id = a.FileId JOIN Folders d ON d.Id = f.FolderId WHERE a.FileId = $file
        UNION ALL
        SELECT $op, 1, l.FolderId, l.AddedAtUtc, d.Path || '\' || f.Name, f.Size, f.ModifiedAtUtc
        FROM FolderLinks l JOIN Files f ON f.Id = l.FileId JOIN Folders d ON d.Id = f.FolderId WHERE l.FileId = $file
        UNION ALL -- Storys (ADR-0058): nur manuelle Einträge (hinzugefügt = 2, Ausschluss = 3); automatische berechnet die Erkennung neu
        SELECT $op, CASE WHEN s.Origin = 1 THEN 2 ELSE 3 END, s.StoryId, s.AddedAtUtc, d.Path || '\' || f.Name, f.Size, f.ModifiedAtUtc
        FROM StoryItems s JOIN Files f ON f.Id = s.FileId JOIN Folders d ON d.Id = f.FolderId WHERE s.FileId = $file AND s.Origin <> 0
        UNION ALL -- Fassungen (ADR-0071): diese Datei als Fassung (4, Ziel = Original) bzw. als Original (5, Ziel = Fassung)
        SELECT $op, 4, v.OriginalFileId, v.CreatedAtUtc, d.Path || '\' || f.Name, f.Size, f.ModifiedAtUtc
        FROM PhotoVariants v JOIN Files f ON f.Id = v.FileId JOIN Folders d ON d.Id = f.FolderId WHERE v.FileId = $file
        UNION ALL
        SELECT $op, 5, v.FileId, v.CreatedAtUtc, d.Path || '\' || f.Name, f.Size, f.ModifiedAtUtc
        FROM PhotoVariants v JOIN Files f ON f.Id = v.OriginalFileId JOIN Folders d ON d.Id = f.FolderId WHERE v.OriginalFileId = $file;
        -- Zusammenfügen (ADR-0078): diese Datei als Ergebnis (6, Ziel = Quelle; in Positionsreihenfolge, die Id-Folge trägt die
        -- Position) bzw. als Quelle (7, Ziel = Ergebnis)
        INSERT INTO FileLinkUndo (FileOperationId, Kind, TargetId, AddedAtUtc, SourcePath, FileSize, FileModifiedAtUtc)
        SELECT $op, 6, s.SourceFileId, m.CreatedAtUtc, d.Path || '\' || f.Name, f.Size, f.ModifiedAtUtc
        FROM MergedPhotos m JOIN MergedPhotoSources s ON s.ResultFileId = m.FileId JOIN Files f ON f.Id = m.FileId JOIN Folders d ON d.Id = f.FolderId
        WHERE m.FileId = $file ORDER BY s.Position;
        INSERT INTO FileLinkUndo (FileOperationId, Kind, TargetId, AddedAtUtc, SourcePath, FileSize, FileModifiedAtUtc)
        SELECT $op, 7, s.ResultFileId, m.CreatedAtUtc, d.Path || '\' || f.Name, f.Size, f.ModifiedAtUtc
        FROM MergedPhotoSources s JOIN MergedPhotos m ON m.FileId = s.ResultFileId JOIN Files f ON f.Id = s.SourceFileId JOIN Folders d ON d.Id = f.FolderId
        WHERE s.SourceFileId = $file;
        -- … und die Ergebnis-Zeile selbst (Verfahren, Aufnahmeart, Rezept)
        INSERT OR REPLACE INTO MergedPhotoUndo (FileOperationId, Method, Projection, SourceCount, SeriesId, Tools, RecipeJson, CreatedAtUtc, BatchId)
        SELECT $op, Method, Projection, SourceCount, SeriesId, Tools, RecipeJson, CreatedAtUtc, BatchId FROM MergedPhotos WHERE FileId = $file;
        """;

    // Zusammenfügen (ADR-0078): ein zurückkehrendes Ergebnis bekommt seine gesicherte MergedPhotos-Zeile zurück (Verfahren,
    // Aufnahmeart, Rezept; Serie und Batch wie gesichert) und seine noch vorhandenen Quellen derselben Bibliothek in alter
    // Reihenfolge — nur, wenn noch mindestens eine Quelle da ist oder eine entfernte Quelle ihre Verknüpfung noch offen hält
    // (Art 7 einer anderen Operation, 0.5.48: Quelle und Ergebnis kehren einzeln zurück); eine zurückkehrende Quelle hängt sich
    // hinten an ihr vorhandenes Ergebnis. Fehlt die gesicherte Zeile (Sicherung älter als MergedPhotoUndo), Panorama ohne Rezept.
    private const string RestoreMergesSql = """
        INSERT OR IGNORE INTO MergedPhotos (FileId, Method, Projection, SourceCount, SeriesId, Tools, RecipeJson, CreatedAtUtc, BatchId)
        SELECT $file, COALESCE(g.Method, 0), g.Projection, COALESCE(g.SourceCount, c.cnt), g.SeriesId, COALESCE(g.Tools, 'restored'),
               COALESCE(g.RecipeJson, char(123) || char(125)), COALESCE(g.CreatedAtUtc, c.t), COALESCE(g.BatchId, 0) -- leeres JSON ohne geschweifte Klammern im Text
        FROM (SELECT COUNT(*) AS cnt, MIN(u.AddedAtUtc) AS t FROM FileLinkUndo u
              WHERE u.FileOperationId = $op AND u.Kind = 6 AND u.TargetId <> $file
                AND EXISTS (SELECT 1 FROM Files s JOIN Files n ON n.LibraryId = s.LibraryId WHERE s.Id = u.TargetId AND n.Id = $file)) c
        LEFT JOIN MergedPhotoUndo g ON g.FileOperationId = $op
        WHERE c.cnt > 0
           OR (g.FileOperationId IS NOT NULL AND EXISTS (SELECT 1 FROM FileLinkUndo k WHERE k.Kind = 7 AND k.TargetId = $file AND k.FileOperationId <> $op));
        INSERT OR IGNORE INTO MergedPhotoSources (ResultFileId, SourceFileId, Position)
        SELECT $file, u.TargetId, (SELECT COUNT(*) FROM FileLinkUndo p WHERE p.FileOperationId = $op AND p.Kind = 6 AND p.Id < u.Id)
        FROM FileLinkUndo u
        WHERE u.FileOperationId = $op AND u.Kind = 6 AND u.TargetId <> $file
          AND EXISTS (SELECT 1 FROM MergedPhotos m WHERE m.FileId = $file)
          AND EXISTS (SELECT 1 FROM Files s JOIN Files n ON n.LibraryId = s.LibraryId WHERE s.Id = u.TargetId AND n.Id = $file);
        INSERT OR IGNORE INTO MergedPhotoSources (ResultFileId, SourceFileId, Position)
        SELECT u.TargetId, $file, COALESCE((SELECT MAX(x.Position) + 1 FROM MergedPhotoSources x WHERE x.ResultFileId = u.TargetId), 0)
        FROM FileLinkUndo u
        WHERE u.FileOperationId = $op AND u.Kind = 7 AND u.TargetId <> $file
          AND EXISTS (SELECT 1 FROM MergedPhotos m JOIN Files r ON r.Id = m.FileId JOIN Files n ON n.LibraryId = r.LibraryId WHERE m.FileId = u.TargetId AND n.Id = $file);
        """;

    // Fassungen (ADR-0071): nur, wenn die andere Datei noch existiert und in derselben Bibliothek liegt; die Werkzeuge sind
    // nicht gesichert („restored“), der Zeitpunkt schon.
    private const string RestoreVariantsSql = """
        INSERT OR IGNORE INTO PhotoVariants (FileId, OriginalFileId, Tools, CreatedAtUtc, BatchId)
        SELECT $file, u.TargetId, 'restored', u.AddedAtUtc, 0 FROM FileLinkUndo u
        WHERE u.FileOperationId = $op AND u.Kind = 4 AND u.TargetId <> $file
          AND EXISTS (SELECT 1 FROM Files o JOIN Files n ON n.LibraryId = o.LibraryId WHERE o.Id = u.TargetId AND n.Id = $file);
        INSERT OR IGNORE INTO PhotoVariants (FileId, OriginalFileId, Tools, CreatedAtUtc, BatchId)
        SELECT u.TargetId, $file, 'restored', u.AddedAtUtc, 0 FROM FileLinkUndo u
        WHERE u.FileOperationId = $op AND u.Kind = 5 AND u.TargetId <> $file
          AND EXISTS (SELECT 1 FROM Files v JOIN Files n ON n.LibraryId = v.LibraryId WHERE v.Id = u.TargetId AND n.Id = $file);
        """;

    // Album nur, wenn es noch existiert und zur Gruppe der Bibliothek der (neuen) Datei gehört (ADR-0048).
    private const string RestoreAlbumsSql = """
        INSERT OR IGNORE INTO AlbumItems (AlbumId, FileId, AddedAtUtc)
        SELECT u.TargetId, $file, u.AddedAtUtc FROM FileLinkUndo u
        WHERE u.FileOperationId = $op AND u.Kind = 0
          AND EXISTS (SELECT 1 FROM Albums a
                      JOIN Libraries l ON l.GroupId = a.GroupId
                      JOIN Files f ON f.LibraryId = l.Id
                      WHERE a.Id = u.TargetId AND a.Kind = 0 AND f.Id = $file);
        """;

    // Ordner nur in derselben Bibliothek und nicht der Ordner, in dem die Datei jetzt physisch liegt.
    private const string RestoreFoldersSql = """
        INSERT OR IGNORE INTO FolderLinks (FolderId, FileId, AddedAtUtc)
        SELECT u.TargetId, $file, u.AddedAtUtc FROM FileLinkUndo u
        WHERE u.FileOperationId = $op AND u.Kind = 1
          AND EXISTS (SELECT 1 FROM Folders d JOIN Files f ON f.LibraryId = d.LibraryId
                      WHERE d.Id = u.TargetId AND f.Id = $file AND f.FolderId <> d.Id);
        """;

    // Storys (ADR-0058): manueller Eintrag nur, wenn die Story noch existiert und zur Gruppe der Bibliothek der Datei gehört.
    // Tagesabschnitt = Kalendertag in Ortszeit (Zeitversatz der Datei, sonst Zeitzone des Rechners wie StoryClock), Zähler
    // und Zeitraum der Story gleich mitziehen (wie StorySql.RefreshAggregates).
    private const string RestoreStoriesSql = """
        INSERT OR IGNORE INTO StoryItems (StoryId, FileId, Day, Origin, AddedAtUtc)
        SELECT u.TargetId, f.Id, (l.Local - ((l.Local % 86400) + 86400) % 86400) / 86400, CASE u.Kind WHEN 2 THEN 1 ELSE -1 END, u.AddedAtUtc
        FROM FileLinkUndo u
        CROSS JOIN Files f ON f.Id = $file
        CROSS JOIN (SELECT COALESCE(COALESCE(x.CapturedAt, x.ModifiedAtUtc) + x.CapturedAtOffset * 60,
                                    CAST(strftime('%s', COALESCE(x.CapturedAt, x.ModifiedAtUtc), 'unixepoch', 'localtime') AS INTEGER)) AS Local
                    FROM Files x WHERE x.Id = $file) l
        WHERE u.FileOperationId = $op AND u.Kind IN (2, 3)
          AND EXISTS (SELECT 1 FROM Stories st JOIN Libraries lib ON lib.GroupId = st.GroupId WHERE st.Id = u.TargetId AND lib.Id = f.LibraryId);
        UPDATE Stories SET FileCount = a.c, VideoCount = a.v, StartAt = a.s, EndAt = a.e, FirstDay = a.fd, LastDay = a.ld
        FROM (SELECT si.StoryId AS Id, COUNT(*) AS c, SUM(x.MediaType = 1) AS v, MIN(COALESCE(x.CapturedAt, x.ModifiedAtUtc)) AS s,
                     MAX(COALESCE(x.CapturedAt, x.ModifiedAtUtc)) AS e, MIN(si.Day) AS fd, MAX(si.Day) AS ld
              FROM StoryItems si JOIN Files x ON x.Id = si.FileId
              WHERE si.Origin >= 0 AND si.StoryId IN (SELECT TargetId FROM FileLinkUndo WHERE FileOperationId = $op AND Kind IN (2, 3))
              GROUP BY si.StoryId) a
        WHERE Stories.Id = a.Id;
        """;

    // Kehrt eine Datei mit NEUER Id zurück (Windows „Wiederherstellen“ + Scan, Rückgängig eines Verschiebens nach außen), zeigen
    // offene Datei-Verknüpfungen anderer Operationen (Art 4–7) noch auf ihre alte Id (= FileOperations.FileId): auf die neue
    // umschreiben (ADR-0078, 0.5.48). Files-Ids werden nie neu vergeben (AUTOINCREMENT) — die alte zeigt auf keine fremde Datei.
    private const string RewriteTargetsSql = """
        UPDATE FileLinkUndo SET TargetId = $file
        WHERE Kind BETWEEN 4 AND 7 AND FileOperationId <> $op
          AND TargetId = (SELECT o.FileId FROM FileOperations o WHERE o.Id = $op AND o.FileId IS NOT NULL AND o.FileId <> $file);
        """;

    // Erledigt bzw. nicht mehr herstellbar ist alles außer Datei-Verknüpfungen (Art 4–7) zu einer Datei, die (noch) fehlt: kehrt
    // sie zurück, holt Restore die Verknüpfung nach (0.5.48). Die Quellen eines Ergebnisses (Art 6) bleiben beisammen, solange eine
    // fehlt — ihre Id-Folge trägt die Position; mit ihnen die gesicherte MergedPhotos-Zeile.
    private const string DeleteSettledSql = """
        DELETE FROM FileLinkUndo WHERE FileOperationId = $op
          AND NOT (Kind IN (4, 5, 7) AND NOT EXISTS (SELECT 1 FROM Files t WHERE t.Id = FileLinkUndo.TargetId))
          AND NOT (Kind = 6 AND EXISTS (SELECT 1 FROM FileLinkUndo k WHERE k.FileOperationId = $op AND k.Kind = 6
                                         AND NOT EXISTS (SELECT 1 FROM Files t WHERE t.Id = k.TargetId)));
        DELETE FROM MergedPhotoUndo WHERE FileOperationId = $op
          AND NOT EXISTS (SELECT 1 FROM FileLinkUndo k WHERE k.FileOperationId = $op AND k.Kind = 6);
        """;

    // Andere Operationen, deren offene Datei-Verknüpfung auf die eben zurückgekehrte Datei zeigt — mit dem, woran ihre eigene
    // Datei zu erkennen ist (alte Id; Pfad, Größe, Änderungszeit wie beim Abgleich in AlbumService).
    private const string WaitingSql = """
        SELECT DISTINCT u.FileOperationId, o.FileId, u.SourcePath, u.FileSize, u.FileModifiedAtUtc
        FROM FileLinkUndo u JOIN FileOperations o ON o.Id = u.FileOperationId
        WHERE u.Kind BETWEEN 4 AND 7 AND u.TargetId = $file AND u.FileOperationId <> $op
        ORDER BY u.FileOperationId;
        """;

    private const string FileByIdentitySql = """
        SELECT f.Id FROM Files f JOIN Folders d ON d.Id = f.FolderId
        WHERE f.Size = $size AND f.ModifiedAtUtc = $modified AND (d.Path || '\' || f.Name) = $path COLLATE NOCASE
        ORDER BY f.Id DESC LIMIT 1;
        """;

    /// <summary>Sichert die Verknüpfungen der Datei zur Operation (vor dem Löschen der Files-Zeile); liefert ihre Zahl.</summary>
    public static Task<int> CaptureAsync(LibraryContext ctx, int operationId, int fileId, CancellationToken ct)
        => ctx.Database.ExecuteSqlRawAsync(CaptureSql, [Param("$op", operationId), Param("$file", fileId)], ct);

    /// <summary>
    /// Stellt die gesicherten Verknüpfungen auf <paramref name="newFileId"/> wieder her (innerhalb der laufenden Transaktion des
    /// Contexts, falls eine läuft) und verwirft, was erledigt ist — siehe <see cref="Restore"/>.
    /// </summary>
    public static async Task<int> RestoreAsync(LibraryContext ctx, int operationId, int newFileId, CancellationToken ct)
    {
        await ctx.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqliteConnection)ctx.Database.GetDbConnection();
            var tx = ctx.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;
            return Restore(connection, tx, operationId, newFileId);
        }
        finally
        {
            await ctx.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stellt die gesicherten Verknüpfungen auf <paramref name="newFileId"/> wieder her. Kehrt die Datei mit neuer Id zurück,
    /// zeigen offene Datei-Verknüpfungen anderer Operationen danach auf die neue Id; Verknüpfungen zu Dateien, die selbst noch
    /// fehlen (Ergebnis und Quellen gemeinsam im Papierkorb), bleiben offen. Wartet eine schon zurückgekehrte Datei auf diese,
    /// wird ihre Verknüpfung jetzt nachgeholt — so ist die Reihenfolge der Rückkehr egal (ADR-0078, 0.5.48; gilt auch für
    /// Fassungen, ADR-0071). Liefert die Zahl der wiederhergestellten Verknüpfungen.
    /// </summary>
    public static int Restore(SqliteConnection connection, SqliteTransaction? tx, int operationId, int newFileId)
    {
        Execute(connection, tx, RewriteTargetsSql, operationId, newFileId);
        var restored = RestoreLinks(connection, tx, operationId, newFileId);
        Execute(connection, tx, DeleteSettledSql, operationId, newFileId);
        foreach (var (waitingOperation, waitingFileId) in Waiting(connection, tx, operationId, newFileId))
        {
            restored += RestoreLinks(connection, tx, waitingOperation, waitingFileId);
            Execute(connection, tx, DeleteSettledSql, waitingOperation, waitingFileId);
        }

        return restored;
    }

    private static int RestoreLinks(SqliteConnection connection, SqliteTransaction? tx, int operationId, int fileId)
    {
        var restored = Execute(connection, tx, RestoreAlbumsSql, operationId, fileId);
        restored += Execute(connection, tx, RestoreFoldersSql, operationId, fileId);
        restored += Execute(connection, tx, RestoreStoriesSql, operationId, fileId); // ADR-0058
        restored += Execute(connection, tx, RestoreVariantsSql, operationId, fileId); // ADR-0071
        restored += Execute(connection, tx, RestoreMergesSql, operationId, fileId); // ADR-0078
        return restored;
    }

    /// <summary>Operationen mit offener Verknüpfung auf <paramref name="fileId"/>, deren eigene Datei schon zurück ist (Operation, ihre FileId).</summary>
    private static List<(int OperationId, int FileId)> Waiting(SqliteConnection connection, SqliteTransaction? tx, int operationId, int fileId)
    {
        var candidates = new List<(int Op, long? OldId, string Path, long Size, long Modified)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = WaitingSql;
            cmd.Parameters.AddWithValue("$op", operationId);
            cmd.Parameters.AddWithValue("$file", fileId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
        }

        var list = new List<(int, int)>();
        foreach (var (op, oldId, path, size, modified) in candidates)
        {
            // mit alter Id zurück (Abzug der Katalogzeile), sonst über Pfad, Größe und Änderungszeit
            var current = oldId is { } id && Scalar(connection, tx, "SELECT Id FROM Files WHERE Id = $id;", ("$id", id)) is long same
                ? same
                : Scalar(connection, tx, FileByIdentitySql, ("$path", path), ("$size", size), ("$modified", modified)) as long?;
            if (current is { } found && found != fileId && !list.Any(x => x.Item1 == op))
            {
                list.Add((op, (int)found));
            }
        }

        return list;
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    /// <summary>Gesicherte Operationen mit Pfad, Größe und Änderungszeit (Unix-Sekunden) der entfernten Datei.</summary>
    public static List<(int OperationId, string SourcePath, long Size, long ModifiedUnix)> ReadPending(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FileOperationId, SourcePath, FileSize, FileModifiedAtUtc FROM FileLinkUndo GROUP BY FileOperationId, SourcePath ORDER BY FileOperationId;";
        var list = new List<(int, string, long, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        return list;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, int operationId, int? fileId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$op", operationId);
        if (fileId is { } id)
        {
            cmd.Parameters.AddWithValue("$file", id);
        }

        return cmd.ExecuteNonQuery();
    }

    private static SqliteParameter Param(string name, object value) => new(name, value);
}
