using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lumora.Services.Duplicates;

/// <summary>
/// Abzug einer Files-Zeile mit allen abhängigen Katalogzeilen (ADR-0053) — damit „Rückgängig“ nach „Übrige in den
/// Papierkorb“ die Datei mit derselben FileId und all ihren Angaben zurückbringt. Welche Tabellen dazugehören, liest der
/// Abzug aus dem Schema: alle Tabellen mit einem Fremdschlüssel auf <c>Files.Id</c> und ON DELETE CASCADE (Tags,
/// Kategorien, Gesichter, Gesichts-Merker, Motiv-Vektoren und -Treffer, Fingerabdrücke … und künftige wie Alben), dazu
/// eine Ebene tiefer, was per Kaskade an deren <c>Id</c> hängt (abgelehnte Vorschläge an Gesichtern). Ausgenommen sind
/// die Vorschaubild-Zeilen (die Cache-Datei ist mit der Datei gelöscht worden und entsteht neu). Ids bleiben gleich — die
/// Tabellen sind AUTOINCREMENT, gelöschte Ids werden nie neu vergeben. Zeilen, deren Bezug inzwischen fehlt (Tag gelöscht),
/// werden beim Zurückschreiben einzeln übersprungen.
/// </summary>
public static class CatalogRowSnapshot
{
    /// <summary>Tabellen, die nie mitgenommen werden.</summary>
    private static readonly HashSet<string> s_excluded = new(StringComparer.OrdinalIgnoreCase) { "Thumbnails", "DuplicateCleanupUndo" };

