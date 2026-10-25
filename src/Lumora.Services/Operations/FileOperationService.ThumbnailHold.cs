using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Services.Duplicates;

namespace Lumora.Services.Operations;

/// <summary>
/// Vorschaubilder entfernter Katalogzeilen für „Rückgängig“ aufbewahren (#21, Nachtrag in ADR-0065): „Fehlende entfernen“,
/// „Ausgeschlossene entfernen“ und „Ordner in den Papierkorb“ löschen die Cache-Datei nicht mehr, sondern verschieben sie in den
/// Ablageordner des Batches (<c>thumbs\{libraryId}\_undo\{batchId}\</c>, gleiches Laufwerk, also nur ein Umbenennen) und legen die
/// Thumbnails-Zeilen als JSON daneben. „Rückgängig“ bringt Datei und Zeile zurück — gerade bei fehlenden Dateien ist das
/// Vorschaubild das einzige Bild, das Lumora noch hat, und es ließe sich nicht neu erzeugen. Die Repo-Regel bleibt im Kern: nach dem
/// Entfernen gibt es weder Thumbnails-Zeile noch Cache-Datei am üblichen Platz.
/// <para>
/// Ablagen werden nach <see cref="ThumbnailHoldMaxAge"/> beim nächsten Entfernen weggeräumt (Rückgängig ist zeitlich nicht begrenzt,
/// der Platz aber schon) — danach bringt Rückgängig die Zeile ohne Vorschaubild zurück: vorhandene Dateien bekommen es beim nächsten
/// Anzeigen neu (wie bisher), fehlende zeigen den Platzhalter mit „fehlt“. „Cache leeren“ in den Einstellungen leert die Ablage mit.
/// Alles best effort: scheitert das Verschieben, wird die Datei wie bisher gelöscht.
/// </para>
/// </summary>
public sealed partial class FileOperationService
{
    /// <summary>Name des Ablageordners unter <c>thumbs\{libraryId}\</c>.</summary>
    internal const string ThumbnailHoldFolderName = "_undo";

    /// <summary>So lange bleiben aufbewahrte Vorschaubilder liegen.</summary>
    internal static readonly TimeSpan ThumbnailHoldMaxAge = TimeSpan.FromDays(30);

    private const string ThumbnailHoldRowsSuffix = ".rows.json";

