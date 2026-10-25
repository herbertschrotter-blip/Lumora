using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Watch;

namespace Lumora.Services.Operations;

/// <summary>
/// „Kopieren nach…“ (ADR-0068): Copy-Batch (OperationType 2) — je Datei eine echte Kopie in den Zielordner. Liegt das Ziel unter
/// einer Wurzel der Bibliothek der Datei, entsteht eine NEUE Katalogzeile (neue FileId, FTS) ohne Sterne, Favorit, Tags,
/// Kategorien, Personen oder Alben der Vorlage — die Kopie ist eine eigene Datei, eigene Angaben vergibt der Benutzer selbst
/// (Aufnahmedatum, Orte, Gesichter und Güte liest der Hintergrund aus der Datei). Außerhalb bleibt der Katalog unberührt.
/// Gleichnamige Datei im Ziel: neuer Name „Name (2).ext“ (nie überschreiben). „Rückgängig“ legt die Kopie in den Papierkorb
/// und entfernt ihre Katalogzeile.
/// </summary>
public sealed partial class FileOperationService
{
    public Task<FileOperationBatchResult> CopyAsync(IReadOnlyList<int> fileIds, string targetFolderPath, CancellationToken ct = default)
    {
        var ids = NormalizeIds(fileIds);
        if (string.IsNullOrWhiteSpace(targetFolderPath))
        {
            throw new ArgumentException("Der Zielordner darf nicht leer sein.", nameof(targetFolderPath));
        }

        var target = NormalizeDirectoryPath(targetFolderPath);
        var targetFolders = new Dictionary<int, Folder>();
        return RunBatchAsync(
            OperationType.Copy,
            ids,
            describe: known => $"{CountText(known.Count)} nach „{target}“ kopiert",
            destOf: cf => Path.Combine(target, cf.File.Name),
            prepare: (ctx, run, token) => PrepareMoveAsync(ctx, run, target, token),
            executeItem: (ctx, run, cf, op, token) => ExecuteCopyItemAsync(ctx, run, cf, op, target, targetFolders, token),
            ct,
            gateDirectory: target);
    }

    private async Task ExecuteCopyItemAsync(
        LibraryContext ctx,
        BatchRun run,
        CatalogFile cf,
        FileOperation op,
        string target,
        Dictionary<int, Folder> targetFolders,
        CancellationToken ct)
    {
        if (!File.Exists(op.SourcePath))
        {
            await FailItemAsync(ctx, run, cf, op, "Quelldatei nicht gefunden.", ct).ConfigureAwait(false);
            return;
        }

        Folder? targetFolder = null;
        var targetRoot = FindRootContaining(run.Roots, cf.Library.Id, target);
        if (targetRoot is not null)
        {
            if (!targetFolders.TryGetValue(cf.Library.Id, out targetFolder))
            {
                targetFolder = await EnsureFolderChainAsync(ctx, targetRoot, target, ct).ConfigureAwait(false);
                targetFolders[cf.Library.Id] = targetFolder;
            }
        }

        // Freier Name im Ziel (Platte und Katalog): „Name (2).ext“, „Name (3).ext“ …
        var dest = await FreeCopyPathAsync(ctx, target, cf.File.Name, targetFolder?.Id, ct).ConfigureAwait(false);
        if (dest is null)
        {
            await FailItemAsync(ctx, run, cf, op, "Kein freier Dateiname im Zielordner.", ct).ConfigureAwait(false);
            return;
        }

        op.DestPath = dest;
        string[] own = targetFolder is not null ? [dest] : [];
        await MarkApplyingAsync(ctx, op, ct).ConfigureAwait(false);
        AddAll(run.Own, own);
        try
        {
            File.Copy(op.SourcePath, dest, overwrite: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(run.Own, own);
            await FailItemAsync(ctx, run, cf, op, $"Kopieren fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        MediaFile? created = null;
        var error = await CommitItemAsync(ctx, op, undo: false, null, async token =>
        {
            if (targetFolder is not null)
            {
                created = await InsertCatalogRowAsync(ctx, cf.Library, targetFolder, dest, token).ConfigureAwait(false);
                if (created is not null)
                {
                    // Nur Eigenschaften der DATEI (Maße), keine Angaben des Benutzers (ADR-0068)
                    created.Width = cf.File.Width;
                    created.Height = cf.File.Height;
                    created.DurationMs = cf.File.DurationMs;
                    created.Codec = cf.File.Codec;
                }
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            if (created is not null)
            {
                ctx.Entry(created).State = EntityState.Detached;
                created = null;
            }

            // Katalog nicht geschrieben → die Kopie wieder weg, sonst taucht sie beim nächsten Einlesen als „neu“ auf
            try
            {
                File.Delete(dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Kopieren: Kopie nach Katalogfehler nicht entfernbar (Operation {OperationId})", op.Id);
            }

            RevokeAll(run.Own, own);
        }
        else if (created is not null)
        {
            run.Changes.Add(cf.Library.Id, FilesChangeKind.Added, created.Id);
        }

        run.Results.Add(new FileOperationItemResult(created?.Id ?? cf.File.Id, op.SourcePath, dest, error is null, error));
    }

    /// <summary>Erster freier Pfad „Name.ext“, „Name (2).ext“ … (Platte und Katalogzeilen des Zielordners); null nach 999 Versuchen.</summary>
    private static async Task<string?> FreeCopyPathAsync(LibraryContext ctx, string target, string name, int? folderId, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        HashSet<string> catalogNames = new(StringComparer.OrdinalIgnoreCase);
        if (folderId is { } id)
        {
            catalogNames = (await ctx.Files.Where(f => f.FolderId == id).Select(f => f.Name).ToListAsync(ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        for (var i = 1; i < 1000; i++)
        {
            var candidate = i == 1 ? name : $"{stem} ({i}){extension}";
            var path = Path.Combine(target, candidate);
            if (!File.Exists(path) && !Directory.Exists(path) && !catalogNames.Contains(candidate))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Rückgängig eines Copy-Items: Kopie in den Papierkorb, ihre Katalogzeile (falls gebucht) entfernen.</summary>
    private async Task<FileOperationItemResult> UndoCopyItemAsync(LibraryContext ctx, FileOperation op, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
    {
        var dest = op.DestPath;
        if (string.IsNullOrEmpty(dest))
        {
            return await UndoFailAsync(ctx, op, "Kein Zielpfad protokolliert.", ct).ConfigureAwait(false);
        }

        var row = await FindCatalogRowByPathAsync(ctx, dest, ct).ConfigureAwait(false);
        if (File.Exists(dest))
        {
            own.Add(dest);
            try
            {
                _recycleBin.MoveToRecycleBin(dest);
            }
            catch (Exception ex)
            {
                own.Revoke(dest);
                return await UndoFailAsync(ctx, op, $"Kopie konnte nicht in den Papierkorb: {ex.Message}", ct).ConfigureAwait(false);
            }
        }

        var removedLibrary = row?.LibraryId;
        var removedId = row?.Id;
        var error = await CommitItemAsync(ctx, op, undo: true, null, async token =>
        {
            if (row is not null)
            {
                await RemoveCatalogEntryAsync(ctx, row, token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (error is null && removedId is int id && removedLibrary is int library)
        {
            DeleteThumbnailFiles(library, id);
            changes.Add(library, FilesChangeKind.Removed, id);
        }

        return new FileOperationItemResult(removedId ?? op.FileId ?? 0, op.SourcePath, dest, error is null, error);
    }
}
