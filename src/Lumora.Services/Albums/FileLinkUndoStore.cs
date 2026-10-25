using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Lumora.Data;

namespace Lumora.Services.Albums;

/// <summary>
/// Verknüpfungen über das Entfernen einer Files-Zeile hinweg (ADR-0054, ADR-0012). Wer eine Files-Zeile löscht, nimmt
/// per Fremdschlüssel-Kaskade auch ihre Album- und Ordner-Verknüpfungen (seit ADR-0058 auch manuelle Story-Einträge, seit ADR-0071 Fassungen) mit. Datei-Operationen (Papierkorb, aus der
/// Bibliothek heraus verschieben, Reste einer Ordner-Übernahme) sichern sie VORHER mit <see cref="CaptureAsync"/> in
/// <c>FileLinkUndo</c> zur protokollierten <c>FileOperation</c> — mit Pfad, Größe und Änderungszeit der Datei.
/// „Rückgängig“ (neue Files-Zeile) stellt sie über die Operation wieder her; kehrt die Datei sonst zurück (Windows
/// „Wiederherstellen“ + Scan, <see cref="AlbumService"/>), nur, wenn am alten Pfad eine Datei gleicher Größe und
/// Änderungszeit auftaucht — ein neuer Import gleichen Namens erbt nichts. Alle Aufrufe in der Transaktion des Aufrufers.
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

    private const string DeleteSql = "DELETE FROM FileLinkUndo WHERE FileOperationId = $op;";

    /// <summary>Sichert die Verknüpfungen der Datei zur Operation (vor dem Löschen der Files-Zeile); liefert ihre Zahl.</summary>
    public static Task<int> CaptureAsync(LibraryContext ctx, int operationId, int fileId, CancellationToken ct)
        => ctx.Database.ExecuteSqlRawAsync(CaptureSql, [Param("$op", operationId), Param("$file", fileId)], ct);

    /// <summary>Stellt die gesicherten Verknüpfungen auf <paramref name="newFileId"/> wieder her und verwirft die Sicherung.</summary>
    public static async Task<int> RestoreAsync(LibraryContext ctx, int operationId, int newFileId, CancellationToken ct)
    {
        var restored = await ctx.Database.ExecuteSqlRawAsync(RestoreAlbumsSql, [Param("$op", operationId), Param("$file", newFileId)], ct).ConfigureAwait(false);
        restored += await ctx.Database.ExecuteSqlRawAsync(RestoreFoldersSql, [Param("$op", operationId), Param("$file", newFileId)], ct).ConfigureAwait(false);
        restored += await ctx.Database.ExecuteSqlRawAsync(RestoreStoriesSql, [Param("$op", operationId), Param("$file", newFileId)], ct).ConfigureAwait(false); // ADR-0058
        restored += await ctx.Database.ExecuteSqlRawAsync(RestoreVariantsSql, [Param("$op", operationId), Param("$file", newFileId)], ct).ConfigureAwait(false); // ADR-0071
        await ctx.Database.ExecuteSqlRawAsync(DeleteSql, [Param("$op", operationId)], ct).ConfigureAwait(false);
        return restored;
    }

    /// <summary>Verwirft die Sicherung ohne Wiederherstellen — die Verknüpfungen kamen auf anderem Weg zurück (Abzug der Katalogzeile, ADR-0065).</summary>
    public static Task<int> DiscardAsync(LibraryContext ctx, int operationId, CancellationToken ct)
        => ctx.Database.ExecuteSqlRawAsync(DeleteSql, [Param("$op", operationId)], ct);

    /// <summary>Wie <see cref="RestoreAsync(LibraryContext, int, int, CancellationToken)"/> auf einer rohen Verbindung.</summary>
    public static int Restore(SqliteConnection connection, SqliteTransaction tx, int operationId, int newFileId)
    {
        var restored = Execute(connection, tx, RestoreAlbumsSql, operationId, newFileId);
        restored += Execute(connection, tx, RestoreFoldersSql, operationId, newFileId);
        restored += Execute(connection, tx, RestoreStoriesSql, operationId, newFileId); // ADR-0058
        restored += Execute(connection, tx, RestoreVariantsSql, operationId, newFileId); // ADR-0071
        Execute(connection, tx, DeleteSql, operationId, null);
        return restored;
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

    private static int Execute(SqliteConnection connection, SqliteTransaction tx, string sql, int operationId, int? fileId)
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
