using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;

namespace Lumora.Services.Operations;

/// <summary>
/// „Fehlende aus der Bibliothek entfernen“ (ADR-0065): Lumora vergisst Dateien, die es auf der Platte nicht mehr gibt — keine
/// Datei wird angefasst. Ablauf unter Schreibsperre und Scan-Sperre der Bibliotheken:
/// <list type="number">
/// <item>Offline-vs-Missing-Guard (PLAN Kap. 12): je Wurzel <see cref="RootAvailabilityProbe"/>; Dateien unter einer nicht
/// erreichbaren Wurzel bleiben (gelten als offline, nicht als fehlend).</item>
/// <item>Jede Datei noch einmal auf Existenz prüfen: wieder da → nicht entfernen, <c>Missing</c> zurücksetzen.</item>
/// <item>RemoveFromCatalog-Batch mit einer Operation je Datei und je Datei ein Abzug der Katalogzeile (Rückgängig).</item>
/// <item>Je Datei in einer Item-Transaktion: Verknüpfungen sichern (ADR-0054), FTS mit alten Werten, Thumbnails-Zeilen,
/// Files-Zeile (Kaskaden: Tags, Kategorien, Gesichter, Alben …); danach die Cache-Datei.</item>
/// <item>Leere, nicht mehr vorhandene Ordner (ohne Dateien, Unterordner und Verknüpfungen, keine Wurzel) mit Abzug aus dem
/// Baum.</item>
/// </list>
/// Protokoll auf Info-Level nur mit Zahlen (keine Dateinamen).
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<MissingRemovalBatchResult> RemoveMissingAsync(IReadOnlyList<int> fileIds, int? pruneUnderFolderId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return MissingRemovalBatchResult.Empty;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var gateLibraries = await LoadLibraryIdsAsync(ctx, ids, ct).ConfigureAwait(false);
            using var lease = await AcquireLibrariesAsync(gateLibraries, ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();
            var (known, unknown) = await LoadFilesAsync(ctx, ids, ct).ConfigureAwait(false);
            var libraryIds = known.Select(cf => cf.Library.Id).Distinct().ToList();
            var roots = await ctx.LibraryRoots.AsNoTracking()
                .Where(r => libraryIds.Contains(r.LibraryId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // 1. + 2. Guard und erneute Prüfung
            var availability = new Dictionary<int, bool>();
            var offlineRoots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var toRemove = new List<CatalogFile>();
            var reappeared = new List<CatalogFile>();
            int skippedOffline = 0, notMissing = 0;
            foreach (var cf in known)
            {
                if (!cf.File.Missing)
                {
                    notMissing++;
                    continue;
                }

                var root = FindRootContaining(roots, cf.Library.Id, cf.Folder.Path);
                if (root is null)
                {
                    skippedOffline++; // ohne Wurzel lässt sich „fehlt“ nicht von „offline“ unterscheiden
                    continue;
                }

                if (!availability.TryGetValue(root.Id, out var available))
                {
                    available = await RootAvailabilityProbe.IsAvailableAsync(root.Path, ct).ConfigureAwait(false);
                    availability[root.Id] = available;
                    if (!available)
                    {
                        offlineRoots.Add(root.Path);
                    }
                }

                if (!available)
                {
                    skippedOffline++;
                    continue;
                }

                if (File.Exists(cf.FullPath))
                {
                    cf.File.Missing = false; // wieder da
                    reappeared.Add(cf);
                }
                else
                {
                    toRemove.Add(cf);
                }
            }

            if (reappeared.Count > 0)
            {
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var changes = new ChangeSet();
            foreach (var cf in reappeared)
            {
                changes.Add(cf.Library.Id, FilesChangeKind.Metadata, cf.File.Id);
            }

            if (toRemove.Count == 0)
            {
                Publish(changes);
                LogMissingRemoval(0, 0, reappeared.Count, skippedOffline, notMissing, 0);
                return new MissingRemovalBatchResult(0, 0, reappeared.Count, skippedOffline, notMissing, 0, unknown.Count, offlineRoots.ToList());
            }

            // 3. + 4. Batch, Operationen, Abzüge, Katalog je Datei (gemeinsam mit ExcludeFromCatalog, ADR-0066)
            var description = toRemove.Count == 1 ? "1 fehlende Datei aus der Bibliothek entfernt" : $"{toRemove.Count} fehlende Dateien aus der Bibliothek entfernt";
            var (batch, removed, failedIds, removedFolderIds) = await RemoveCatalogRowsWithUndoAsync(ctx, OperationType.RemoveFromCatalog, description, toRemove, changes, ct).ConfigureAwait(false);

            // 5. leere, nicht mehr vorhandene Ordner
            var foldersRemoved = 0;
            if (removed > 0)
            {
                try
                {
                    foldersRemoved = await PruneVanishedFoldersAsync(ctx, batch.Id, removedFolderIds, pruneUnderFolderId, roots, availability).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or DbUpdateException or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "Fehlende entfernen: leere Ordner konnten nicht aus dem Baum entfernt werden (Batch {BatchId})", batch.Id);
                }
            }

            await FinishCatalogRemovalBatchAsync(ctx, batch, removed).ConfigureAwait(false);
            Publish(changes);
            foreach (var libraryId in toRemove.Select(cf => cf.Library.Id).Distinct())
            {
                PublishFoldersChanged(libraryId);
            }

            LogMissingRemoval(batch.Id, removed, reappeared.Count, skippedOffline, notMissing, foldersRemoved);
            return new MissingRemovalBatchResult(batch.Id, removed, reappeared.Count, skippedOffline, notMissing, foldersRemoved, failedIds.Count + unknown.Count, offlineRoots.ToList());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void LogMissingRemoval(int batchId, int removed, int reappeared, int offline, int notMissing, int folders)
        => _logger.LogInformation(
            "Fehlende entfernen: {Removed} entfernt, {Reappeared} wieder da, {Offline} offline übersprungen, {NotMissing} nicht fehlend, {Folders} leere Ordner entfernt (Batch {BatchId})",
            removed, reappeared, offline, notMissing, folders, batchId);

    /// <summary>
    /// Batch + eine Operation je Datei + je Datei ein Abzug in EINER Transaktion (ADR-0012: alles steht, bevor der Katalog sich
    /// ändert), danach je Datei eine Item-Transaktion: Verknüpfungen sichern (ADR-0054), FTS mit alten Werten, Thumbnails-Zeilen,
    /// Files-Zeile (Kaskaden), danach die Cache-Datei (in die Ablage des Batches, #21). Scheitert die Buchung einer Datei, ist ihre
    /// Operation Failed und ihr Abzug verworfen. Gemeinsam für „Fehlende entfernen“ (ADR-0065) und „Ausgeschlossene entfernen“ (ADR-0066).
    /// </summary>
    private async Task<(OperationBatch Batch, int Removed, List<int> FailedIds, HashSet<int> RemovedFolderIds)> RemoveCatalogRowsWithUndoAsync(
        LibraryContext ctx, OperationType type, string description, IReadOnlyList<CatalogFile> toRemove, ChangeSet changes, CancellationToken ct)
    {
        var batch = new OperationBatch
        {
            OperationType = type,
            Status = BatchStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            Description = description,
        };
        var items = toRemove.Select(cf => (File: cf, Op: new FileOperation
        {
            FileId = cf.File.Id,
            Action = type,
            SourcePath = cf.FullPath,
            DestPath = null,
            Status = FileOperationStatus.Pending,
        })).ToList();
        PruneExpiredThumbnailHolds(); // #21: alte Vorschaubild-Ablagen wegräumen
        var thumbRows = new Dictionary<int, List<Dictionary<string, object?>>>();
        await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            ctx.OperationBatches.Add(batch);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var (_, op) in items)
            {
                op.BatchId = batch.Id;
                ctx.FileOperations.Add(op);
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            var (connection, transaction) = RawConnection(ctx);
            foreach (var (cf, _) in items)
            {
                RemovalUndoStore.CaptureFile(connection, transaction, batch.Id, cf.File.Id);
                thumbRows[cf.File.Id] = ReadThumbnailRows(connection, transaction, cf.File.Id); // #21: für „Rückgängig“ aufbewahren
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        batch.Status = BatchStatus.Applying;
        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        // ab hier ohne Abbruch: die Abzüge stehen
        var failedIds = new List<int>();
        var removedFolderIds = new HashSet<int>();
        var removed = 0;
        foreach (var (cf, op) in items)
        {
            await MarkApplyingAsync(ctx, op, CancellationToken.None).ConfigureAwait(false);
            var error = await CommitItemAsync(ctx, op, undo: false, null,
                token => RemoveCatalogEntryAsync(ctx, cf.File, token, op.Id)).ConfigureAwait(false); // Verknüpfungen gesichert (ADR-0054)
            if (error is null)
            {
                removed++;
                HoldThumbnailFiles(cf.Library.Id, cf.File.Id, batch.Id, thumbRows.GetValueOrDefault(cf.File.Id)); // #21: statt löschen aufbewahren
                changes.Add(cf.Library.Id, FilesChangeKind.Removed, cf.File.Id);
                removedFolderIds.Add(cf.Folder.Id);
            }
            else
            {
                // Zeile blieb: Operation gescheitert (nichts zurückzunehmen), ihr Abzug wird verworfen
                op.Status = FileOperationStatus.Failed;
                await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                failedIds.Add(cf.File.Id);
            }
        }

        if (failedIds.Count > 0)
        {
            await DeleteUndoRowsAsync(ctx, batch.Id, failedIds).ConfigureAwait(false);
        }

        return (batch, removed, failedIds, removedFolderIds);
    }

    /// <summary>Batch abschließen: Applied, sobald etwas entfernt wurde; sonst Failed und Abzüge verworfen.</summary>
    private async Task FinishCatalogRemovalBatchAsync(LibraryContext ctx, OperationBatch batch, int removed)
    {
        batch.Status = removed > 0 ? BatchStatus.Applied : BatchStatus.Failed;
        batch.CompletedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        if (removed == 0)
        {
            await DeleteUndoRowsAsync(ctx, batch.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Ordnerzeile für das Aufräumen leerer Ordner.</summary>
    private sealed record FolderRowInfo(int Id, int? ParentId, string Path, int LibraryId);

    private static async Task<List<FolderRowInfo>> LoadFolderRowsAsync(LibraryContext ctx, IReadOnlyList<int> libraryIds)
        => await ctx.Folders.AsNoTracking()
            .Where(f => libraryIds.Contains(f.LibraryId))
            .Select(f => new FolderRowInfo(f.Id, f.ParentId, f.Path, f.LibraryId))
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);

    /// <summary>
    /// Leere Ordner, die es auf der Platte nicht mehr gibt, aus dem Baum entfernen — Kandidaten: die Ordner der entfernten
    /// Dateien samt ihren Elternordnern und (bei „Fehlende entfernen“ am Ordner) der Ordner samt Unterordnern. Entfernt wird
    /// nur, was keine Dateien, keine Unterordner-Zeilen und keine Verknüpfungen hat, keine Wurzel ist, unter einer erreichbaren
    /// Wurzel liegt und nicht existiert; tiefste zuerst, bis sich nichts mehr ändert. Mit Abzug für „Rückgängig“.
    /// </summary>
    private async Task<int> PruneVanishedFoldersAsync(
        LibraryContext ctx,
        int batchId,
        IReadOnlySet<int> fileFolderIds,
        int? pruneUnderFolderId,
        List<LibraryRoot> roots,
        Dictionary<int, bool> availability)
    {
        var libraryIds = roots.Select(r => r.LibraryId).Distinct().ToList();
        var folders = await LoadFolderRowsAsync(ctx, libraryIds).ConfigureAwait(false);
        var byId = folders.ToDictionary(f => f.Id);

        var candidates = new HashSet<int>();
        foreach (var id in fileFolderIds)
        {
            // Ordner und alle Elternordner (über ParentId, ersatzweise über den Pfad)
            for (var current = byId.GetValueOrDefault(id); current is not null && candidates.Add(current.Id);)
            {
                current = current.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
            }
        }

        if (pruneUnderFolderId is { } under && byId.TryGetValue(under, out var top))
        {
            var prefix = NormalizeDirectoryPath(top.Path) + Path.DirectorySeparatorChar;
            candidates.Add(top.Id);
            foreach (var f in folders.Where(f => f.LibraryId == top.LibraryId && NormalizeDirectoryPath(f.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(f.Id);
            }
        }

        var rootPaths = roots.Select(r => NormalizeDirectoryPath(r.Path)).ToList();
        candidates.RemoveWhere(id =>
        {
            var path = NormalizeDirectoryPath(byId[id].Path);
            var root = FindRootContaining(roots, byId[id].LibraryId, path);
            return root is null
                   || !availability.GetValueOrDefault(root.Id, false)
                   || rootPaths.Any(r => PathEquals(r, path))
                   || Directory.Exists(path);
        });
        return await RemoveEmptyFolderRowsAsync(ctx, batchId, folders, candidates).ConfigureAwait(false);
    }

    /// <summary>
    /// Entfernt von den Kandidaten, was keine Dateien, keine (verbliebenen) Unterordner-Zeilen und keine Verknüpfungen hat —
    /// tiefste zuerst, bis sich nichts mehr ändert; mit Abzug für „Rückgängig“ (ADR-0065, ADR-0066).
    /// </summary>
    private async Task<int> RemoveEmptyFolderRowsAsync(LibraryContext ctx, int batchId, List<FolderRowInfo> folders, HashSet<int> candidates)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        var byId = folders.ToDictionary(f => f.Id);
        var withFiles = new HashSet<int>();
        var withLinks = new HashSet<int>();
        foreach (var chunk in candidates.Chunk(QueryChunkSize))
        {
            withFiles.UnionWith(await ctx.Files.AsNoTracking().Where(f => chunk.Contains(f.FolderId)).Select(f => f.FolderId).Distinct().ToListAsync(CancellationToken.None).ConfigureAwait(false));
            withLinks.UnionWith(await ctx.FolderLinks.AsNoTracking().Where(l => chunk.Contains(l.FolderId)).Select(l => l.FolderId).Distinct().ToListAsync(CancellationToken.None).ConfigureAwait(false));
        }

        var children = folders.Where(f => f.ParentId is not null).GroupBy(f => f.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToHashSet());
        var remove = new HashSet<int>();
        bool changed;
        do
        {
            changed = false;
            foreach (var id in candidates.Where(c => !remove.Contains(c)).OrderByDescending(c => byId[c].Path.Length))
            {
                var hasChild = children.TryGetValue(id, out var kids) && kids.Any(k => !remove.Contains(k));
                if (!hasChild && !withFiles.Contains(id) && !withLinks.Contains(id))
                {
                    remove.Add(id);
                    changed = true;
                }
            }
        }
        while (changed);

        if (remove.Count == 0)
        {
            return 0;
        }

        await using var tx = await ctx.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        var (connection, transaction) = RawConnection(ctx);
        RemovalUndoStore.CaptureFolders(connection, transaction, batchId, remove);
        foreach (var chunk in remove.Chunk(QueryChunkSize))
        {
            await ctx.Folders.Where(f => chunk.Contains(f.Id)).ExecuteDeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return remove.Count;
    }
}
