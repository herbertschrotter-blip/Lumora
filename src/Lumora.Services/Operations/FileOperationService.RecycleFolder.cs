using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Watch;

namespace Lumora.Services.Operations;

/// <summary>
/// „Ordner in den Papierkorb“ (ADR-0065) nach dem Protokoll aus ADR-0012: Batch + Ordner-Operation + eine Recycle-Operation
/// je katalogisierter Datei (Pending) und je Datei ein Abzug der Katalogzeile (<see cref="RemovalUndoStore"/>) in EINER
/// Transaktion — dann der Ordner in einem Schritt in den Papierkorb (bzw. nach Zustimmung endgültig gelöscht, wenn das
/// Laufwerk keinen hat), danach die Katalog-Buchung: je Datei, die wirklich weg ist, Verknüpfungen sichern (ADR-0054), FTS
/// mit alten Werten, Thumbnails-Zeilen und Files-Zeile entfernen; Folders-Zeilen der nicht mehr vorhandenen Ordner (mit
/// Abzug) entfernen; Cache-Dateien löschen. Der Ordnerbaum gilt der Ordnerüberwachung währenddessen als eigen
/// (<see cref="IOwnChangeScope.AddTree"/>) — kein automatischer Scan, keine „fehlt“-Flut.
/// „Rückgängig“ (<see cref="UndoRemovalAsync"/>) holt den Ordner aus dem Papierkorb zurück und schreibt Ordner- und
/// Dateizeilen mit denselben Ids und allen Angaben zurück.
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<FolderRecycleResult> RecycleFolderAsync(int folderId, bool allowPermanentDelete = false, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var libraryId = await ctx.Folders.AsNoTracking()
                .Where(f => f.Id == folderId)
                .Select(f => (int?)f.LibraryId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (libraryId is null)
            {
                return FolderRecycleResult.Rejected("Der Ordner ist nicht (mehr) im Katalog.");
            }

            using var lease = await AcquireLibrariesAsync([libraryId.Value], ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();
            var folder = await ctx.Folders.FirstOrDefaultAsync(f => f.Id == folderId, ct).ConfigureAwait(false);
            if (folder is null)
            {
                return FolderRecycleResult.Rejected("Der Ordner ist nicht (mehr) im Katalog.");
            }

            var path = NormalizeDirectoryPath(folder.Path);
            var check = await FolderRemovalRules.CheckAsync(ctx, folder.LibraryId, path, ct).ConfigureAwait(false);
            if (check is not null)
            {
                return FolderRecycleResult.Rejected(check);
            }

            var noBinReason = _recycleBin.GetUnavailableReasonFor(path);
            if (noBinReason is not null && !allowPermanentDelete)
            {
                _logger.LogInformation("Ordner in den Papierkorb: Laufwerk ohne Papierkorb — Rückfrage nötig");
                return new FolderRecycleResult(0, false, false, 0, 0, 0, null, NeedsPermanentConsent: true, NoRecycleBinReason: noBinReason);
            }

            var permanent = noBinReason is not null;
            return await RecycleFolderCoreAsync(ctx, folder, path, permanent, own, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<FolderRecycleResult> RecycleFolderCoreAsync(LibraryContext ctx, Folder folder, string path, bool permanent, IOwnChangeScope own, CancellationToken ct)
    {
        var libraryId = folder.LibraryId;
        var lowerPrefix = AsciiLower(path + Path.DirectorySeparatorChar);
        var descendants = await ctx.Folders
            .Where(f => f.LibraryId == libraryId && f.Path.ToLower().StartsWith(lowerPrefix))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var folderRows = descendants.Append(folder).ToList();
        var pathById = folderRows.ToDictionary(f => f.Id, f => NormalizeDirectoryPath(f.Path));
        var files = new List<MediaFile>();
        foreach (var chunk in pathById.Keys.Chunk(QueryChunkSize))
        {
            files.AddRange(await ctx.Files.Where(f => chunk.Contains(f.FolderId)).ToListAsync(ct).ConfigureAwait(false));
        }

        var batch = new OperationBatch
        {
            OperationType = permanent ? OperationType.DeleteFolder : OperationType.RecycleFolder,
            Status = BatchStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            Description = permanent
                ? $"Ordner „{folder.Name}“ endgültig gelöscht ({CountText(files.Count)}, kein Papierkorb)"
                : $"Ordner „{folder.Name}“ in den Papierkorb ({CountText(files.Count)})",
        };
        var folderOp = new FileOperation
        {
            Action = batch.OperationType,
            SourcePath = path,
            DestPath = null,
            Status = FileOperationStatus.Pending,
        };
        var fileOps = files.Select(f => (File: f, Op: new FileOperation
        {
            FileId = f.Id,
            Action = OperationType.Recycle,
            SourcePath = Path.Combine(pathById[f.FolderId], f.Name),
            DestPath = null,
            Status = FileOperationStatus.Pending,
        })).ToList();

        // ADR-0012: Operationsliste (und hier die Abzüge für „Rückgängig“) vor dem Dateisystem-Schritt persistieren
        var thumbRows = new Dictionary<int, List<Dictionary<string, object?>>>(); // #21: Vorschaubilder für „Rückgängig“ aufbewahren
        if (!permanent)
        {
            PruneExpiredThumbnailHolds();
        }

        await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            ctx.OperationBatches.Add(batch);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            folderOp.BatchId = batch.Id;
            ctx.FileOperations.Add(folderOp);
            foreach (var (_, op) in fileOps)
            {
                op.BatchId = batch.Id;
                ctx.FileOperations.Add(op);
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            if (!permanent)
            {
                var (connection, transaction) = RawConnection(ctx);
                foreach (var (file, _) in fileOps)
                {
                    RemovalUndoStore.CaptureFile(connection, transaction, batch.Id, file.Id);
                    thumbRows[file.Id] = ReadThumbnailRows(connection, transaction, file.Id);
                }

                RemovalUndoStore.CaptureFolders(connection, transaction, batch.Id, pathById.Keys);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        batch.Status = BatchStatus.Applying;
        await MarkApplyingAsync(ctx, folderOp, ct).ConfigureAwait(false);

        own.AddTree(path); // Ereignisse des ganzen Baums sind eigen (kein Diff-Scan, keine „fehlt“-Markierung)
        var existedBefore = fileOps.Where(x => File.Exists(x.Op.SourcePath)).Select(x => x.File.Id).ToHashSet();
        var deletedPermanently = permanent;
        string? fsError = null;
        try
        {
            if (permanent)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                _recycleBin.MoveDirectoryToRecycleBin(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fsError = Truncate($"{(permanent ? "Löschen" : "Papierkorb")} fehlgeschlagen: {ex.Message}");
            _logger.LogWarning(ex, "Ordner {Mode} fehlgeschlagen (Batch {BatchId})", permanent ? "löschen" : "in den Papierkorb", batch.Id);
        }

        // Hat die Shell den Ordner ohne Papierkorb gelöscht (zu groß, Benutzer stimmte in ihrem Dialog zu), ist er nicht
        // rückgängig zu machen — dann wie „endgültig gelöscht“ buchen, keine Abzüge behalten.
        if (fsError is null && !permanent && !_recycleBin.ContainsDirectory(path, batch.CreatedAtUtc))
        {
            deletedPermanently = true;
            batch.OperationType = OperationType.DeleteFolder;
            folderOp.Action = OperationType.DeleteFolder;
            batch.Description = $"Ordner „{folder.Name}“ endgültig gelöscht ({CountText(files.Count)}, nicht im Papierkorb)";
            _logger.LogWarning("Ordner in den Papierkorb: nicht im Papierkorb gefunden — als endgültig gelöscht gebucht (Batch {BatchId})", batch.Id);
        }

        // Weg ist, was vorher da war und jetzt fehlt; ist der ganze Ordner weg, alle (auch schon fehlende) Dateien darin.
        var folderGone = !Directory.Exists(path);
        var gone = fileOps
            .Where(x => folderGone || (existedBefore.Contains(x.File.Id) && !File.Exists(x.Op.SourcePath)))
            .Select(x => x.File.Id)
            .ToHashSet();
        if (fsError is not null && gone.Count == 0 && !folderGone)
        {
            // nichts passiert: alle Items Failed, Abzüge verwerfen
            var now = DateTime.UtcNow;
            foreach (var op in fileOps.Select(x => x.Op).Prepend(folderOp))
            {
                op.Status = FileOperationStatus.Failed;
                op.Error = fsError;
                op.ExecutedAtUtc = now;
            }

            batch.Status = BatchStatus.Failed;
            batch.CompletedAtUtc = now;
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await DeleteUndoRowsAsync(ctx, batch.Id).ConfigureAwait(false);
            return new FolderRecycleResult(batch.Id, false, permanent, 0, fileOps.Count, 0, fsError);
        }

        var removedFolders = 0;
        var catalogError = await CommitItemAsync(ctx, folderOp, undo: false, fsError, async token =>
        {
            foreach (var (file, op) in fileOps)
            {
                if (gone.Contains(file.Id))
                {
                    // Verknüpfungen gesichert (ADR-0054) — nicht bei endgültig gelöscht (die Datei kommt nie zurück)
                    await RemoveCatalogEntryAsync(ctx, file, token, deletedPermanently ? null : op.Id).ConfigureAwait(false);
                    MarkDone(op, undo: false, null);
                }
                else
                {
                    op.Status = FileOperationStatus.Failed;
                    op.Error = existedBefore.Contains(file.Id) ? "Datei ist noch vorhanden (gesperrt?)." : "Datei fehlte schon vorher; Ordner blieb.";
                    op.ExecutedAtUtc = DateTime.UtcNow;
                }
            }

            // Folders-Zeilen der nicht mehr vorhandenen Ordner ohne verbliebene Dateien (tiefste zuerst)
            var remaining = fileOps.Where(x => !gone.Contains(x.File.Id)).Select(x => x.File.FolderId).ToHashSet();
            var removed = new HashSet<int>();
            foreach (var row in folderRows.OrderByDescending(f => f.Path.Length))
            {
                var hasChild = folderRows.Any(c => c.ParentId == row.Id && !removed.Contains(c.Id));
                if (!Directory.Exists(pathById[row.Id]) && !remaining.Contains(row.Id) && !hasChild)
                {
                    ctx.Folders.Remove(row);
                    removed.Add(row.Id);
                }
            }

            removedFolders = removed.Count;
        }).ConfigureAwait(false);

        var removedFromCatalog = catalogError is null ? gone : new HashSet<int>();
        if (catalogError is not null)
        {
            removedFolders = 0;
        }

        // Applied, sobald etwas auf der Platte weg ist — auch wenn nur die Buchung scheiterte (der Ordner liegt dann im
        // Papierkorb und „Rückgängig“ holt ihn zurück; den Katalog heilt sonst der nächste Scan).
        var anythingGone = gone.Count > 0 || folderGone;
        batch.Status = anythingGone ? BatchStatus.Applied : BatchStatus.Failed;
        batch.CompletedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        if (deletedPermanently || !anythingGone)
        {
            await DeleteUndoRowsAsync(ctx, batch.Id).ConfigureAwait(false);
        }

        var changes = new ChangeSet();
        foreach (var id in removedFromCatalog)
        {
            // #21: endgültig gelöscht → Cache-Datei weg; sonst in die Ablage des Batches (Rückgängig bringt sie zurück)
            HoldThumbnailFiles(libraryId, id, batch.Id, deletedPermanently ? null : thumbRows.GetValueOrDefault(id));
            changes.Add(libraryId, FilesChangeKind.Removed, id);
        }

        Publish(changes);
        PublishFoldersChanged(libraryId);
        var failedFiles = fileOps.Count - gone.Count;
        var success = catalogError is null && fsError is null && failedFiles == 0;
        _logger.LogInformation(
            "Ordner {Mode}: {Files} Dateien aus dem Katalog, {Failed} geblieben, {Folders} Ordnerzeilen entfernt (Batch {BatchId})",
            deletedPermanently ? "endgültig gelöscht" : "in den Papierkorb", removedFromCatalog.Count, failedFiles, removedFolders, batch.Id);
        return new FolderRecycleResult(batch.Id, success, deletedPermanently, removedFromCatalog.Count, failedFiles, removedFolders, catalogError ?? fsError, FolderGone: folderGone);
    }

    /// <summary>Rohe Verbindung und laufende Transaktion des Contexts (für die Abzüge in <see cref="RemovalUndoStore"/>).</summary>
    private static (SqliteConnection Connection, SqliteTransaction Transaction) RawConnection(LibraryContext ctx)
        => ((SqliteConnection)ctx.Database.GetDbConnection(), (SqliteTransaction)ctx.Database.CurrentTransaction!.GetDbTransaction());

    /// <summary>Abzüge eines Batches (KeptFileId 0) verwerfen — alle oder nur die der genannten Dateien.</summary>
    private async Task DeleteUndoRowsAsync(LibraryContext ctx, int batchId, IReadOnlyCollection<int>? fileIds = null)
    {
        try
        {
            if (fileIds is null)
            {
                await ctx.DuplicateCleanupUndo.Where(u => u.BatchId == batchId && u.KeptFileId == 0).ExecuteDeleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                foreach (var chunk in fileIds.Chunk(QueryChunkSize))
                {
                    await ctx.DuplicateCleanupUndo.Where(u => u.BatchId == batchId && u.KeptFileId == 0 && chunk.Contains(u.FileId)).ExecuteDeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Rückgängig-Abzüge von Batch {BatchId} konnten nicht verworfen werden", batchId);
        }
    }

    // ------------------------------------------------------------------ Rückgängig (RecycleFolder und RemoveFromCatalog)

    /// <summary>
    /// Undo eines RecycleFolder- oder RemoveFromCatalog-Batches (ADR-0065): beim Ordner zuerst der Ordner aus dem Papierkorb
    /// zurück, dann die Folders-Zeilen (alte Ids), je Datei die Files-Zeile mit derselben FileId und allen abhängigen Zeilen aus
    /// dem Abzug (+ FTS), zuletzt die Verknüpfungen fremder Dateien in die Ordner. Fehlt eine Datei (noch) auf der Platte, kommt
    /// sie als „fehlt“ zurück.
    /// </summary>
    private async Task<List<FileOperationItemResult>> UndoRemovalAsync(LibraryContext ctx, OperationBatch batch, List<FileOperation> ops, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
    {
        var results = new List<FileOperationItemResult>();
        var folderOp = batch.OperationType == OperationType.RecycleFolder
            ? ops.FirstOrDefault(o => o.FileId is null && o.Action == OperationType.RecycleFolder)
            : null;
        var folderRestored = false;
        var catalogPending = ops.Any(o => o.FileId is not null && o.Status == FileOperationStatus.Applied);
        if (batch.OperationType == OperationType.RecycleFolder && folderOp is { Status: FileOperationStatus.Undone } && catalogPending)
        {
            // Ordner ist schon zurück (ein früheres Rückgängig scheiterte erst im Katalog): nur den Katalog nachholen
        }
        else if (batch.OperationType == OperationType.RecycleFolder)
        {
            if (folderOp is null || folderOp.Status != FileOperationStatus.Applied)
            {
                results.Add(new FileOperationItemResult(0, folderOp?.SourcePath ?? string.Empty, null, false, "Ordner-Operation nicht (mehr) rückgängig machbar."));
                return results;
            }

            var path = NormalizeDirectoryPath(folderOp.SourcePath);
            if (Directory.Exists(path) || File.Exists(path))
            {
                results.Add(await UndoFailAsync(ctx, folderOp, "Ursprungsort ist inzwischen belegt.", ct).ConfigureAwait(false));
                return results;
            }

            own.AddTree(path);
            try
            {
                _recycleBin.RestoreDirectoryFromRecycleBin(path, batch.CreatedAtUtc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(await UndoFailAsync(ctx, folderOp, $"Aus dem Papierkorb zurückholen fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false));
                return results;
            }

            folderRestored = true;
        }

        try
        {
            return await RestoreRemovalCatalogAsync(ctx, batch, ops, folderOp, results, changes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (folderRestored && folderOp is not null)
        {
            // Der Ordner liegt wieder auf der Platte: festhalten, damit ein erneutes Rückgängig nur den Katalog nachholt
            MarkDone(folderOp, undo: true, Truncate($"Ordner zurück, Katalog unvollständig: {ex.Message}"));
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Katalog-Teil des Rückgängig (Ordner-Abzug, Dateizeilen, Verknüpfungen hinein, Abzüge erledigt).</summary>
    private async Task<List<FileOperationItemResult>> RestoreRemovalCatalogAsync(
        LibraryContext ctx, OperationBatch batch, List<FileOperation> ops, FileOperation? folderOp, List<FileOperationItemResult> results, ChangeSet changes, CancellationToken ct)
    {
        // Ordner-Abzug zuerst (eigene kurze Transaktion), damit die Dateien ihre alten Ordner-Ids wiederfinden
        await ctx.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        var (snapshots, folderSnapshot) = RemovalUndoStore.Load(connection, null, batch.Id);
        var libraries = new HashSet<int>();
        if (folderSnapshot is { } fs)
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            var (c, t) = RawConnection(ctx);
            RemovalUndoStore.RestoreFolders(c, t, fs.Json);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var done = new List<int>();
        var restoredFiles = 0;
        foreach (var op in ops.Where(o => o.FileId is not null && o.Status == FileOperationStatus.Applied).OrderBy(o => o.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (!snapshots.TryGetValue(op.FileId!.Value, out var entry))
            {
                results.Add(await UndoFailAsync(ctx, op, "Keine Rückgängig-Daten zu dieser Datei.", ct).ConfigureAwait(false));
                continue;
            }

            var snapshot = Duplicates.CatalogRowSnapshot.Parse(entry.Snapshot);
            var (restoredId, error) = await RestoreSnapshotItemAsync(ctx, snapshot, op).ConfigureAwait(false);
            if (restoredId is int id)
            {
                await RestoreHeldThumbnailsAsync(ctx, snapshot.LibraryId, batch.Id, id).ConfigureAwait(false); // #21
                restoredFiles++;
                done.Add(entry.RowId);
                libraries.Add(snapshot.LibraryId);
                changes.Add(snapshot.LibraryId, FilesChangeKind.Added, id);
                results.Add(new FileOperationItemResult(id, op.SourcePath, null, true, null));
            }
            else
            {
                results.Add(await UndoFailAsync(ctx, op, error ?? "Katalogzeile konnte nicht zurückgeschrieben werden.", CancellationToken.None).ConfigureAwait(false));
            }
        }

        await using (var tx = await ctx.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var (c, t) = RawConnection(ctx);
            if (folderSnapshot is { } f)
            {
                RemovalUndoStore.RestoreFolderLinks(c, t, f.Json);
                done.Add(f.RowId);
            }

            RemovalUndoStore.MarkUndone(c, t, done);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (folderOp is not null)
        {
            MarkDone(folderOp, undo: true, null);
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            results.Insert(0, new FileOperationItemResult(0, folderOp.SourcePath, null, true, null));
            if (await FindRootContainingAsync(ctx, null, folderOp.SourcePath, CancellationToken.None).ConfigureAwait(false) is { } root)
            {
                libraries.Add(root.LibraryId);
            }
        }

        foreach (var libraryId in libraries)
        {
            PublishFoldersChanged(libraryId);
        }

        _logger.LogInformation("Rückgängig ({Type}): {Ok} Dateien zurück im Katalog, {Failed} nicht", batch.OperationType, restoredFiles, results.Count(r => !r.Success));
        return results;
    }

    /// <summary>
    /// Eine Datei aus dem Abzug in eigener Transaktion zurück: gelingt es, ist die Operation Undone; sonst bleibt sie Applied
    /// (erneuter Versuch möglich) und der Grund kommt zurück.
    /// </summary>
    private async Task<(int? FileId, string? Error)> RestoreSnapshotItemAsync(LibraryContext ctx, Duplicates.CatalogRowSnapshot.Snapshot snapshot, FileOperation op)
    {
        var none = CancellationToken.None;
        await using var tx = await ctx.Database.BeginTransactionAsync(none).ConfigureAwait(false);
        try
        {
            var id = await RestoreSnapshotRowAsync(ctx, snapshot, op, none).ConfigureAwait(false);
            if (id is null)
            {
                await tx.RollbackAsync(none).ConfigureAwait(false);
                DiscardTrackedChanges(ctx);
                return (null, "FileId oder Platz im Katalog ist inzwischen belegt (Datei schon wieder eingelesen?).");
            }

            MarkDone(op, undo: true, null);
            await ctx.SaveChangesAsync(none).ConfigureAwait(false);
            await tx.CommitAsync(none).ConfigureAwait(false);
            return (id, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or DbUpdateException or IOException)
        {
            await tx.RollbackAsync(none).ConfigureAwait(false);
            DiscardTrackedChanges(ctx);
            _logger.LogWarning(ex, "Rückgängig: Katalogzeile von Op {OpId} nicht zurückgeschrieben", op.Id);
            return (null, Truncate($"Katalog: {ex.Message}"));
        }
    }

    /// <summary>
    /// Files-Zeile aus dem Abzug zurückschreiben (innerhalb der laufenden Transaktion): Ordner über den Pfad (zurückgeholte Zeile
    /// oder neu angelegte Kette), alte FileId und Angaben, „fehlt“ nach der Platte, abhängige Zeilen, FTS; die Sicherung der
    /// Verknüpfungen (ADR-0054) wird verworfen. Liefert die FileId oder null, wenn Id bzw. Platz belegt sind.
    /// </summary>
    private async Task<int?> RestoreSnapshotRowAsync(LibraryContext ctx, Duplicates.CatalogRowSnapshot.Snapshot snapshot, FileOperation op, CancellationToken ct)
    {
        var folderPath = NormalizeDirectoryPath(snapshot.FolderPath);
        var libraryId = snapshot.LibraryId;
        var candidates = new[] { AsciiLower(folderPath), AsciiLower(folderPath + Path.DirectorySeparatorChar) };
        var folderId = await ctx.Folders.AsNoTracking()
            .Where(f => f.LibraryId == libraryId && candidates.Contains(f.Path.ToLower()))
            .Select(f => (int?)f.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (folderId is null)
        {
            var root = await FindRootContainingAsync(ctx, libraryId, folderPath, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Der Ordner liegt unter keiner Wurzel der Bibliothek mehr.");
            folderId = (await EnsureFolderChainAsync(ctx, root, folderPath, ct).ConfigureAwait(false)).Id;
        }

        var (connection, transaction) = RawConnection(ctx);
        var exists = File.Exists(Path.Combine(folderPath, snapshot.Name));
        if (!Duplicates.CatalogRowSnapshot.RestoreFileRowAsCaptured(connection, transaction, snapshot, folderId.Value, missing: !exists))
        {
            return null;
        }

        Duplicates.CatalogRowSnapshot.RestoreDependents(connection, transaction, snapshot);
        await FtsInsertAsync(ctx, snapshot.FileId, snapshot.Name, snapshot.Extension, ct).ConfigureAwait(false);
        await Albums.FileLinkUndoStore.DiscardAsync(ctx, op.Id, ct).ConfigureAwait(false);
        return snapshot.FileId;
    }
}
