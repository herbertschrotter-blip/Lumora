using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Faces;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Duplicates;

/// <summary>
/// Fingerabdruck-Phase: pHash je Bild aus dem fertigen Vorschaubild (kurze Kante 400 px, ADR-0043), auf
/// <see cref="PerceptualHash.LoadEdge"/> verkleinert geladen; Bilder ohne Vorschaubild aus dem Original (nur lokal),
/// Videos nur über ihr Vorschaubild. Läuft mit niedriger Thread-Priorität (Galerie und Vorschaubilder gehen vor).
/// </summary>
public sealed partial class DuplicateIndexService
{
    private const string ImageError = "decode";

    private readonly record struct ImageRow(int FileId, int LibraryId, int MediaType, string Path, long ModifiedAt, string? ThumbnailPath);

    private readonly record struct ImageResult(ImageRow Row, ImageFingerprint? Fingerprint);

    private static string StaleImage
        => $"(h.FileId IS NULL OR h.ImageHashVersion IS NOT {PerceptualHash.Version.ToString(CultureInfo.InvariantCulture)} OR h.ImageModifiedAt IS NOT f.ModifiedAtUtc)";

    /// <summary>Bilder, und Videos mit fertigem Vorschaubild (Status 2 = erzeugt).</summary>
    private const string ImageCandidate =
        "f.Missing = 0 AND (f.MediaType = 0 OR (f.MediaType = 1 AND EXISTS (SELECT 1 FROM Thumbnails t WHERE t.FileId = f.Id AND t.Status = 2)))";

    private async Task<PhaseReport> RunImagePhaseAsync(IReadOnlyList<string> offlineRoots, HashSet<int> touched, CancellationToken token)
    {
        if (_imageLoader is null)
        {
            return default;
        }

        using var reader = _factory.OpenSqliteConnection();
        var total = await Task.Run(() => CountImages(reader), token).ConfigureAwait(false);
        if (total == 0)
        {
            return default;
        }

        _logger.LogInformation("Doppelte: {Count} Bilder noch ohne Bild-Fingerabdruck", total);
        var cursor = 0;
        int done = 0, skipped = 0, failed = 0;
        PublishStatus(new DuplicateIndexStatus(true, "Bild-Fingerabdrücke", 0, total), force: true);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            using var turn = await TurnAsync(token).ConfigureAwait(false);
            var batch = await Task.Run(() => LoadImageBatch(reader, cursor, ImageBatchSize), token).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            if (AnyBusy(batch.Select(r => r.LibraryId)))
            {
                await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                continue;
            }

            var results = new List<ImageResult>(batch.Count);
            await Task.Run(
                () =>
                {
                    var thread = Thread.CurrentThread;
                    var previous = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    try
                    {
                        foreach (var row in batch)
                        {
                            token.ThrowIfCancellationRequested();
                            switch (Fingerprint(row, offlineRoots))
                            {
                                case { } fingerprint:
                                    results.Add(new ImageResult(row, fingerprint));
                                    break;
                                case null when CanRead(row, offlineRoots):
                                    failed++;
                                    results.Add(new ImageResult(row, null));
                                    break;
                                default:
                                    skipped++;
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        thread.Priority = previous;
                    }
                },
                token).ConfigureAwait(false);

            cursor = batch[^1].FileId;
            if (results.Count > 0)
            {
                var written = await WriteImagesAsync(results, token).ConfigureAwait(false);
                done += written.Count(r => r.Fingerprint is not null);
                foreach (var r in written)
                {
                    touched.Add(r.Row.LibraryId);
                }
            }

            PublishStatus(new DuplicateIndexStatus(true, "Bild-Fingerabdrücke", Math.Min(total, done + skipped + failed), total));
        }

        return new PhaseReport(done, skipped, failed);
    }

    /// <summary>Quelle des Fingerabdrucks: Vorschaubild, sonst das lokale Original eines Bildes; null = gerade nicht lesbar.</summary>
    private static string? SourceOf(ImageRow row, IReadOnlyList<string> offlineRoots)
    {
        if (row.ThumbnailPath is { } thumb && File.Exists(thumb))
        {
            return thumb;
        }

        if (row.MediaType != 0 || ContentHashBatch.IsUnderAny(row.Path, offlineRoots) || !ThumbnailService.IsLocallyAvailable(row.Path))
        {
            return null;
        }

        return row.Path;
    }

    private static bool CanRead(ImageRow row, IReadOnlyList<string> offlineRoots) => SourceOf(row, offlineRoots) is not null;

    /// <summary>Fingerabdruck oder null (nicht lesbar bzw. gerade nicht erreichbar — siehe <see cref="CanRead"/>).</summary>
    private ImageFingerprint? Fingerprint(ImageRow row, IReadOnlyList<string> offlineRoots)
    {
        if (SourceOf(row, offlineRoots) is not { } source)
        {
            return null;
        }

        try
        {
            var image = _imageLoader!.Load(source, PerceptualHash.LoadEdge);
            return PerceptualHash.Compute(image.Pixels, image.Width, image.Height);
        }
        catch (Exception ex) when (ex is FaceImageDecodeException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "Doppelte: Bild-Fingerabdruck für FileId {FileId} nicht möglich", row.FileId);
            return null;
        }
    }

