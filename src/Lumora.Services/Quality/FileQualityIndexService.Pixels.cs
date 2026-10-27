using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Faces;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Quality;

/// <summary>
/// Schärfe/Belichtung-Phase (ADR-0057): je Bild aus dem fertigen Vorschaubild (kurze Kante 400 px, ADR-0043), auf
/// <see cref="ImageQuality.LoadEdge"/> geladen; Bilder ohne Vorschaubild aus dem lokalen Original, Videos nur über ihr
/// Vorschaubild. Eigener Durchlauf neben dem pHash (ADR-0053 lädt auf 128 px — zu klein für die Schärfe; ein gemeinsamer
/// Durchlauf hätte dessen Fingerabdrücke verschoben). Niedrige Thread-Priorität (Galerie und Vorschaubilder gehen vor).
/// </summary>
public sealed partial class FileQualityIndexService
{
    /// <summary>Fehlercode „nicht dekodierbar“ in <c>FileQuality.ErrorCode</c>.</summary>
    public const string DecodeError = "decode";

    private readonly record struct PixelRow(int FileId, int LibraryId, int MediaType, string Path, long Size, long ModifiedAt, string? ThumbnailPath);

    private readonly record struct PixelResult(PixelRow Row, ImageQualityResult? Quality);

    private readonly record struct PhaseReport(int Done, int Skipped, int Failed);

    /// <summary>Gültiger Güte-Wert der Zeile <paramref name="q"/> (FileQuality) zur Datei <paramref name="f"/> (Files).</summary>
    public static string ValidQuality(string q, string f)
        => $"{q}.Version = {ImageQuality.Version.ToString(CultureInfo.InvariantCulture)} AND {q}.SourceSize = {f}.Size AND {q}.SourceModifiedAt = {f}.ModifiedAtUtc";

    private static string Stale
        => $"(q.FileId IS NULL OR q.Version IS NOT {ImageQuality.Version.ToString(CultureInfo.InvariantCulture)} OR q.SourceSize IS NOT f.Size OR q.SourceModifiedAt IS NOT f.ModifiedAtUtc)";

    /// <summary>Bilder, und Videos mit fertigem Vorschaubild (Status 2 = erzeugt).</summary>
    private const string Candidate =
        "f.Missing = 0 AND (f.MediaType = 0 OR (f.MediaType = 1 AND EXISTS (SELECT 1 FROM Thumbnails t WHERE t.FileId = f.Id AND t.Status = 2)))";

    private enum RateOutcome
    {
        Rated,

        /// <summary>Nicht dekodierbar — wird gemerkt (kein neuer Versuch mit derselben Dateiversion).</summary>
        Undecodable,

        /// <summary>Gerade nicht lesbar (fehlt, gesperrt, nur online, Wurzel nicht erreichbar) — nächster Durchgang versucht es wieder.</summary>
        Unavailable,
    }

    private async Task<PhaseReport> RunPixelPhaseAsync(string scope, IReadOnlyList<int>? libraries, HashSet<int> touched, CancellationToken token)
    {
        if (_imageLoader is null)
        {
            return default;
        }

        using var reader = _factory.OpenSqliteConnection();
        var (pending, total) = await Task.Run(() => Count(reader, scope), token).ConfigureAwait(false);
        if (pending == 0)
        {
            return default;
        }

        var offlineRoots = await LoadOfflineRootsAsync(libraries, token).ConfigureAwait(false);
        _logger.LogInformation("Foto-Güte: {Count} von {Total} Dateien noch ohne Bewertung", pending, total);
        var baseDone = total - pending;
        var cursor = 0;
        int done = 0, skipped = 0, failed = 0;
        PublishStatus(new FileQualityIndexStatus(true, FileQualityIndexStatus.PixelPhase, baseDone, total), force: true);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            using var turn = await TurnAsync(token).ConfigureAwait(false);
            var batch = await Task.Run(() => LoadBatch(reader, scope, cursor, PixelBatchSize), token).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            if (_planner is null && AnyBusy(batch.Select(r => r.LibraryId)))
            {
                await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                continue;
            }

            var results = new List<PixelResult>(batch.Count);
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
                            var (outcome, quality) = Rate(row, offlineRoots);
                            switch (outcome)
                            {
                                case RateOutcome.Rated:
                                    results.Add(new PixelResult(row, quality));
                                    break;
                                case RateOutcome.Undecodable:
                                    failed++;
                                    results.Add(new PixelResult(row, null));
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
                var written = await WritePixelsAsync(results, token).ConfigureAwait(false);
                done += written.Count(r => r.Quality is not null);
                foreach (var r in written)
                {
                    touched.Add(r.Row.LibraryId);
                }
            }

            PublishStatus(new FileQualityIndexStatus(true, FileQualityIndexStatus.PixelPhase, Math.Min(total, baseDone + done + failed), total));
        }

