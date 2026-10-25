using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Watch;

namespace Lumora.Services.Operations;

/// <summary>
/// „Ordner verschieben“ (ADR-0066) nach dem Protokoll aus ADR-0012 und dem Muster von „Ordner umbenennen“: Batch + Ordner-Operation
/// (Quelle → Ziel) + eine Move-Operation je katalogisierter Datei (Pending) in EINER Transaktion, dann <c>Directory.Move</c> in
/// einem Schritt (nur auf demselben Laufwerk), danach die Katalog-Buchung OHNE neue FileIds: Pfade des Ordners und aller
/// Unterordner, Elternordner (Zielordner, bei Bedarf samt Kette angelegt) und Wurzel der Folders-Zeilen. Dateien speichern keinen
/// Pfad (Ordner-Id + Name), Vorschaubilder sind FileId-basiert, der Suchindex kennt nur Name/Endung, Alben und
/// Ordner-Verknüpfungen hängen an Ids — es bleibt also alles verbunden. Ausschlüsse unterhalb des Ordners ziehen mit um.
/// Quelle und Ziel gelten der Ordnerüberwachung als eigen (kein Diff-Scan, keine „fehlt“-Flut). „Rückgängig“ verschiebt zurück.
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<FolderOperationResult> MoveFolderAsync(int folderId, string targetFolderPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolderPath);
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
                return Rejected("Der Ordner ist nicht (mehr) im Katalog.");
            }

            // Läuft ein Scan der Bibliothek, wartet der Schritt hier (wie alle Datei-Operationen)
            using var lease = await AcquireLibrariesAsync([libraryId.Value], ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();
            var folder = await ctx.Folders.FirstOrDefaultAsync(f => f.Id == folderId, ct).ConfigureAwait(false);
            if (folder is null)
            {
                return Rejected("Der Ordner ist nicht (mehr) im Katalog.");
            }

            var oldPath = NormalizeDirectoryPath(folder.Path);
            var target = NormalizeDirectoryPath(targetFolderPath);
            var allRoots = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            var exclusions = await _exclusions.LoadSetAsync(folder.LibraryId, ct).ConfigureAwait(false);
            var rule = FolderMoveRules.Check(allRoots, folder.LibraryId, oldPath, target, exclusions, out var sourceRoot, out var targetRoot);
            if (rule is not null)
            {
                return Rejected(rule, folder.Id, oldPath);
            }

            if (!await RootAvailabilityProbe.IsAvailableAsync(sourceRoot!.Path, ct).ConfigureAwait(false)
                || !await RootAvailabilityProbe.IsAvailableAsync(targetRoot!.Path, ct).ConfigureAwait(false))
            {
                return Rejected("Laufwerk nicht erreichbar — es wurde nichts verschoben.", folder.Id, oldPath);
            }

            var name = FolderNameOf(oldPath);
            if (!Directory.Exists(oldPath))
            {
                return Rejected($"„{name}“ ist auf der Platte nicht (mehr) vorhanden.", folder.Id, oldPath);
            }

            if (!Directory.Exists(target))
            {
                return Rejected($"Das Ziel „{FolderNameOf(target)}“ ist auf der Platte nicht vorhanden.", folder.Id, oldPath);
            }

            if (Directory.Exists(Path.Combine(oldPath, Infrastructure.CatalogFolders.HiddenCatalogFolderName)))
            {
                return Rejected($"In „{name}“ liegt eine versteckte Bibliothek (.lumora) — der Ordner lässt sich nicht verschieben.", folder.Id, oldPath);
            }

            var newPath = Path.Combine(target, name);
            var lowerNew = AsciiLower(newPath);
            var lowerNewPrefix = AsciiLower(newPath + Path.DirectorySeparatorChar);
            var libId = folder.LibraryId;
            if (Directory.Exists(newPath) || File.Exists(newPath)
                || await ctx.Folders.AnyAsync(f => f.LibraryId == libId && (f.Path.ToLower() == lowerNew || f.Path.ToLower().StartsWith(lowerNewPrefix)), ct).ConfigureAwait(false))
            {
                return Rejected($"Im Ziel gibt es schon einen Ordner „{name}“ — Zusammenführen ist nicht möglich.", folder.Id, oldPath);
            }

            return await MoveFolderCoreAsync(ctx, folder, oldPath, newPath, target, targetRoot!, own, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<FolderOperationResult> MoveFolderCoreAsync(
        LibraryContext ctx, Folder folder, string oldPath, string newPath, string target, LibraryRoot targetRoot, IOwnChangeScope own, CancellationToken ct)
    {
        var libraryId = folder.LibraryId;
        var lowerPrefix = AsciiLower(oldPath + Path.DirectorySeparatorChar);
        var descendants = await ctx.Folders
            .Where(f => f.LibraryId == libraryId && f.Path.ToLower().StartsWith(lowerPrefix))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var pathsById = descendants.ToDictionary(d => d.Id, d => NormalizeDirectoryPath(d.Path));
        pathsById[folder.Id] = oldPath;
        var files = new List<MediaFile>();
        foreach (var chunk in pathsById.Keys.Chunk(QueryChunkSize))
        {
            files.AddRange(await ctx.Files.AsNoTracking().Where(f => chunk.Contains(f.FolderId)).ToListAsync(ct).ConfigureAwait(false));
        }

        var batch = new OperationBatch
        {
            OperationType = OperationType.MoveFolder,
            Status = BatchStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            Description = $"Ordner „{folder.Name}“ nach „{FolderNameOf(target)}“ verschoben ({CountText(files.Count)})",
        };
        var folderOp = new FileOperation
        {
            Action = OperationType.MoveFolder,
            SourcePath = oldPath,
            DestPath = newPath,
            Status = FileOperationStatus.Pending,
        };
        var fileOps = files.Select(file =>
        {
            var source = Path.Combine(pathsById[file.FolderId], file.Name);
            return new FileOperation
            {
                FileId = file.Id,
                Action = OperationType.Move,
                SourcePath = source,
                DestPath = newPath + source[oldPath.Length..],
                Status = FileOperationStatus.Pending,
            };
        }).ToList();

        // ADR-0012: Operationsliste vor dem Dateisystem-Schritt persistieren
        await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            ctx.OperationBatches.Add(batch);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            folderOp.BatchId = batch.Id;
            ctx.FileOperations.Add(folderOp);
            foreach (var op in fileOps)
            {
                op.BatchId = batch.Id;
                ctx.FileOperations.Add(op);
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        batch.Status = BatchStatus.Applying;
        await MarkApplyingAsync(ctx, folderOp, ct).ConfigureAwait(false);

        // Quelle und Ziel samt Baum sind eigen: Deleted/Created bzw. Renamed des Ordners zählen nicht (kein „fehlt“-Sturm)
        string[] ownPaths = [oldPath, newPath];
        AddAll(own, ownPaths);
        own.AddTree(oldPath);
        own.AddTree(newPath);
        try
        {
            Directory.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var now = DateTime.UtcNow;
            var message = Truncate($"Verschieben fehlgeschlagen: {ex.Message}");
            foreach (var op in fileOps.Prepend(folderOp))
            {
                op.Status = FileOperationStatus.Failed;
                op.Error = message;
                op.ExecutedAtUtc = now;
            }

            batch.Status = BatchStatus.Failed;
            batch.CompletedAtUtc = now;
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(ex, "Ordner verschieben fehlgeschlagen (Batch {BatchId})", batch.Id);
            return new FolderOperationResult(batch.Id, false, folder.Id, oldPath, message);
        }

        var error = await CommitItemAsync(ctx, folderOp, undo: false, null, async token =>
        {
            var parent = await EnsureFolderChainAsync(ctx, targetRoot, target, token).ConfigureAwait(false);
            ApplyFolderPaths(folder, descendants, oldPath, newPath, null);
            folder.ParentId = parent.Id;
            foreach (var row in descendants.Append(folder))
            {
                row.RootId = targetRoot.Id; // anderer Eingang/Archiv derselben Bibliothek auf demselben Laufwerk
            }

            foreach (var op in fileOps)
            {
                MarkDone(op, undo: false, null);
            }
        }).ConfigureAwait(false);
        if (error is not null)
        {
            RevokeAll(own, ownPaths); // Katalog nicht gebucht → die Überwachung liest neu ein
        }
        else
        {
            await FollowExclusionsAsync(libraryId, oldPath, newPath).ConfigureAwait(false);
        }

        batch.Status = BatchStatus.Applied;
        batch.CompletedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        var changes = new ChangeSet();
        foreach (var file in files)
        {
            changes.Add(libraryId, FilesChangeKind.Moved, file.Id);
        }

        Publish(changes);
        PublishFoldersChanged(libraryId);
        _logger.LogInformation("Ordner verschoben: {Files} Dateien, {Subfolders} Unterordner (Batch {BatchId}){Error}", files.Count, descendants.Count, batch.Id, error is null ? string.Empty : " — Katalog unvollständig");
        return new FolderOperationResult(batch.Id, error is null, folder.Id, newPath, error);
    }

    /// <summary>
    /// Undo eines MoveFolder-Batches: der Ordner wird in einem Schritt zurückverschoben, danach Pfade, Elternordner und Wurzel der
    /// Folders-Zeilen; alle Operationen des Batches werden Undone. FileIds bleiben wie beim Verschieben.
    /// </summary>
    private async Task<List<FileOperationItemResult>> UndoMoveFolderAsync(LibraryContext ctx, List<FileOperation> ops, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
    {
        var folderOp = ops.FirstOrDefault(o => o.FileId is null && o.Action == OperationType.MoveFolder);
        var fileOps = ops.Where(o => !ReferenceEquals(o, folderOp)).ToList();
        if (folderOp is null || folderOp.Status != FileOperationStatus.Applied || string.IsNullOrEmpty(folderOp.DestPath))
        {
            return [new(0, folderOp?.SourcePath ?? string.Empty, folderOp?.DestPath, false, "Ordner-Operation nicht (mehr) rückgängig machbar.")];
        }

        var oldPath = NormalizeDirectoryPath(folderOp.SourcePath);
        var newPath = NormalizeDirectoryPath(folderOp.DestPath);
        var oldParent = Path.GetDirectoryName(oldPath);
        if (!Directory.Exists(newPath))
        {
            return [await UndoFailAsync(ctx, folderOp, "Ordner ist am Zielort nicht mehr vorhanden.", ct).ConfigureAwait(false)];
        }

        if (Directory.Exists(oldPath) || File.Exists(oldPath))
        {
            return [await UndoFailAsync(ctx, folderOp, "Ursprungsort ist inzwischen belegt.", ct).ConfigureAwait(false)];
        }

        if (oldParent is null || !Directory.Exists(oldParent))
        {
            return [await UndoFailAsync(ctx, folderOp, "Der ursprüngliche Elternordner ist nicht mehr vorhanden.", ct).ConfigureAwait(false)];
        }

        // Bibliothek über die Wurzel des jetzigen Orts (gleiche Pfade in einer anderen Bibliothek nie anfassen)
        var lowerNew = AsciiLower(newPath);
        var libraryOfNew = (await FindRootContainingAsync(ctx, null, newPath, ct).ConfigureAwait(false))?.LibraryId;
        var folder = await ctx.Folders
            .FirstOrDefaultAsync(f => f.Path.ToLower() == lowerNew && (libraryOfNew == null || f.LibraryId == libraryOfNew), ct)
            .ConfigureAwait(false);
        var lowerPrefix = AsciiLower(newPath + Path.DirectorySeparatorChar);
        var descendants = folder is null
            ? new List<Folder>()
            : await ctx.Folders.Where(f => f.LibraryId == folder.LibraryId && f.Path.ToLower().StartsWith(lowerPrefix)).ToListAsync(ct).ConfigureAwait(false);
        var root = folder is null ? null : await FindRootContainingAsync(ctx, folder.LibraryId, oldParent, ct).ConfigureAwait(false);
        if (folder is not null && root is null)
        {
            return [await UndoFailAsync(ctx, folderOp, "Der Ursprungsort liegt unter keiner Wurzel der Bibliothek mehr.", ct).ConfigureAwait(false)];
        }

        // Ohne Katalogzeile bucht das Undo nichts — dann bleibt das Ereignis fremd und die Überwachung liest neu ein.
        string[] ownPaths = folder is not null ? [newPath, oldPath] : [];
        AddAll(own, ownPaths);
        if (folder is not null)
        {
            own.AddTree(newPath);
            own.AddTree(oldPath);
        }

        try
        {
            Directory.Move(newPath, oldPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(own, ownPaths);
            return [await UndoFailAsync(ctx, folderOp, $"Zurückverschieben fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false)];
        }

        var error = await CommitItemAsync(ctx, folderOp, undo: true, null, async token =>
        {
            if (folder is not null)
            {
                var parent = await EnsureFolderChainAsync(ctx, root!, oldParent, token).ConfigureAwait(false);
                ApplyFolderPaths(folder, descendants, newPath, oldPath, null);
                folder.ParentId = parent.Id;
                foreach (var row in descendants.Append(folder))
                {
                    row.RootId = root!.Id;
                }
            }

            foreach (var op in fileOps)
            {
                if (op.Status == FileOperationStatus.Applied)
                {
                    MarkDone(op, undo: true, null);
                }
            }
        }).ConfigureAwait(false);
        if (error is not null)
        {
            RevokeAll(own, ownPaths);
        }

        var results = new List<FileOperationItemResult>(ops.Count) { new(0, folderOp.SourcePath, folderOp.DestPath, error is null, error) };
        foreach (var op in fileOps)
        {
            results.Add(new FileOperationItemResult(op.FileId ?? 0, op.SourcePath, op.DestPath, error is null, error));
            if (error is null && op.FileId is { } fileId && folder is not null)
            {
                changes.Add(folder.LibraryId, FilesChangeKind.Moved, fileId);
            }
        }

        if (folder is not null)
        {
            if (error is null)
            {
                await FollowExclusionsAsync(folder.LibraryId, newPath, oldPath).ConfigureAwait(false);
            }

            PublishFoldersChanged(folder.LibraryId);
        }

        _logger.LogInformation("Ordner verschieben rückgängig: {Files} Dateien", fileOps.Count);
        return results;
    }

    /// <summary>Ausschlüsse unterhalb eines umbenannten/verschobenen Ordners ziehen mit (ADR-0066); Fehler nur protokolliert.</summary>
    private async Task FollowExclusionsAsync(int libraryId, string oldPath, string newPath)
    {
        try
        {
            await _exclusions.MoveFoldersAsync(libraryId, oldPath, newPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or DbUpdateException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Ausschlüsse konnten nach dem Verschieben nicht nachgezogen werden (Bibliothek {LibraryId})", libraryId);
        }
    }

    private static FolderOperationResult Rejected(string error, int? folderId = null, string path = "")
        => new(0, false, folderId, path, error);
}