    private int CountImages(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM Files f
            LEFT JOIN FileFingerprints h ON h.FileId = f.Id
            WHERE {ImageCandidate} AND {StaleImage};
            """;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<ImageRow> LoadImageBatch(SqliteConnection connection, int afterFileId, int take)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.Id, f.LibraryId, f.MediaType, d.Path, f.Name, f.ModifiedAtUtc,
                   (SELECT t.CachePath FROM Thumbnails t WHERE t.FileId = f.Id AND t.Status = 2 ORDER BY t.Size DESC LIMIT 1)
            FROM Files f
            JOIN Folders d ON d.Id = f.FolderId
            LEFT JOIN FileFingerprints h ON h.FileId = f.Id
            WHERE f.Id > $after AND {ImageCandidate} AND {StaleImage}
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$take", take);
        var rows = new List<ImageRow>(take);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ImageRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                System.IO.Path.Combine(reader.GetString(3), reader.GetString(4)),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private async Task<List<ImageResult>> WriteImagesAsync(IReadOnlyList<ImageResult> results, CancellationToken token)
    {
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, token).ConfigureAwait(false);
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO FileFingerprints(FileId, ImageHash, ImageHashVersion, ImageModifiedAt, ImageLowDetail, ImageError)
                        SELECT $id, $hash, $ver, $mod, $low, $err
                        WHERE EXISTS (SELECT 1 FROM Files WHERE Id = $id AND ModifiedAtUtc = $mod)
                        ON CONFLICT(FileId) DO UPDATE SET
                            ImageHash = excluded.ImageHash,
                            ImageHashVersion = excluded.ImageHashVersion,
                            ImageModifiedAt = excluded.ImageModifiedAt,
                            ImageLowDetail = excluded.ImageLowDetail,
                            ImageError = excluded.ImageError;
                        """;
                    var id = cmd.Parameters.Add("$id", SqliteType.Integer);
                    var hash = cmd.Parameters.Add("$hash", SqliteType.Integer);
                    var ver = cmd.Parameters.Add("$ver", SqliteType.Integer);
                    var mod = cmd.Parameters.Add("$mod", SqliteType.Integer);
                    var low = cmd.Parameters.Add("$low", SqliteType.Integer);
                    var err = cmd.Parameters.Add("$err", SqliteType.Text);
                    ver.Value = PerceptualHash.Version;
                    var written = new List<ImageResult>(results.Count);
                    foreach (var r in results)
                    {
                        id.Value = r.Row.FileId;
                        mod.Value = r.Row.ModifiedAt;
                        if (r.Fingerprint is { } fp)
                        {
                            hash.Value = PerceptualHash.ToStored(fp.Hash);
                            low.Value = fp.LowDetail ? 1 : 0;
                            err.Value = DBNull.Value;
                        }
                        else
                        {
                            hash.Value = DBNull.Value;
                            low.Value = 0;
                            err.Value = ImageError;
                        }

                        if (cmd.ExecuteNonQuery() > 0)
                        {
                            written.Add(r);
                        }
                    }

                    tx.Commit();
                    return written;
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