        return new PhaseReport(done, skipped, failed);
    }

    /// <summary>Quelle: Vorschaubild, sonst das lokale Original eines Bildes; null = gerade nicht lesbar.</summary>
    private static string? SourceOf(PixelRow row, IReadOnlyList<string> offlineRoots)
    {
        if (row.ThumbnailPath is { } thumb && File.Exists(thumb))
        {
            return thumb;
        }

        if (row.MediaType != 0 || IsUnderAny(row.Path, offlineRoots) || !ThumbnailService.IsLocallyAvailable(row.Path))
        {
            return null;
        }

        return row.Path;
    }

    /// <summary>Güte, oder warum nicht: nicht dekodierbar (gemerkt) bzw. gerade nicht lesbar (später neu).</summary>
    private (RateOutcome Outcome, ImageQualityResult? Quality) Rate(PixelRow row, IReadOnlyList<string> offlineRoots)
    {
        if (SourceOf(row, offlineRoots) is not { } source)
        {
            return (RateOutcome.Unavailable, null);
        }

        try
        {
            var image = _imageLoader!.Load(source, ImageQuality.LoadEdge);
            return (RateOutcome.Rated, ImageQuality.Analyze(image.Pixels, image.Width, image.Height));
        }
        catch (Exception ex) when (ex is FaceImageDecodeException or ArgumentException)
        {
            _logger.LogDebug(ex, "Foto-Güte: FileId {FileId} nicht dekodierbar", row.FileId);
            return (RateOutcome.Undecodable, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Vorschaubild wird gerade geschrieben, Datei gesperrt (Sync, Virenscanner) — nicht merken.
            _logger.LogDebug(ex, "Foto-Güte: FileId {FileId} gerade nicht lesbar", row.FileId);
            return (RateOutcome.Unavailable, null);
        }
    }

    /// <summary>(offen, bewertbar gesamt) im Bereich.</summary>
    private static (int Pending, int Total) Count(SqliteConnection connection, string scope)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(SUM(CASE WHEN {Stale} THEN 1 ELSE 0 END), 0), COUNT(*)
            FROM Files f
            LEFT JOIN FileQuality q ON q.FileId = f.Id
            WHERE {scope} AND {Candidate};
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static List<PixelRow> LoadBatch(SqliteConnection connection, string scope, int afterFileId, int take)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.Id, f.LibraryId, f.MediaType, d.Path, f.Name, f.Size, f.ModifiedAtUtc,
                   (SELECT t.CachePath FROM Thumbnails t WHERE t.FileId = f.Id AND t.Status = 2 ORDER BY t.Size DESC LIMIT 1)
            FROM Files f
            JOIN Folders d ON d.Id = f.FolderId
            LEFT JOIN FileQuality q ON q.FileId = f.Id
            WHERE f.Id > $after AND {scope} AND {Candidate} AND {Stale}
            ORDER BY f.Id
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$take", take);
        var rows = new List<PixelRow>(take);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PixelRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                System.IO.Path.Combine(reader.GetString(3), reader.GetString(4)),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    private async Task<List<PixelResult>> WritePixelsAsync(IReadOnlyList<PixelResult> results, CancellationToken token)
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
                        INSERT INTO FileQuality(FileId, Sharpness, Exposure, Aesthetic, AestheticVersion, Version, SourceSize, SourceModifiedAt, ErrorCode)
                        SELECT $id, $sharp, $expo, NULL, NULL, $ver, $size, $mod, $err
                        WHERE EXISTS (SELECT 1 FROM Files WHERE Id = $id AND Size = $size AND ModifiedAtUtc = $mod)
                        ON CONFLICT(FileId) DO UPDATE SET
                            Sharpness = excluded.Sharpness,
                            Exposure = excluded.Exposure,
                            Aesthetic = NULL,
                            AestheticVersion = NULL,
                            Version = excluded.Version,
                            SourceSize = excluded.SourceSize,
                            SourceModifiedAt = excluded.SourceModifiedAt,
                            ErrorCode = excluded.ErrorCode;
                        """;
                    var id = cmd.Parameters.Add("$id", SqliteType.Integer);
                    var sharp = cmd.Parameters.Add("$sharp", SqliteType.Real);
                    var expo = cmd.Parameters.Add("$expo", SqliteType.Real);
                    var ver = cmd.Parameters.Add("$ver", SqliteType.Integer);
                    var size = cmd.Parameters.Add("$size", SqliteType.Integer);
                    var mod = cmd.Parameters.Add("$mod", SqliteType.Integer);
                    var err = cmd.Parameters.Add("$err", SqliteType.Text);
                    ver.Value = ImageQuality.Version;
                    var written = new List<PixelResult>(results.Count);
                    foreach (var r in results)
                    {
                        id.Value = r.Row.FileId;
                        size.Value = r.Row.Size;
                        mod.Value = r.Row.ModifiedAt;
                        if (r.Quality is { } quality)
                        {
                            sharp.Value = quality.Sharpness;
                            expo.Value = quality.Exposure;
                            err.Value = DBNull.Value;
                        }
                        else
                        {
                            sharp.Value = DBNull.Value;
                            expo.Value = DBNull.Value;
                            err.Value = DecodeError;
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
