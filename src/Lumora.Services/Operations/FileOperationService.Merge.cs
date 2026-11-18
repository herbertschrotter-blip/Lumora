using Microsoft.EntityFrameworkCore;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Merge;
using Lumora.Services.Watch;

namespace Lumora.Services.Operations;

/// <summary>
/// Ein zusammengefügtes Ergebnis, das als neue Datei in den Katalog soll (ADR-0078 Punkt 10).
/// </summary>
/// <param name="SourceFileIds">Quellen in Positionsreihenfolge (0 = erstes Einzelbild, neben dem das Ergebnis liegt); alle aus einer Bibliothek.</param>
/// <param name="ResultPath">Fertige Ergebnis-Datei im Temp-Ordner des Katalogs; wird an das Ziel verschoben.</param>
/// <param name="TargetPath">Gewünschter Zielpfad; ist er belegt, wird „ (2)“, „ (3)“ … angehängt — nie überschrieben.</param>
/// <param name="Tools">Kurzkennung („merge:panorama“).</param>
/// <param name="RecipeJson">Rezept für volle Auflösung und „Neu berechnen…“.</param>
/// <param name="SeriesId">Erkannte Serie (Stufe 2), sonst null.</param>
/// <param name="CopyCommonTags">Nur die Tags übernehmen, die alle Quellen tragen.</param>
/// <param name="CopyRating">Höchste Sternezahl der Quellen, Favorit wenn eine Quelle Favorit ist (Abnahme Nr. 12).</param>
/// <param name="FileMetadata">Zeit/Ort frisch aus den Dateien (Quellen, denen sie im Katalog noch fehlen), je FileId.</param>
public sealed record MergeCreateRequest(
    IReadOnlyList<int> SourceFileIds,
    string ResultPath,
    string TargetPath,
    MergeMethod Method,
    PanoramaProjection? Projection,
    string Tools,
    string RecipeJson,
    int? SeriesId = null,
    int? Width = null,
    int? Height = null,
    bool CopyCommonTags = true,
    bool CopyRating = true,
    IReadOnlyDictionary<int, MergeSourceMetadata>? FileMetadata = null);

