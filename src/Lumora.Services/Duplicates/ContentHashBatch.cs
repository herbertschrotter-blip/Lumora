using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Scanning;

namespace Lumora.Services.Duplicates;

/// <summary>Eine Datei, deren Prüfsumme gelesen werden soll — Größe und Änderungszeit aus der Files-Zeile.</summary>
internal readonly record struct ContentHashRow(int FileId, int LibraryId, string Path, long Size, long ModifiedAt);

/// <summary>Ergebnis je Datei; <paramref name="Hash"/> null = für diese Dateiversion nicht lesbar.</summary>
internal readonly record struct ContentHashResult(ContentHashRow Row, byte[]? Hash);

/// <summary>
/// Gemeinsamer Teil von Doppelten-Index und Prüfsummen im Leerlauf: einen Stapel lesen (parallel nur, wo der Planer es für
/// das Laufwerk erlaubt, ADR-0074) und in einer Transaktion schreiben — nur für die gelesene Dateiversion (ADR-0053).
/// </summary>
internal static class ContentHashBatch
{
    /// <summary>Veraltet = keine Zeile oder Größe/Änderungszeit der Files-Zeile anders als beim Lesen (Alias f/h).</summary>
    public const string Stale =
        "(h.FileId IS NULL OR h.ContentSize IS NOT f.Size OR h.ContentModifiedAt IS NOT f.ModifiedAtUtc)";

    /// <summary>
    /// SHA-256 je Datei; ein Lesefehler wird gezählt und als null gemerkt (kein Dauer-Wiederversuch). Ist die Datei danach
    /// nicht mehr da (Laufwerk während des Durchgangs abgezogen, Datei verschoben), fällt sie nur aus dem Stapel (übersprungen)
    /// und kommt in einem späteren Durchgang wieder dran.
    /// </summary>
    public static async Task<(List<ContentHashResult> Results, int Failed, int Skipped)> HashAsync(
        IReadOnlyList<ContentHashRow> rows, int parallelism, ILogger logger, string logPrefix, CancellationToken token)
    {
        var hashed = new ContentHashResult?[rows.Count];
        int failed = 0, skipped = 0;
        await Task.Run(
            () => Parallel.For(0, rows.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = token }, i =>
            {
                var row = rows[i];
                try
                {
                    hashed[i] = new ContentHashResult(row, ContentHasher.Compute(row.Path, token));
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && !File.Exists(row.Path))
                {
                    Interlocked.Increment(ref skipped); // fehlt jetzt: nichts merken
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogDebug(ex, "{Prefix}: Prüfsumme für FileId {FileId} nicht lesbar", logPrefix, row.FileId);
                    hashed[i] = new ContentHashResult(row, null);
                }
            }),
            token).ConfigureAwait(false);
        return (hashed.OfType<ContentHashResult>().ToList(), failed, skipped);
    }

    /// <summary>Ein Stapel in einer Transaktion — nur, wenn die Files-Zeile noch dieselbe Version hat; liefert die geschriebenen.</summary>
    public static Task<List<ContentHashResult>> WriteAsync(ILibraryContextFactory factory, IReadOnlyList<ContentHashResult> results, CancellationToken token)
        => Task.Run(
            async () =>
            {
                using var connection = factory.OpenSqliteConnection();
                using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, token).ConfigureAwait(false);
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO FileFingerprints(FileId, ContentHash, ContentSize, ContentModifiedAt)
                    SELECT $id, $hash, $size, $mod
                    WHERE EXISTS (SELECT 1 FROM Files WHERE Id = $id AND Size = $size AND ModifiedAtUtc = $mod)
                    ON CONFLICT(FileId) DO UPDATE SET
                        ContentHash = excluded.ContentHash,
                        ContentSize = excluded.ContentSize,
                        ContentModifiedAt = excluded.ContentModifiedAt;
                    """;
                var id = cmd.Parameters.Add("$id", SqliteType.Integer);
                var hash = cmd.Parameters.Add("$hash", SqliteType.Blob);
                var size = cmd.Parameters.Add("$size", SqliteType.Integer);
                var mod = cmd.Parameters.Add("$mod", SqliteType.Integer);
                var written = new List<ContentHashResult>(results.Count);
                foreach (var r in results)
                {
                    id.Value = r.Row.FileId;
                    hash.Value = (object?)r.Hash ?? DBNull.Value;
                    size.Value = r.Row.Size;
                    mod.Value = r.Row.ModifiedAt;
                    if (cmd.ExecuteNonQuery() > 0)
                    {
                        written.Add(r);
                    }
                }

                tx.Commit();
                return written;
            },
            token);

    /// <summary>Zeilen aus <c>SELECT f.Id, f.LibraryId, d.Path, f.Name, f.Size, f.ModifiedAtUtc …</c>.</summary>
    public static List<ContentHashRow> ReadRows(SqliteCommand cmd, int capacity)
    {
        var rows = new List<ContentHashRow>(capacity);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ContentHashRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                System.IO.Path.Combine(reader.GetString(2), reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    /// <summary>Wurzeln, die gerade nicht erreichbar sind — ihre Dateien werden ohne Dateizugriff übersprungen.</summary>
    public static async Task<IReadOnlyList<string>> LoadOfflineRootsAsync(ILibraryContextFactory factory, CancellationToken token)
    {
        List<string> roots;
        await using (var ctx = factory.Create())
        {
            roots = await ctx.LibraryRoots.AsNoTracking().Select(r => r.Path).ToListAsync(token).ConfigureAwait(false);
        }

        var offline = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await RootAvailabilityProbe.IsAvailableAsync(root, token).ConfigureAwait(false))
            {
                offline.Add(System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar);
            }
        }

        return offline;
    }

    public static bool IsUnderAny(string path, IReadOnlyList<string> roots)
        => roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
}
