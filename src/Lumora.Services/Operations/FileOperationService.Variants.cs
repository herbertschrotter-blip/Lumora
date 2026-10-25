using Microsoft.EntityFrameworkCore;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Watch;

namespace Lumora.Services.Operations;

/// <summary>
/// Eine verbesserte Fassung, die als neue Datei in den Katalog soll (ADR-0071).
/// </summary>
/// <param name="OriginalFileId">Das Original (bleibt unverändert).</param>
/// <param name="ResultPath">Fertige Ergebnis-Datei (Zwischenstand im Temp-Ordner des Katalogs); wird an das Ziel verschoben.</param>
/// <param name="TargetPath">Gewünschter Zielpfad; ist er belegt, wird „ (2)“, „ (3)“ … angehängt — nie überschrieben.</param>
/// <param name="Tools">Kurzkennungen der Werkzeuge („upscale+light“).</param>
/// <param name="CopyCatalogMetadata">Aufnahmedatum und Ort aus der Katalogzeile des Originals übernehmen.</param>
/// <param name="CopyRatingAndTags">Favorit, Sterne und Tags des Originals übernehmen.</param>
/// <param name="Width">Pixelbreite des Ergebnisses (für Files.Width), null = unbekannt.</param>
/// <param name="Height">Pixelhöhe des Ergebnisses.</param>
public sealed record VariantCreateRequest(
    int OriginalFileId,
    string ResultPath,
    string TargetPath,
    string Tools,
    bool CopyCatalogMetadata = true,
    bool CopyRatingAndTags = true,
    int? Width = null,
    int? Height = null);