/// <summary>
/// Zusammengefügte Ergebnisse anlegen (ADR-0078 Punkt 10, CreateMerged-Batch): gleicher Ablauf wie die Fassungen
/// (<see cref="CreateVariantsAsync"/>, gemeinsamer Element-Schritt <c>ExecuteCreateDerivedItemAsync</c>) — Ergebnis-Datei an das
/// Ziel verschieben, Files-Zeile mit FTS und Ordnerkette, Zeit/Ort/Kamera nach <see cref="MergeMetadataPicker"/>, Bewertung,
/// gemeinsame Tags, <c>MergedPhotos</c> und <c>MergedPhotoSources</c>. Alle Quellen müssen aus einer Bibliothek stammen
/// (ADR-0048); das Ziel nur unter deren Wurzeln (in einer versteckten Bibliothek bleibt so alles im Container). Die
/// Beschreibung des Batches nennt nur Verfahren und Zahl, nie Dateinamen. „Rückgängig“: Ergebnis in den Papierkorb,
/// Katalogzeile samt Verknüpfung weg.
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<FileOperationBatchResult> CreateMergedAsync(IReadOnlyList<MergeCreateRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("Es wurden keine Ergebnisse angegeben.", nameof(requests));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var allSourceIds = requests.SelectMany(r => r.SourceFileIds).Distinct().ToList();
            var gate = await LoadLibraryIdsAsync(ctx, allSourceIds, ct).ConfigureAwait(false);
            gate.AddRange(await LibrariesContainingAsync(ctx, requests.Select(r => Path.GetDirectoryName(r.TargetPath) ?? r.TargetPath).ToList(), ct).ConfigureAwait(false));
            using var lease = await AcquireLibrariesAsync(gate, ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();

            var (known, unknown) = await LoadFilesAsync(ctx, allSourceIds, ct).ConfigureAwait(false);
            var byId = known.ToDictionary(k => k.File.Id);
            var results = new List<FileOperationItemResult>(unknown);
            var accepted = new List<(CatalogFile First, MergeCreateRequest Request, string Target, List<CatalogFile> Sources)>();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                var sources = request.SourceFileIds.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                if (sources.Count < 2 || sources.Count != request.SourceFileIds.Distinct().Count())
                {
                    results.Add(new FileOperationItemResult(sources.FirstOrDefault()?.File.Id ?? 0, sources.FirstOrDefault()?.FullPath ?? string.Empty, request.TargetPath, false,
                        "Mindestens zwei Quellen im Katalog nötig."));
                    continue;
                }

                if (sources.Select(s => s.Library.Id).Distinct().Count() > 1)
                {
                    results.Add(new FileOperationItemResult(sources[0].File.Id, sources[0].FullPath, request.TargetPath, false,
                        "Die Fotos stammen aus verschiedenen Bibliotheken."));
                    continue;
                }

                if (!File.Exists(request.ResultPath))
                {
                    results.Add(new FileOperationItemResult(sources[0].File.Id, sources[0].FullPath, request.TargetPath, false, "Ergebnis-Datei fehlt."));
                    continue;
                }

                var target = UniqueVariantPath(Path.GetFullPath(request.TargetPath), reserved);
                reserved.Add(target);
                accepted.Add((sources[0], request, target, sources));
            }

            if (accepted.Count == 0)
            {
                return new FileOperationBatchResult(0, OperationType.CreateMerged, 0, results.Count, results);
            }

            var libraryIds = accepted.Select(a => a.First.Library.Id).Distinct().ToList();
            var roots = await ctx.LibraryRoots.AsNoTracking().Where(r => libraryIds.Contains(r.LibraryId)).ToListAsync(ct).ConfigureAwait(false);
            var run = await CreateBatchAsync(
                ctx,
                OperationType.CreateMerged,
                accepted.Count == 1 ? $"{MethodLabel(accepted[0].Request.Method)} aus {accepted[0].Sources.Count:N0} Fotos" : $"{accepted.Count:N0} zusammengefügte Ergebnisse",
                accepted.Select(a => (a.First, a.First.FullPath, (string?)a.Target)),
                roots,
                own,
                ct).ConfigureAwait(false);

            var foldersTouched = new HashSet<int>();
            try
            {
                for (var i = 0; i < run.Items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (first, op) = run.Items[i];
                    var (_, request, _, sources) = accepted[i];
                    if (await ExecuteCreateMergedItemAsync(ctx, run, first, op, request, sources).ConfigureAwait(false))
                    {
                        foldersTouched.Add(first.Library.Id);
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
                PublishFoldersChanged(libraryId); // ein neuer Unterordner „zusammengefügt“ erscheint im Baum
            }

            return finished;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string MethodLabel(MergeMethod method) => method switch
    {
        MergeMethod.Panorama => "Panorama",
        MergeMethod.ExposureFusion => "Belichtungsreihe",
        MergeMethod.Denoise => "Entrauschen",
        MergeMethod.RemovePeople => "Leute entfernen",
        MergeMethod.LongExposure => "Langzeitbelichtung",
        MergeMethod.FocusStack => "Fokus-Stapel",
        MergeMethod.GroupShot => "Gruppenfoto",
        MergeMethod.HdrPanorama => "HDR-Panorama",
        MergeMethod.Timelapse => "Zeitraffer",
        _ => "Zusammengefügt",
    };

    private Task<bool> ExecuteCreateMergedItemAsync(LibraryContext ctx, BatchRun run, CatalogFile first, FileOperation op, MergeCreateRequest request, List<CatalogFile> sources)
        => ExecuteCreateDerivedItemAsync(ctx, run, first, op, request.ResultPath, "Das Ziel liegt außerhalb der Bibliothek der Fotos.", async (row, token) =>
        {
            var files = sources.Select(s => s.File).ToList();
            var choice = MergeMetadataPicker.Pick(files, request.FileMetadata);
            MergeMetadataPicker.Apply(row, files, choice, request.FileMetadata, request.CopyRating);
            row.Width = request.Width ?? row.Width;
            row.Height = request.Height ?? row.Height;
            ctx.MergedPhotos.Add(new MergedPhoto
            {
                FileId = row.Id,
                Method = request.Method,
                Projection = request.Projection,
                SourceCount = sources.Count,
                SeriesId = request.SeriesId,
                Tools = request.Tools,
                RecipeJson = request.RecipeJson,
                CreatedAtUtc = DateTime.UtcNow,
                BatchId = run.Batch.Id,
            });
            await ctx.SaveChangesAsync(token).ConfigureAwait(false);
            for (var position = 0; position < sources.Count; position++)
            {
                ctx.MergedPhotoSources.Add(new MergedPhotoSource { ResultFileId = row.Id, SourceFileId = sources[position].File.Id, Position = position });
            }

            await ctx.SaveChangesAsync(token).ConfigureAwait(false);
            if (request.CopyCommonTags)
            {
                // nur Tags, die alle Quellen tragen (Ids sind Zahlen aus dem Katalog)
                var list = string.Join(',', files.Select(f => f.Id));
                var sql = $"INSERT OR IGNORE INTO FileTags (FileId, TagId) SELECT {row.Id}, TagId FROM FileTags WHERE FileId IN ({list}) GROUP BY TagId HAVING COUNT(DISTINCT FileId) = {files.Count}";
                await ctx.Database.ExecuteSqlRawAsync(sql, token).ConfigureAwait(false);
            }
        });

    /// <summary>
    /// Rückgängig eines Ergebnisses: die neue Datei in den Papierkorb (war sie schon weg, nur den Katalog bereinigen), Katalogzeile
    /// samt FTS, Vorschaubildern, <c>MergedPhotos</c>/<c>MergedPhotoSources</c> (Kaskade) entfernen. Die Einzelbilder bleiben.
    /// </summary>
    private Task<FileOperationItemResult> UndoCreateMergedItemAsync(LibraryContext ctx, FileOperation op, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
        => UndoCreateVariantItemAsync(ctx, op, changes, own, ct, "Das zusammengefügte Ergebnis war nicht mehr vorhanden; Katalog bereinigt.");
}
