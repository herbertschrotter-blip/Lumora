using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Duplicates;

/// <summary>
/// Prüfsummen-Phase: Vorfilter Dateigröße — nur Dateien, deren Größe im Katalog mindestens zweimal vorkommt (eine Datei mit
/// einmaliger Größe kann kein exaktes Duplikat haben), bekommen eine SHA-256. Die Größen stehen für den Durchgang in einer
/// TEMP-Tabelle der Lese-Verbindung; gelesen wird nach FileId in Stapeln. Ein Lesefehler wird für diese Dateiversion
/// gemerkt (Prüfsumme NULL), ein übersprungener (fehlt, nur online, Wurzel nicht erreichbar) nicht — er kommt später dran.
/// </summary>
public sealed partial class DuplicateIndexService
{
    private const string StaleContent = ContentHashBatch.Stale;

    private readonly record struct PhaseReport(int Done, int Skipped, int Failed);

    private async Task<PhaseReport> RunContentPhaseAsync(IReadOnlyList<string> offlineRoots, HashSet<int> touched, CancellationToken token)
    {
        using var reader = _factory.OpenSqliteConnection();
        var total = await Task.Run(() => PrepareSizes(reader), token).ConfigureAwait(false);
        if (total == 0)
        {
            return default;
        }

        _logger.LogInformation("Doppelte: {Count} Dateien gleicher Größe noch ohne Prüfsumme", total);
        var cursor = 0;
        int done = 0, skipped = 0, failed = 0;
        PublishStatus(new DuplicateIndexStatus(true, "Prüfsummen", 0, total), force: true);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            using var turn = await TurnAsync(token).ConfigureAwait(false);
            var batch = await Task.Run(() => LoadContentBatch(reader, cursor, ContentBatchSize), token).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            if (AnyBusy(batch.Select(r => r.LibraryId)))
            {
                await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                continue; // denselben Stapel später noch einmal
            }

            var readable = new List<ContentHashRow>(batch.Count);
            foreach (var row in batch)
            {
                if (ContentHashBatch.IsUnderAny(row.Path, offlineRoots) || !ThumbnailService.IsLocallyAvailable(row.Path))
                {
                    skipped++; // fehlt oder nur online: in einer späteren Sitzung
                    continue;
                }

                readable.Add(row);
            }

            // Prüfsummen parallel nur auf SSD bzw. Netz (Planer, ADR-0074); Festplatte liest eine Datei nach der anderen
            var parallelism = readable.Count == 0 ? 1 : _planner?.ReadParallelism(readable[0].Path) ?? 1;
            var (results, batchFailed, batchSkipped) = await ContentHashBatch.HashAsync(readable, parallelism, _logger, "Doppelte", token).ConfigureAwait(false);
            failed += batchFailed;
            skipped += batchSkipped;

            cursor = batch[^1].FileId;
            if (results.Count > 0)
            {
                var written = await WriteContentAsync(results, token).ConfigureAwait(false);
                done += written.Count(r => r.Hash is not null);
                foreach (var r in written)
                {
                    touched.Add(r.Row.LibraryId);
                }
            }

            PublishStatus(new DuplicateIndexStatus(true, "Prüfsummen", Math.Min(total, done + skipped + failed), total));
        }

        return new PhaseReport(done, skipped, failed);
    }

    /// <summary>Größen mit mindestens zwei Dateien in die TEMP-Tabelle; liefert die Zahl offener Dateien.</summary>
    private static int PrepareSizes(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS pv_dup_sizes(Size INTEGER PRIMARY KEY);
                DELETE FROM temp.pv_dup_sizes;
                INSERT INTO temp.pv_dup_sizes(Size)
                SELECT Size FROM Files WHERE Missing = 0 AND Size > 0 GROUP BY Size HAVING COUNT(*) > 1;
                """;
            cmd.ExecuteNonQuery();
        }

        using var count = connection.CreateCommand();
        count.CommandText = $"""
            SELECT COUNT(*) FROM Files f
            JOIN temp.pv_dup_sizes s ON s.Size = f.Size
            LEFT JOIN FileFingerprints h ON h.FileId = f.Id
            WHERE f.Missing = 0 AND {StaleContent};
            """;
        return Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<ContentHashRow> LoadContentBatch(SqliteConnection connection, int afterFileId, int take)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.Id, f.LibraryId, d.Path, f.Name, f.Size, f.ModifiedAtUtc
            FROM Files f
            JOIN temp.pv_dup_sizes s ON s.Size = f.Size
            JOIN Folders d ON d.Id = f.FolderId
            LEFT JOIN FileFingerprints h ON h.FileId = f.Id
            WHERE f.Id > $after AND f.Missing = 0 AND {StaleContent}
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$take", take);
        return ContentHashBatch.ReadRows(cmd, take);
    }

    /// <summary>Ein Stapel in einer Transaktion — nur für die gelesene Dateiversion; liefert die geschriebenen Zeilen.</summary>
    private async Task<List<ContentHashResult>> WriteContentAsync(IReadOnlyList<ContentHashResult> results, CancellationToken token)
    {
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ContentHashBatch.WriteAsync(_factory, results, token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
