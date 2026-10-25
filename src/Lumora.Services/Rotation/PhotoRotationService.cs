using System.Buffers.Binary;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Activity;
using Lumora.Services.Database;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Infrastructure;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Places;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;

namespace Lumora.Services.Rotation;

/// <summary>
/// Umsetzung von <see cref="IPhotoRotationService"/> (ADR-0068). Ablauf wie „Ort setzen“ (ADR-0050): Bibliotheken über die
/// Scan-Sperre belegen, Dateien schreiben (<see cref="GeoFileWriter.WriteJpegExif"/>, eigene Pfade für die Ordnerüberwachung),
/// dann Protokoll (Rotate-Batch, FileOperations, <c>GeoTagUndo</c> mit den alten Bytes) und Katalog in EINER Transaktion —
/// scheitert sie, werden die Dateien zurückgesetzt. Die Änderungszeit der Datei rückt mindestens eine Sekunde vor (Katalog,
/// Vorschaubild und Galerie-Kachel erkennen die neue Fassung an der Zeit); Rückgängig stellt sie exakt wieder her. Gesichtsrahmen
/// sind relativ zum ausgerichteten Bild (ADR-0035) und werden mitgedreht, ihre Ausschnitte verworfen. Protokoll nur mit Zahlen.
/// </summary>
public sealed class PhotoRotationService : IPhotoRotationService, IDisposable
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly ILogger<PhotoRotationService> _logger;
    private readonly IScanGate? _scanGate;
    private readonly IOwnChangeRegistry? _ownChanges;
    private readonly IThumbnailService? _thumbnails;
    private readonly IFaceCropService? _crops;
    private readonly IActivityCenter? _activities;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PhotoRotationService(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        ILogger<PhotoRotationService> logger,
        IScanGate? scanGate = null,
        IOwnChangeRegistry? ownChanges = null,
        IThumbnailService? thumbnails = null,
        IFaceCropService? crops = null,
        IActivityCenter? activities = null,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _paths = paths;
        _logger = logger;
        _scanGate = scanGate;
        _ownChanges = ownChanges;
        _thumbnails = thumbnails;
        _crops = crops;
        _activities = activities;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public void Dispose() => _writeLock.Dispose();

    // ---- Drehen ------------------------------------------------------------------------------------------------------

    private sealed record Row(int Id, int LibraryId, string Path, string Extension, MediaType MediaType, bool Missing, long ModifiedAtUnix);

    private sealed record Change(Row Row, GeoFileWrite Write);

    public async Task<RotationResult> RotateAsync(IReadOnlyList<int> fileIds, bool clockwise, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return RotationResult.Empty;
        }

        var verb = clockwise ? "Nach rechts drehen" : "Nach links drehen";
        var handle = _activities?.Start(new ActivityStartInfo(ActivityKind.FileOperation, verb, UserInitiated: true));
        var errors = new List<string>();
        var changes = new List<Change>();
        var skipped = 0;
        var batchId = 0;
        try
        {
            var all = await Task.Run(() => LoadRows(ids), ct).ConfigureAwait(false);
            var eligible = all.Where(r => !r.Missing && IPhotoRotationService.CanRotate(r.Extension, r.MediaType)).ToList();
            skipped = ids.Length - eligible.Count;
            var leases = await AcquireAsync(eligible.Select(r => r.LibraryId), ct).ConfigureAwait(false);
            try
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var own = _ownChanges?.Begin();
                    await Task.Run(() =>
                    {
                        for (var i = 0; i < eligible.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var row = eligible[i];
                            try
                            {
                                var write = GeoFileWriter.WriteJpegExif(
                                    row.Path,
                                    old => JpegExifOrientationWriter.BuildSegment(old, JpegExifOrientationWriter.Rotate(JpegExifOrientationWriter.ReadOrientation(old), clockwise)),
                                    _ => NextModifiedTime(row.ModifiedAtUnix),
                                    own);
                                changes.Add(new Change(row, write));
                            }
                            catch (GeoWriteException ex)
                            {
                                errors.Add($"{Path.GetFileName(row.Path)}: {ex.Message}");
                            }

                            handle?.Report(i + 1, eligible.Count);
                        }
                    }, ct).ConfigureAwait(false);

                    if (changes.Count > 0)
                    {
                        batchId = await WriteRotateCatalogAsync(changes, clockwise, $"{verb} ({changes.Count} Datei(en))").ConfigureAwait(false);
                    }
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (changes.Count > 0 && batchId == 0)
            {
                RollBack(changes);
                throw;
            }
            finally
            {
                Release(leases);
            }
        }
        catch (OperationCanceledException)
        {
            handle?.Canceled();
            throw;
        }
        catch (Exception ex)
        {
            handle?.Fail("Drehen fehlgeschlagen", ex.Message);
            throw;
        }

        AfterCatalogChange(changes.Select(c => (c.Row.Id, c.Row.LibraryId)).ToList());
        var result = new RotationResult(batchId, changes.Count, skipped, errors.Count, errors);
        _logger.LogInformation("Drehen ({Direction}): {Rotated} gedreht, {Skipped} übersprungen, {Failed} fehlgeschlagen", clockwise ? "rechts" : "links", changes.Count, skipped, errors.Count);
        handle?.Complete(new ActivityCompletion(
            result.Summary,
            errors.Count > 0 || skipped > 0 ? ActivityState.Warning : ActivityState.Succeeded,
            result.CanUndo ? [ActivityAction.Undo(async token => (await UndoAsync(batchId, token).ConfigureAwait(false)).Summary.Replace("gedreht", "zurückgedreht", StringComparison.Ordinal))] : null,
            errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null));
        return result;
    }

    /// <summary>Neue Änderungszeit: jetzt, aber mindestens eine Sekunde nach dem Katalogwert (Unix-Sekunden).</summary>
    private static DateTime NextModifiedTime(long catalogModifiedUnix)
    {
        var minimum = DateTimeOffset.FromUnixTimeSeconds(catalogModifiedUnix + 1).UtcDateTime;
        var now = DateTime.UtcNow;
        return now > minimum ? now : minimum;
    }

    private async Task<int> WriteRotateCatalogAsync(IReadOnlyList<Change> changes, bool clockwise, string description)
    {
        try
        {
            return await Task.Run(async () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var batchId = Convert.ToInt32(Scalar(connection, tx, """
                    INSERT INTO OperationBatches (OperationType, Status, CreatedAtUtc, CompletedAtUtc, PlanId, Description)
                    VALUES ($type, $status, $now, $now, NULL, $description);
                    SELECT last_insert_rowid();
                    """, ("$type", (int)OperationType.Rotate), ("$status", (int)BatchStatus.Applied), ("$now", now), ("$description", description)), CultureInfo.InvariantCulture);
                foreach (var (row, write) in changes)
                {
                    var opId = Convert.ToInt64(Scalar(connection, tx, """
                        INSERT INTO FileOperations (BatchId, FileId, Action, SourcePath, DestPath, Status, Error, ExecutedAtUtc, UndoneAtUtc)
                        VALUES ($batch, $file, $action, $path, $path, $status, NULL, $now, NULL);
                        SELECT last_insert_rowid();
                        """, ("$batch", batchId), ("$file", row.Id), ("$action", (int)OperationType.Rotate), ("$path", row.Path),
                        ("$status", (int)FileOperationStatus.Applied), ("$now", now)), CultureInfo.InvariantCulture);
                    Execute(connection, tx, """
                        INSERT INTO GeoTagUndo (FileOperationId, OldLatitude, OldLongitude, OldGeoSource, OldPlaceId, NewLatitude, NewLongitude,
                                                WriteKind, WrittenPath, OldBytes, WrittenHash, SegmentOffset, OldModifiedTicks, OldSize)
                        VALUES ($op, NULL, NULL, NULL, NULL, NULL, NULL, $kind, $written, $oldBytes, $hash, $offset, $oldModified, $oldSize);
                        """, ("$op", opId), ("$kind", (int)write.Kind), ("$written", write.WrittenPath), ("$oldBytes", write.OldBytes),
                        ("$hash", write.WrittenHash), ("$offset", write.SegmentOffset), ("$oldModified", write.OldModifiedTicks), ("$oldSize", write.OldSize));
                    UpdateFileRow(connection, tx, row.Id, write.NewSize, write.NewModifiedAt);
                    RotateFaces(connection, tx, row.Id, clockwise);
                }

                tx.Commit();
                return batchId;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Drehen: Katalog konnte nicht geschrieben werden — {Count} Datei(en) werden zurückgesetzt", changes.Count);
            RollBack(changes);
            throw;
        }
    }

    private void RollBack(IEnumerable<Change> changes)
    {
        using var own = _ownChanges?.Begin();
        foreach (var change in changes)
        {
            if (GeoFileWriter.Restore(change.Write, own) is { } error)
            {
                _logger.LogWarning("Drehen: Datei {FileId} konnte nicht zurückgesetzt werden ({Reason})", change.Row.Id, error);
            }
        }
    }

    // ---- Rückgängig --------------------------------------------------------------------------------------------------

    private sealed record UndoEntry(long OperationId, int? FileId, GeoFileWrite Write);

    public async Task<RotationResult> UndoAsync(int batchId, CancellationToken ct = default)
    {
        var entries = await Task.Run(() => LoadUndo(batchId), ct).ConfigureAwait(false);
        if (entries is null)
        {
            return RotationResult.Empty;
        }

        var libraries = await Task.Run(() => LibrariesOf(entries.Select(e => e.FileId).OfType<int>().ToList()), ct).ConfigureAwait(false);
        var restored = new List<(UndoEntry Entry, bool WasClockwise)>();
        var errors = new List<string>();
        var leases = await AcquireAsync(libraries.Values, ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var own = _ownChanges?.Begin();
                await Task.Run(() =>
                {
                    foreach (var entry in entries)
                    {
                        var path = entry.Write.WrittenPath ?? string.Empty;
                        var current = ReadCurrentOrientation(path, entry.Write.SegmentOffset);
                        var old = JpegExifOrientationWriter.ReadOrientation(entry.Write.OldBytes is { Length: > 0 } b ? b : null);
                        var wasClockwise = JpegExifOrientationWriter.Rotate(old, clockwise: true) == current;
                        if (GeoFileWriter.Restore(entry.Write, own) is { } error)
                        {
                            errors.Add($"{Path.GetFileName(path)}: {error}");
                            continue;
                        }

                        restored.Add((entry, wasClockwise));
                    }
                }, CancellationToken.None).ConfigureAwait(false);

                try
                {
                await Task.Run(async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (var (entry, wasClockwise) in restored)
                    {
                        Execute(connection, tx, "UPDATE FileOperations SET Status = $undone, UndoneAtUtc = $now WHERE Id = $op;",
                            ("$undone", (int)FileOperationStatus.Undone), ("$now", now), ("$op", entry.OperationId));
                        if (entry.FileId is { } fileId)
                        {
                            var oldModified = entry.Write.OldModifiedTicks is { } ticks
                                ? GeoFileWriter.ToUnix(new DateTime(ticks, DateTimeKind.Utc))
                                : (long?)null;
                            UpdateFileRow(connection, tx, fileId, entry.Write.OldSize, oldModified);
                            RotateFaces(connection, tx, fileId, clockwise: !wasClockwise);
                        }
                    }

                    if (restored.Count > 0)
                    {
                        Execute(connection, tx, "UPDATE OperationBatches SET Status = $status WHERE Id = $b;", ("$status", (int)BatchStatus.Undone), ("$b", batchId));
                    }

                    tx.Commit();
                }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Dateien sind schon zurückgesetzt; Maße holt das nächste Einlesen (Änderungszeit weicht ab), der Batch
                    // bleibt „Applied“ und ein erneutes Rückgängig scheitert an der Prüfsumme — als Fehler melden statt still
                    _logger.LogError(ex, "Drehen rückgängig: Katalog konnte nicht geschrieben werden (Batch {BatchId}, {Count} Datei(en) bereits zurückgesetzt)", batchId, restored.Count);
                    errors.Add("Katalog nicht aktualisiert — bitte den Ordner neu einlesen");
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            Release(leases);
        }

        AfterCatalogChange(restored.Where(r => r.Entry.FileId is int id && libraries.ContainsKey(id))
            .Select(r => (r.Entry.FileId!.Value, libraries[r.Entry.FileId!.Value])).ToList());
        _logger.LogInformation("Drehen rückgängig: Batch {BatchId} — {Restored} zurück, {Failed} nicht möglich", batchId, restored.Count, errors.Count);
        return new RotationResult(batchId, restored.Count, 0, errors.Count, errors);
    }

    public Task<int?> GetUndoableBatchIdAsync(CancellationToken ct = default)
        => Task.Run<int?>(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            return Scalar(connection, null, "SELECT MAX(Id) FROM OperationBatches WHERE OperationType = $type AND Status = $applied;",
                ("$type", (int)OperationType.Rotate), ("$applied", (int)BatchStatus.Applied)) is long id ? (int)id : null;
        }, ct);

    private static int ReadCurrentOrientation(string path, long? offset)
    {
        try
        {
            using var stream = GeoFileWriter.OpenRead(path);
            if (JpegExifGpsWriter.Locate(stream) is { Exists: true } location && (offset is null || location.Offset == offset))
            {
                return JpegExifOrientationWriter.ReadOrientation(GeoFileWriter.ReadRange(stream, location.Offset, location.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            // Restore meldet den Grund gleich selbst
        }

        return 1;
    }

    private List<UndoEntry>? LoadUndo(int batchId)
    {
        using var connection = _factory.OpenSqliteConnection();
        if (Scalar(connection, null, "SELECT COUNT(*) FROM OperationBatches WHERE Id = $b AND OperationType = $type AND Status = $applied;",
                ("$b", batchId), ("$type", (int)OperationType.Rotate), ("$applied", (int)BatchStatus.Applied)) is not long n || n == 0)
        {
            return null;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT o.Id, o.FileId, u.WriteKind, u.WrittenPath, u.OldBytes, u.WrittenHash, u.SegmentOffset, u.OldModifiedTicks, u.OldSize
            FROM FileOperations o JOIN GeoTagUndo u ON u.FileOperationId = o.Id
            WHERE o.BatchId = $b AND o.Status = $applied ORDER BY o.Id DESC;
            """;
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$applied", (int)FileOperationStatus.Applied);
        var list = new List<UndoEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new UndoEntry(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                new GeoFileWrite(
                    (GeoTagWriteKind)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? [] : (byte[])reader.GetValue(4),
                    reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8))));
        }

        return list;
    }

    // ---- Katalog -----------------------------------------------------------------------------------------------------

    /// <summary>Größe/Änderungszeit setzen (wo bekannt), Breite und Höhe tauschen, Vorschaubild-Zeilen verwerfen.</summary>
    private static void UpdateFileRow(SqliteConnection connection, SqliteTransaction tx, int fileId, long? size, long? modifiedUnix)
    {
        Execute(connection, tx, """
            UPDATE Files SET Size = COALESCE($size, Size), ModifiedAtUtc = COALESCE($modified, ModifiedAtUtc), Width = Height, Height = Width
            WHERE Id = $id;
            DELETE FROM Thumbnails WHERE FileId = $id;
            """, ("$size", size), ("$modified", modifiedUnix), ("$id", fileId));
    }

    /// <summary>Gesichtsrahmen und Landmarken (relativ zum ausgerichteten Bild) um 90° drehen.</summary>
    private static void RotateFaces(SqliteConnection connection, SqliteTransaction tx, int fileId, bool clockwise)
    {
        var faces = new List<(long Id, float X, float Y, float W, float H, byte[] Landmarks)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Id, BoxX, BoxY, BoxW, BoxH, Landmarks FROM Faces WHERE FileId = $f;";
            read.Parameters.AddWithValue("$f", fileId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                faces.Add((reader.GetInt64(0), reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4), (byte[])reader.GetValue(5)));
            }
        }

        foreach (var face in faces)
        {
            var (x, y, w, h) = RotateBox(face.X, face.Y, face.W, face.H, clockwise);
            Execute(connection, tx, "UPDATE Faces SET BoxX = $x, BoxY = $y, BoxW = $w, BoxH = $h, Landmarks = $l WHERE Id = $id;",
                ("$x", x), ("$y", y), ("$w", w), ("$h", h), ("$l", RotateLandmarks(face.Landmarks, clockwise)), ("$id", face.Id));
        }
    }

    /// <summary>Box (relativ) um 90° drehen: im Uhrzeigersinn (x, y) → (1 − y − h, x), dagegen (x, y) → (y, 1 − x − w).</summary>
    public static (float X, float Y, float W, float H) RotateBox(float x, float y, float w, float h, bool clockwise)
        => clockwise ? (1f - y - h, x, h, w) : (y, 1f - x - w, h, w);

    /// <summary>Landmarken (10 float32 little-endian, Punkte relativ) um 90° drehen; andere Längen bleiben unverändert.</summary>
    public static byte[] RotateLandmarks(byte[] landmarks, bool clockwise)
    {
        if (landmarks.Length % 8 != 0 || landmarks.Length == 0)
        {
            return landmarks;
        }

        var result = new byte[landmarks.Length];
        for (var i = 0; i < landmarks.Length; i += 8)
        {
            var px = BinaryPrimitives.ReadSingleLittleEndian(landmarks.AsSpan(i, 4));
            var py = BinaryPrimitives.ReadSingleLittleEndian(landmarks.AsSpan(i + 4, 4));
            var (nx, ny) = clockwise ? (1f - py, px) : (py, 1f - px);
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(i, 4), nx);
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(i + 4, 4), ny);
        }

        return result;
    }

    /// <summary>Vorschaubild-Dateien und Gesichts-Ausschnitte verwerfen, Galerie und Ordnerbaum benachrichtigen.</summary>
    private void AfterCatalogChange(IReadOnlyList<(int FileId, int LibraryId)> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        foreach (var (fileId, libraryId) in files)
        {
            DeleteThumbnailFiles(libraryId, fileId);
        }

        _thumbnails?.Invalidate(files.Select(f => f.FileId).ToList());
        if (_crops is not null)
        {
            var faces = LoadFaceIds(files.Select(f => f.FileId).ToList());
            foreach (var group in faces.GroupBy(f => f.LibraryId))
            {
                _crops.Delete(group.Key, group.Select(f => f.FaceId).ToList());
            }
        }

        foreach (var group in files.GroupBy(f => f.LibraryId))
        {
            _messenger.Send(new FilesChangedMessage(group.Key, FilesChangeKind.Moved, group.Select(f => f.FileId).ToList()));
        }
    }

    private void DeleteThumbnailFiles(int libraryId, int fileId)
    {
        var dir = _paths.GetLibraryThumbnailsPath(libraryId);
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, $"{fileId.ToString(CultureInfo.InvariantCulture)}_*"))
            {
                try
                {
                    File.Delete(file); // Vorschaubild-Cache von Lumora, nie eine Bibliotheksdatei
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Vorschaubild {FileId} konnte nicht gelöscht werden", fileId);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Vorschaubild-Ordner der Bibliothek {LibraryId} nicht lesbar", libraryId);
        }
    }

    private List<Row> LoadRows(IReadOnlyList<int> ids)
    {
        var list = new List<Row>();
        using var connection = _factory.OpenSqliteConnection();
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.Id, f.LibraryId, d.Path, f.Name, f.Extension, f.MediaType, f.Missing, f.ModifiedAtUtc
                FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({IdList(chunk)});
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new Row(reader.GetInt32(0), reader.GetInt32(1), Path.Combine(reader.GetString(2), reader.GetString(3)),
                    reader.GetString(4), (MediaType)reader.GetInt32(5), reader.GetBoolean(6), reader.GetInt64(7)));
            }
        }

        var order = ids.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        return list.OrderBy(r => order[r.Id]).ToList();
    }

    private Dictionary<int, int> LibrariesOf(IReadOnlyList<int> fileIds)
    {
        var map = new Dictionary<int, int>();
        using var connection = _factory.OpenSqliteConnection();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Id, LibraryId FROM Files WHERE Id IN ({IdList(chunk)});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                map[reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }

        return map;
    }

    private List<(long FaceId, int LibraryId)> LoadFaceIds(IReadOnlyList<int> fileIds)
    {
        var list = new List<(long, int)>();
        using var connection = _factory.OpenSqliteConnection();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT x.Id, f.LibraryId FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE x.FileId IN ({IdList(chunk)});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add((reader.GetInt64(0), reader.GetInt32(1)));
            }
        }

        return list;
    }

    private async Task<IReadOnlyList<IDisposable>> AcquireAsync(IEnumerable<int> libraryIds, CancellationToken ct)
    {
        var leases = new List<IDisposable>();
        if (_scanGate is null)
        {
            return leases;
        }

        try
        {
            foreach (var id in libraryIds.Distinct().Order())
            {
                leases.Add(await _scanGate.AcquireForFileOperationAsync(id, ct).ConfigureAwait(false));
            }
        }
        catch
        {
            Release(leases);
            throw;
        }

        return leases;
    }

    private static void Release(IReadOnlyList<IDisposable> leases)
    {
        for (var i = leases.Count - 1; i >= 0; i--)
        {
            leases[i].Dispose();
        }
    }

    private static string IdList(IEnumerable<int> ids) => string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }
}