    /// <summary>Abzug als JSON (innerhalb einer laufenden Transaktion oder ohne).</summary>
    public static string Capture(SqliteConnection connection, SqliteTransaction? tx, int fileId)
    {
        var file = ReadRows(connection, tx, "Files", "Id", [fileId]).SingleOrDefault()
                   ?? throw new InvalidOperationException($"Datei {fileId} nicht im Katalog.");
        var folderPath = Scalar(connection, tx, "SELECT Path FROM Folders WHERE Id = $id", ToLong(file["FolderId"])) as string ?? string.Empty;

        var tables = new List<(string Table, List<Dictionary<string, object?>> Rows)>();
        foreach (var (table, column) in Children(connection, tx, "Files"))
        {
            var rows = ReadRows(connection, tx, table, column, [fileId]);
            if (rows.Count == 0)
            {
                continue;
            }

            tables.Add((table, rows));
            if (!rows[0].ContainsKey("Id"))
            {
                continue;
            }

            var ids = rows.Select(r => ToLong(r["Id"])).ToList();
            foreach (var (grandChild, grandColumn) in Children(connection, tx, table))
            {
                var nested = ReadRows(connection, tx, grandChild, grandColumn, ids);
                if (nested.Count > 0)
                {
                    tables.Add((grandChild, nested));
                }
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("folderPath", folderPath);
            writer.WritePropertyName("file");
            WriteRow(writer, file);
            writer.WriteStartArray("tables");
            foreach (var (table, rows) in tables)
            {
                writer.WriteStartObject();
                writer.WriteString("table", table);
                writer.WriteStartArray("rows");
                foreach (var row in rows)
                {
                    WriteRow(writer, row);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Gelesener Abzug.</summary>
    public sealed record Snapshot(
        string FolderPath,
        IReadOnlyDictionary<string, object?> File,
        IReadOnlyList<(string Table, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)> Tables)
    {
        public int FileId => (int)ToLong(File["Id"]);

        public int LibraryId => (int)ToLong(File["LibraryId"]);

        public string Name => (string)File["Name"]!;

        public string Extension => (string)File["Extension"]!;
    }

    public static Snapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tables = new List<(string, IReadOnlyList<IReadOnlyDictionary<string, object?>>)>();
        foreach (var t in root.GetProperty("tables").EnumerateArray())
        {
            tables.Add((t.GetProperty("table").GetString()!, t.GetProperty("rows").EnumerateArray().Select(ReadRow).ToList()));
        }

        return new Snapshot(root.GetProperty("folderPath").GetString() ?? string.Empty, ReadRow(root.GetProperty("file")), tables);
    }

    /// <summary>
    /// Files-Zeile mit der alten Id zurückschreiben (Ordner, Größe und Änderungszeit von jetzt); false, wenn die Id belegt ist
    /// oder die Bibliothek fehlt — dann legt der Aufrufer eine neue Zeile an. Ohne FTS (macht der Aufrufer).
    /// </summary>
    public static bool RestoreFileRow(SqliteConnection connection, SqliteTransaction tx, Snapshot snapshot, int folderId, long size, long modifiedUnix)
    {
        if (Scalar(connection, tx, "SELECT 1 FROM Files WHERE Id = $id", snapshot.FileId) is not null
            || Scalar(connection, tx, "SELECT 1 FROM Libraries WHERE Id = $id", snapshot.LibraryId) is null)
        {
            return false;
        }

        var row = snapshot.File.ToDictionary(kv => kv.Key, kv => kv.Value);
        row["FolderId"] = (long)folderId;
        row["Size"] = size;
        row["ModifiedAtUtc"] = modifiedUnix;
        row["Missing"] = 0L;
        return TryInsert(connection, tx, "Files", row);
    }

    /// <summary>
    /// Files-Zeile mit der alten Id und allen erfassten Werten zurückschreiben, nur Ordner und Missing von jetzt (ADR-0065:
    /// „Fehlende entfernen“ bzw. „Ordner in den Papierkorb“ rückgängig — die Datei fehlt evtl. weiterhin, Größe und
    /// Änderungszeit bleiben die erfassten); false, wenn die Id oder der Platz (Ordner + Name) belegt ist oder die
    /// Bibliothek fehlt. Ohne FTS (macht der Aufrufer).
    /// </summary>
    public static bool RestoreFileRowAsCaptured(SqliteConnection connection, SqliteTransaction tx, Snapshot snapshot, int folderId, bool missing)
    {
        if (Scalar(connection, tx, "SELECT 1 FROM Files WHERE Id = $id", snapshot.FileId) is not null
            || Scalar(connection, tx, "SELECT 1 FROM Libraries WHERE Id = $id", snapshot.LibraryId) is null)
        {
            return false;
        }

        var row = snapshot.File.ToDictionary(kv => kv.Key, kv => kv.Value);
        row["FolderId"] = (long)folderId;
        row["Missing"] = missing ? 1L : 0L;
        return TryInsert(connection, tx, "Files", row);
    }

    /// <summary>Abhängige Zeilen zurückschreiben; liefert (geschrieben, übersprungen).</summary>
    public static (int Restored, int Skipped) RestoreDependents(SqliteConnection connection, SqliteTransaction tx, Snapshot snapshot)
    {
        int restored = 0, skipped = 0;
        var own = snapshot.Tables.Select(t => t.Table).Append("Files").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, rows) in snapshot.Tables)
        {
            List<string>? optional = null;
            foreach (var row in rows)
            {
                if (TryInsert(connection, tx, table, row))
                {
                    restored++;
                    continue;
                }

                // Bezug inzwischen weg (z. B. Person gelöscht/zusammengeführt): mit leerem Verweis statt gar nicht —
                // ein Gesicht kommt dann ohne Person zurück, nicht als fehlendes Gesicht.
                optional ??= OptionalForeignKeys(connection, tx, table, own);
                if (optional.Count > 0 && optional.Any(c => row.TryGetValue(c, out var v) && v is not null))
                {
                    var relaxed = row.ToDictionary(kv => kv.Key, kv => optional.Contains(kv.Key) ? null : kv.Value);
                    if (TryInsert(connection, tx, table, relaxed))
                    {
                        restored++;
                        continue;
                    }
                }

                skipped++;
            }
        }

        return (restored, skipped);
    }

    /// <summary>Spalten mit Fremdschlüssel auf Tabellen außerhalb des Abzugs, die NULL sein dürfen.</summary>
    private static List<string> OptionalForeignKeys(SqliteConnection connection, SqliteTransaction tx, string table, HashSet<string> own)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT p."from", p."table" FROM pragma_foreign_key_list($t) p
            JOIN pragma_table_info($t) c ON c.name = p."from"
            WHERE c."notnull" = 0 AND c.pk = 0;
            """;
        cmd.Parameters.AddWithValue("$t", table);
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!own.Contains(reader.GetString(1)))
            {
                list.Add(reader.GetString(0));
            }
        }

        return list;
    }

    /// <summary>Eine Zeile in einem Sicherungspunkt einfügen (nur Spalten, die es noch gibt); false bei Konflikt oder fehlendem Bezug.</summary>
    internal static bool TryInsert(SqliteConnection connection, SqliteTransaction tx, string table, IReadOnlyDictionary<string, object?> row)
    {
        var existing = Columns(connection, tx, table);
        var columns = row.Keys.Where(existing.Contains).ToList();
        if (columns.Count == 0)
        {
            return false;
        }

        Execute(connection, tx, "SAVEPOINT pv_snapshot_row;");
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(",", columns.Select(Quote))}) VALUES ({string.Join(",", columns.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)))});";
            for (var i = 0; i < columns.Count; i++)
            {
                cmd.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), row[columns[i]] ?? DBNull.Value);
            }

            cmd.ExecuteNonQuery();
            Execute(connection, tx, "RELEASE pv_snapshot_row;");
            return true;
        }
        catch (SqliteException)
        {
            Execute(connection, tx, "ROLLBACK TO pv_snapshot_row; RELEASE pv_snapshot_row;");
            return false;
        }
    }

    /// <summary>Tabellen mit Fremdschlüssel ON DELETE CASCADE auf <paramref name="parent"/>.Id (Tabelle, Spalte).</summary>
    private static List<(string Table, string Column)> Children(SqliteConnection connection, SqliteTransaction? tx, string parent)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT m.name, p."from" FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) p
            WHERE m.type = 'table' AND p."table" = $parent AND p."to" = 'Id' AND p.on_delete = 'CASCADE'
            ORDER BY m.name;
            """;
        cmd.Parameters.AddWithValue("$parent", parent);
        var list = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (!s_excluded.Contains(table) && !table.StartsWith("sqlite_", StringComparison.Ordinal))
            {
                list.Add((table, reader.GetString(1)));
            }
        }

        return list;
    }

