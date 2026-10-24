using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lumora.Services.Duplicates;

namespace Lumora.Services.Operations;

/// <summary>
/// Rückgängig-Daten für „Ordner in den Papierkorb“ und „Fehlende entfernen“ (ADR-0065). Ohne neue Tabelle: die Abzüge
/// liegen in <c>DuplicateCleanupUndo</c> (ADR-0053), das genau diese Form hat (Batch, FileId, Abzug als JSON) — mit
/// <c>KeptFileId = 0</c> als Kennzeichen „kein behaltenes Gegenstück“, das die Seite „Doppelte“ ausblendet.
/// <list type="bullet">
/// <item>Je entfernter Datei eine Zeile mit dem <see cref="CatalogRowSnapshot"/> (Files-Zeile + alle Kaskaden-Kinder:
/// Tags, Kategorien, Gesichter, Motive, Alben, Ordner-Verknüpfungen, Story-Einträge …).</item>
/// <item>Höchstens eine Zeile mit <c>FileId = 0</c>: Abzug der entfernten <c>Folders</c>-Zeilen samt der Verknüpfungen
/// fremder Dateien in diese Ordner (<c>FolderLinks</c>) — damit „Rückgängig“ die Ordner mit denselben Ids zurückbringt.</item>
/// </list>
/// Alle Aufrufe auf der Verbindung und in der Transaktion des Aufrufers.
/// </summary>
internal static class RemovalUndoStore
{
    /// <summary>Kennzeichen in <c>DuplicateCleanupUndo.KeptFileId</c>.</summary>
    public const int NoKeptFile = 0;

    /// <summary><c>FileId</c> der Zeile mit dem Ordner-Abzug.</summary>
    public const int FolderRowsFileId = 0;

    /// <summary>Abzug einer Datei sichern (vor dem Löschen ihrer Files-Zeile).</summary>
    public static void CaptureFile(SqliteConnection connection, SqliteTransaction tx, int batchId, int fileId)
        => Insert(connection, tx, batchId, fileId, CatalogRowSnapshot.Capture(connection, tx, fileId));

    /// <summary>Folders-Zeilen und die Verknüpfungen hinein sichern (vor dem Löschen der Zeilen); nichts bei leerer Liste.</summary>
    public static void CaptureFolders(SqliteConnection connection, SqliteTransaction tx, int batchId, IReadOnlyCollection<int> folderIds)
    {
        if (folderIds.Count == 0)
        {
            return;
        }

        var ids = folderIds.Select(i => (long)i).ToList();
        var folders = CatalogRowSnapshot.ReadRows(connection, tx, "Folders", "Id", ids);
        var links = CatalogRowSnapshot.ReadRows(connection, tx, "FolderLinks", "FolderId", ids);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("folders");
            foreach (var row in folders)
            {
                CatalogRowSnapshot.WriteRow(writer, row);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("links");
            foreach (var row in links)
            {
                CatalogRowSnapshot.WriteRow(writer, row);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Insert(connection, tx, batchId, FolderRowsFileId, Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Offene Abzüge eines Batches: je Datei (FileId → Zeilen-Id + Abzug) und der Ordner-Abzug (oder null).</summary>
    public static (Dictionary<int, (int RowId, string Snapshot)> Files, (int RowId, string Json)? Folders) Load(SqliteConnection connection, SqliteTransaction? tx, int batchId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, FileId, Snapshot FROM DuplicateCleanupUndo WHERE BatchId = $b AND KeptFileId = 0 AND UndoneAtUtc IS NULL ORDER BY Id;";
        cmd.Parameters.AddWithValue("$b", batchId);
        var files = new Dictionary<int, (int, string)>();
        (int, string)? folders = null;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rowId = reader.GetInt32(0);
            var fileId = reader.GetInt32(1);
            var snapshot = reader.GetString(2);
            if (fileId == FolderRowsFileId)
            {
                folders = (rowId, snapshot);
            }
            else
            {
                files[fileId] = (rowId, snapshot);
            }
        }

        return (files, folders);
    }

    /// <summary>
    /// Folders-Zeilen mit ihren alten Ids zurückschreiben (Eltern vor Kindern); Zeilen, deren Id oder Pfad inzwischen belegt
    /// ist, werden übersprungen. Liefert die Zahl geschriebener Zeilen.
    /// </summary>
    public static int RestoreFolders(SqliteConnection connection, SqliteTransaction tx, string json)
    {
        var (folders, _) = Parse(json);
        var restored = 0;
        foreach (var row in folders.OrderBy(r => (r["Path"] as string ?? string.Empty).Length))
        {
            if (CatalogRowSnapshot.TryInsert(connection, tx, "Folders", row))
            {
                restored++;
            }
        }

        return restored;
    }

    /// <summary>Verknüpfungen fremder Dateien in die zurückgeholten Ordner (nach den Dateien); Konflikte und fehlende Bezüge werden übersprungen.</summary>
    public static int RestoreFolderLinks(SqliteConnection connection, SqliteTransaction tx, string json)
    {
        var (_, links) = Parse(json);
        return links.Count(row => CatalogRowSnapshot.TryInsert(connection, tx, "FolderLinks", row));
    }

    /// <summary>Abzüge als erledigt markieren (Rückgängig ausgeführt).</summary>
    public static void MarkUndone(SqliteConnection connection, SqliteTransaction? tx, IReadOnlyCollection<int> rowIds)
    {
        foreach (var chunk in rowIds.Chunk(500))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE DuplicateCleanupUndo SET UndoneAtUtc = $now WHERE Id IN ({string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)))});";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction tx, int batchId, int fileId, string snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO DuplicateCleanupUndo (BatchId, FileId, KeptFileId, Snapshot, Transfer, CreatedAtUtc, UndoneAtUtc)
            VALUES ($b, $f, 0, $s, NULL, $now, NULL);
            """;
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$f", fileId);
        cmd.Parameters.AddWithValue("$s", snapshot);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    private static (List<IReadOnlyDictionary<string, object?>> Folders, List<IReadOnlyDictionary<string, object?>> Links) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.GetProperty("folders").EnumerateArray().Select(CatalogRowSnapshot.ReadRow).ToList(),
            root.GetProperty("links").EnumerateArray().Select(CatalogRowSnapshot.ReadRow).ToList());
    }
}