/// <summary>
/// Fassungen anlegen (ADR-0071, CreateVariant-Batch): je Anfrage wird die fertige Ergebnis-Datei an ihr Ziel verschoben (der
/// Dateisystem-Schritt), danach in einer kurzen Transaktion die Files-Zeile (FTS, Ordnerkette unter der Wurzel), Metadaten aus
/// der Katalogzeile des Originals und die Verknüpfung <c>PhotoVariants</c> gebucht. Das Original wird nie angefasst. Ziele
/// außerhalb der Wurzeln der Bibliothek des Originals werden abgelehnt (der Katalog bliebe sonst ohne Eintrag; in einer
/// versteckten Bibliothek bleibt so alles im Container). „Rückgängig“: neue Datei in den Papierkorb, Katalogzeile samt
/// Verknüpfung weg.
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<FileOperationBatchResult> CreateVariantsAsync(IReadOnlyList<VariantCreateRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("Es wurden keine Fassungen angegeben.", nameof(requests));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var originalIds = requests.Select(r => r.OriginalFileId).Distinct().ToList();
            var gate = await LoadLibraryIdsAsync(ctx, originalIds, ct).ConfigureAwait(false);
            gate.AddRange(await LibrariesContainingAsync(ctx, requests.Select(r => Path.GetDirectoryName(r.TargetPath) ?? r.TargetPath).ToList(), ct).ConfigureAwait(false));
            using var lease = await AcquireLibrariesAsync(gate, ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();

            var (known, unknown) = await LoadFilesAsync(ctx, originalIds, ct).ConfigureAwait(false);
            var byId = known.ToDictionary(k => k.File.Id);
            var results = new List<FileOperationItemResult>(unknown);
            var accepted = new List<(CatalogFile Original, VariantCreateRequest Request, string Target)>();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                if (!byId.TryGetValue(request.OriginalFileId, out var original))
                {
                    continue; // steht schon in „unknown“
                }

                if (!File.Exists(request.ResultPath))
                {
                    results.Add(new FileOperationItemResult(original.File.Id, original.FullPath, request.TargetPath, false, "Ergebnis-Datei fehlt."));
                    continue;
                }

                var target = UniqueVariantPath(Path.GetFullPath(request.TargetPath), reserved);
                reserved.Add(target);
                accepted.Add((original, request, target));
            }

            if (accepted.Count == 0)
            {
                return new FileOperationBatchResult(0, OperationType.CreateVariant, 0, results.Count, results);
            }

            var libraryIds = accepted.Select(a => a.Original.Library.Id).Distinct().ToList();
            var roots = await ctx.LibraryRoots.AsNoTracking().Where(r => libraryIds.Contains(r.LibraryId)).ToListAsync(ct).ConfigureAwait(false);
            var run = await CreateBatchAsync(
                ctx,
                OperationType.CreateVariant,
                accepted.Count == 1 ? $"Verbesserte Fassung von „{accepted[0].Original.File.Name}“" : $"{accepted.Count:N0} verbesserte Fassungen",
                accepted.Select(a => (a.Original, a.Original.FullPath, (string?)a.Target)),
                roots,
                own,
                ct).ConfigureAwait(false);

            var foldersTouched = new HashSet<int>();
            try
            {
                for (var i = 0; i < run.Items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (original, op) = run.Items[i];
                    var request = accepted[i].Request;
                    if (await ExecuteCreateVariantItemAsync(ctx, run, original, op, request).ConfigureAwait(false))
                    {
                        foldersTouched.Add(original.Library.Id);
                    }

                    Activity.ActivityScope.ReportItems(i + 1, run.Items.Count);
                }
            }
            catch (Exception ex)
            {
                await FinalizeAfterAbortAsync(run, ex).ConfigureAwait(false);
                throw;
            }

            var finished = await FinishBatchAsync(ctx, run, results).ConfigureAwait(false);
            foreach (var libraryId in foldersTouched)
            {
                PublishFoldersChanged(libraryId); // ein neuer Unterordner „verbessert“ erscheint im Baum
            }

            return finished;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Eine Fassung: Ergebnis an das Ziel verschieben, dann Katalog buchen. true, wenn dabei ein Ordner neu entstand.</summary>
    private async Task<bool> ExecuteCreateVariantItemAsync(LibraryContext ctx, BatchRun run, CatalogFile original, FileOperation op, VariantCreateRequest request)
    {
        var dest = op.DestPath!;
        var destDir = Path.GetDirectoryName(dest)!;
        var root = FindRootContaining(run.Roots, original.Library.Id, destDir);
        if (root is null)
        {
            await FailItemAsync(ctx, run, original, op, "Das Ziel liegt außerhalb der Bibliothek des Originals.", CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (File.Exists(dest))
        {
            await FailItemAsync(ctx, run, original, op, "Am Ziel liegt bereits eine Datei — nichts überschrieben.", CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        // Ab hier ohne Abbruch: ein angefangenes Verschieben läuft zu Ende, sonst passen Datei und Katalog nicht zusammen.
        await MarkApplyingAsync(ctx, op, CancellationToken.None).ConfigureAwait(false);
        var newDirectories = MissingDirectoryChain(destDir);
        var ownPaths = newDirectories.Append(dest).ToList();
        AddAll(run.Own, ownPaths);
        try
        {
            Directory.CreateDirectory(destDir);
            File.Move(request.ResultPath, dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RevokeAll(run.Own, ownPaths);
            await FailItemAsync(ctx, run, original, op, $"Speichern fehlgeschlagen: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        MediaFile? created = null;
        string? hint = null;
        (int LibraryId, int FileId)? staleRow = null;
        var error = await CommitItemAsync(ctx, op, undo: false, null, async token =>
        {
            var folder = await EnsureFolderChainAsync(ctx, root, destDir, token).ConfigureAwait(false);
            // Veraltete Katalogzeile am Ziel (z. B. „fehlt“ nach Löschen im Explorer): die Datei dort ist neu → Zeile ersetzen
            if (await FindCatalogRowByPathAsync(ctx, dest, token).ConfigureAwait(false) is { } stale)
            {
                staleRow = (stale.LibraryId, stale.Id);
                await RemoveCatalogEntryAsync(ctx, stale, token).ConfigureAwait(false);
                await ctx.SaveChangesAsync(token).ConfigureAwait(false);
            }

            // Fassung einer Fassung: an das ursprüngliche Original hängen (eine Ebene, Marke „n Fassungen“ bleibt eindeutig)
            var originalId = original.File.Id;
            var rootOriginal = await ctx.PhotoVariants.AsNoTracking().Where(v => v.FileId == originalId).Select(v => (int?)v.OriginalFileId)
                .FirstOrDefaultAsync(token).ConfigureAwait(false);
            var row = await InsertCatalogRowAsync(ctx, original.Library, folder, dest, token).ConfigureAwait(false);
            created = row;
            if (row is null)
            {
                hint = "Gespeichert, aber nicht im Katalog (Endung nicht registriert).";
                return;
            }

            CopyVariantMetadata(original.File, row, request);
            op.FileId = row.Id;
            ctx.PhotoVariants.Add(new PhotoVariant
            {
                FileId = row.Id,
                OriginalFileId = rootOriginal ?? originalId,
                Tools = request.Tools,
                CreatedAtUtc = DateTime.UtcNow,
                BatchId = run.Batch.Id,
            });
            await ctx.SaveChangesAsync(token).ConfigureAwait(false);
            if (request.CopyRatingAndTags)
            {
                await ctx.Database.ExecuteSqlAsync(
                    $"INSERT OR IGNORE INTO FileTags (FileId, TagId) SELECT {row.Id}, TagId FROM FileTags WHERE FileId = {original.File.Id}", token)
                    .ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            if (created is not null)
            {
                ctx.Entry(created).State = EntityState.Detached;
            }

            RevokeAll(run.Own, ownPaths); // Datei liegt am Ziel, Katalog fehlt: die Ordnerüberwachung bzw. der nächste Scan bucht sie
            run.Results.Add(new FileOperationItemResult(original.File.Id, original.FullPath, dest, false, error));
            return false;
        }

        if (staleRow is { } removed)
        {
            DeleteThumbnailFiles(removed.LibraryId, removed.FileId);
            run.Changes.Add(removed.LibraryId, FilesChangeKind.Removed, removed.FileId);
        }

        if (created is not null)
        {
            run.Changes.Add(original.Library.Id, FilesChangeKind.Added, created.Id);
        }

        run.Results.Add(new FileOperationItemResult(created?.Id ?? 0, original.FullPath, dest, true, hint));
        return newDirectories.Count > 0;
    }

    /// <summary>Aufnahmedatum, Ort, Kamera (und nach Wunsch Favorit/Sterne) aus der Katalogzeile des Originals.</summary>
    private static void CopyVariantMetadata(MediaFile original, MediaFile created, VariantCreateRequest request)
    {
        created.Width = request.Width ?? created.Width;
        created.Height = request.Height ?? created.Height;
        if (request.CopyCatalogMetadata)
        {
            created.CapturedAt = original.CapturedAt;
            created.CapturedAtSource = original.CapturedAtSource;
            created.CapturedAtPrecision = original.CapturedAtPrecision;
            created.CapturedAtOffset = original.CapturedAtOffset;
            created.ExifTimeOffset = original.ExifTimeOffset;
            created.CameraModel = original.CameraModel;
            created.Latitude = original.Latitude;
            created.Longitude = original.Longitude;
            created.GeoSource = original.GeoSource;
            created.PlaceId = original.PlaceId;
            created.GeoAccuracy = original.GeoAccuracy;
            created.GeoAccuracyReason = original.GeoAccuracyReason;
            created.GpsTimeUtc = original.GpsTimeUtc;
        }

        if (request.CopyRatingAndTags)
        {
            created.IsFavorite = original.IsFavorite;
            created.Rating = original.Rating;
        }
    }

    /// <summary>Freier Zielname: „Name.jpg“, sonst „Name (2).jpg“, „Name (3).jpg“ … (auch gegen Ziele derselben Anfrage).</summary>
    internal static string UniqueVariantPath(string target, IReadOnlySet<string>? reserved = null)
    {
        var dir = Path.GetDirectoryName(target) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        var candidate = target;
        for (var n = 2; File.Exists(candidate) || reserved?.Contains(candidate) == true; n++)
        {
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
        }

        return candidate;
    }

    /// <summary>
    /// Rückgängig einer Fassung: die neue Datei in den Papierkorb (war sie schon weg, nur den Katalog bereinigen), Katalogzeile
    /// samt FTS, Vorschaubildern und Verknüpfung entfernen. Die Zeile wird über den Pfad gesucht, sonst über die protokollierte
    /// FileId — nur, wenn sie noch auf das Ziel zeigt.
    /// </summary>
    private async Task<FileOperationItemResult> UndoCreateVariantItemAsync(LibraryContext ctx, FileOperation op, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
    {
        var dest = op.DestPath;
        if (string.IsNullOrEmpty(dest))
        {
            return await UndoFailAsync(ctx, op, "Kein Zielpfad protokolliert.", ct).ConfigureAwait(false);
        }

        var row = await FindCatalogRowByPathAsync(ctx, dest, ct).ConfigureAwait(false)
                  ?? await FindCatalogRowByIdAsync(ctx, op.FileId, dest, dest, ct).ConfigureAwait(false);
        string? hint = null;
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
                return await UndoFailAsync(ctx, op, $"Papierkorb: {ex.Message}", ct).ConfigureAwait(false);
            }
        }
        else
        {
            hint = "Die verbesserte Datei war nicht mehr vorhanden; Katalog bereinigt.";
        }

        var libraryId = row?.LibraryId;
        var fileId = row?.Id;
        var error = await CommitItemAsync(ctx, op, undo: true, hint, async token =>
        {
            if (row is not null)
            {
                await RemoveCatalogEntryAsync(ctx, row, token).ConfigureAwait(false); // PhotoVariants per Kaskade
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            own.Revoke(dest);
        }
        else if (libraryId is int lib && fileId is int id)
        {
            DeleteThumbnailFiles(lib, id);
            changes.Add(lib, FilesChangeKind.Removed, id);
        }

        return new FileOperationItemResult(fileId ?? op.FileId ?? 0, op.SourcePath, dest, error is null, error ?? hint);
    }
}