    private static HashSet<string> Columns(SqliteConnection connection, SqliteTransaction? tx, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name FROM pragma_table_info($t);";
        cmd.Parameters.AddWithValue("$t", table);
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    internal static List<Dictionary<string, object?>> ReadRows(SqliteConnection connection, SqliteTransaction? tx, string table, string column, IReadOnlyList<long> ids)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT * FROM {Quote(table)} WHERE {Quote(column)} IN ({string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)))});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    internal static void WriteRow(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> row)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in row)
        {
            writer.WritePropertyName(key);
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case long l:
                    writer.WriteStartObject();
                    writer.WriteNumber("i", l);
                    writer.WriteEndObject();
                    break;
                case double d:
                    writer.WriteStartObject();
                    writer.WriteNumber("r", d);
                    writer.WriteEndObject();
                    break;
                case byte[] bytes:
                    writer.WriteStartObject();
                    writer.WriteBase64String("b", bytes);
                    writer.WriteEndObject();
                    break;
                default:
                    writer.WriteStartObject();
                    writer.WriteString("s", Convert.ToString(value, CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                    break;
            }
        }

        writer.WriteEndObject();
    }

    internal static IReadOnlyDictionary<string, object?> ReadRow(JsonElement element)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var v = property.Value;
            row[property.Name] = v.ValueKind == JsonValueKind.Null
                ? null
                : v.TryGetProperty("i", out var i) ? i.GetInt64()
                : v.TryGetProperty("r", out var r) ? r.GetDouble()
                : v.TryGetProperty("b", out var b) ? b.GetBytesFromBase64()
                : v.GetProperty("s").GetString();
        }

        return row;
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ToLong(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