    /// <summary>Ablageordner eines Batches für eine Bibliothek.</summary>
    internal string ThumbnailHoldDirectory(int libraryId, int batchId)
        => Path.Combine(_paths.GetLibraryThumbnailsPath(libraryId), ThumbnailHoldFolderName, batchId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Thumbnails-Zeilen einer Datei lesen — vor dem Entfernen, in der Transaktion der Abzüge.</summary>
    private static List<Dictionary<string, object?>> ReadThumbnailRows(SqliteConnection connection, SqliteTransaction transaction, int fileId)
        => CatalogRowSnapshot.ReadRows(connection, transaction, "Thumbnails", "FileId", [fileId]);

    /// <summary>
    /// Cache-Dateien einer entfernten Datei in die Ablage des Batches verschieben, die Zeilen daneben ablegen. Ohne Zeilen (nie ein
    /// Vorschaubild erzeugt) oder bei Fehlern wie bisher löschen.
    /// </summary>
    private void HoldThumbnailFiles(int libraryId, int fileId, int batchId, IReadOnlyList<Dictionary<string, object?>>? rows)
    {
        var dir = _paths.GetLibraryThumbnailsPath(libraryId);
        if (rows is not { Count: > 0 } || !Directory.Exists(dir))
        {
            DeleteThumbnailFiles(libraryId, fileId);
            return;
        }

        try
        {
            var files = Directory.GetFiles(dir, $"{fileId}_*");
            if (files.Length == 0)
            {
                return;
            }

            // Zuerst die Zeilen ablegen, dann verschieben: scheitert das Ablegen, ist noch nichts verschoben (dann wie bisher löschen)
            var hold = Directory.CreateDirectory(ThumbnailHoldDirectory(libraryId, batchId)).FullName;
            using (var stream = File.Create(Path.Combine(hold, fileId.ToString(CultureInfo.InvariantCulture) + ThumbnailHoldRowsSuffix)))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var row in rows)
                {
                    CatalogRowSnapshot.WriteRow(writer, row);
                }

                writer.WriteEndArray();
            }

            foreach (var file in files)
            {
                try
                {
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        continue;
                    }

                    File.Move(file, Path.Combine(hold, Path.GetFileName(file)), overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Zeile ohne Datei in der Ablage: Rückgängig überspringt sie
                    _logger.LogDebug(ex, "Vorschaubild konnte nicht aufbewahrt werden (Datei {FileId}) — wird gelöscht", fileId);
                    TryDeleteFile(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Vorschaubild-Ablage nicht schreibbar (Datei {FileId}) — wird gelöscht", fileId);
            DeleteThumbnailFiles(libraryId, fileId);
        }
    }

    /// <summary>
    /// Nach dem Zurückschreiben der Files-Zeile: aufbewahrte Cache-Dateien zurück an ihren Platz und die Thumbnails-Zeilen mit dem
    /// aktuellen Pfad (eigene kurze Transaktion). Gibt die Zahl zurückgeholter Vorschaubilder zurück; ohne Ablage 0.
    /// </summary>
    private async Task<int> RestoreHeldThumbnailsAsync(LibraryContext ctx, int libraryId, int batchId, int fileId)
    {
        var hold = ThumbnailHoldDirectory(libraryId, batchId);
        var rowsFile = Path.Combine(hold, fileId.ToString(CultureInfo.InvariantCulture) + ThumbnailHoldRowsSuffix);
        if (!File.Exists(rowsFile))
        {
            return 0;
        }

        var restored = 0;
        try
        {
            List<IReadOnlyDictionary<string, object?>> rows;
            using (var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(rowsFile).ConfigureAwait(false)))
            {
                rows = doc.RootElement.EnumerateArray().Select(CatalogRowSnapshot.ReadRow).ToList();
            }

            var target = _paths.GetLibraryThumbnailsPath(libraryId);
            Directory.CreateDirectory(target);
            await using (var tx = await ctx.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var (connection, transaction) = RawConnection(ctx);
                foreach (var row in rows)
                {
                    var name = row.TryGetValue("CachePath", out var p) && p is string path ? Path.GetFileName(path) : null;
                    var source = name is null ? null : Path.Combine(hold, name);
                    if (source is null || !File.Exists(source))
                    {
                        continue;
                    }

                    var dest = Path.Combine(target, name!);
                    if (File.Exists(dest))
                    {
                        continue; // inzwischen neu erzeugt — das neuere bleibt (die Ablage wird unten verworfen)
                    }

                    File.Move(source, dest);
                    var copy = new Dictionary<string, object?>(row, StringComparer.Ordinal) { ["CachePath"] = dest };
                    if (CatalogRowSnapshot.TryInsert(connection, transaction, "Thumbnails", copy))
                    {
                        restored++;
                    }
                }

                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // Reste dieser Datei (ohne Zeile) und die Zeilen-Datei verwerfen, leere Ablage wegräumen
            foreach (var rest in Directory.GetFiles(hold, $"{fileId}_*"))
            {
                TryDeleteFile(rest);
            }

            TryDeleteFile(rowsFile);
            if (!Directory.EnumerateFileSystemEntries(hold).Any())
            {
                Directory.Delete(hold);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SqliteException or InvalidOperationException or KeyNotFoundException)
        {
            // Vorschaubild ist kein Grund, das Rückgängig scheitern zu lassen: es entsteht beim Anzeigen neu bzw. bleibt Platzhalter
            _logger.LogDebug(ex, "Aufbewahrtes Vorschaubild konnte nicht zurückgeholt werden (Datei {FileId})", fileId);
        }

        return restored;
    }

    /// <summary>Ablagen älter als <see cref="ThumbnailHoldMaxAge"/> wegräumen (vor jedem neuen Entfernen, best effort).</summary>
    private void PruneExpiredThumbnailHolds()
    {
        try
        {
            var root = _paths.ThumbnailsRoot;
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - ThumbnailHoldMaxAge;
            foreach (var library in Directory.EnumerateDirectories(root))
            {
                var holds = Path.Combine(library, ThumbnailHoldFolderName);
                if (!Directory.Exists(holds))
                {
                    continue;
                }

                foreach (var batchDir in Directory.EnumerateDirectories(holds))
                {
                    if (Directory.GetCreationTimeUtc(batchDir) < cutoff)
                    {
                        try
                        {
                            Directory.Delete(batchDir, recursive: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogDebug(ex, "Alte Vorschaubild-Ablage konnte nicht entfernt werden");
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Vorschaubild-Ablagen nicht lesbar");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Datei in der Vorschaubild-Ablage konnte nicht gelöscht werden");
        }
    }
}
