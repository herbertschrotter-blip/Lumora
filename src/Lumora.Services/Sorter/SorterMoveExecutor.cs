using Microsoft.Extensions.Logging;
using Lumora.Services.Operations;
using Lumora.Services.Watch;

namespace Lumora.Services.Sorter;

/// <summary>
/// Führt einen <see cref="SorterPlan"/> aus — ausschließlich über <see cref="IFileOperationService"/>
/// (ADR-0012: Protokoll + Undo): je Zielordner ein <c>MoveAsync</c>, Dedup-Umbenennungen (Flatten) als
/// vorgeschaltetes <c>RenameAsync</c>. Angelegte Zielordner und gelöschte leere Quellordner werden im
/// Lauf-Protokoll festgehalten; „Rückgängig“ nimmt alle Batches in umgekehrter Reihenfolge zurück und
/// entfernt leer gewordene angelegte Ordner. Gelöschte leere Ordner stehen kurz in der <see cref="IOwnChangeRegistry"/>
/// (kein automatischer Scan der Ordnerüberwachung dafür). Der Executor hält nie die Scan-Sperre selbst — die belegt der
/// <see cref="IFileOperationService"/> je Batch und gibt sie vor der Rückkehr frei.
/// </summary>
public sealed partial class SorterMoveExecutor
{
    private readonly IFileOperationService _fileOps;
    private readonly SorterRunLog _runLog;
    private readonly SorterCatalog _catalog;
    private readonly ILogger<SorterMoveExecutor> _logger;
    private readonly IOwnChangeRegistry _ownChanges;

    /// <param name="ownChanges">Optional (Tests); produktiv der Singleton, den die Ordnerüberwachung abfragt.</param>
    public SorterMoveExecutor(
        IFileOperationService fileOps,
        SorterRunLog runLog,
        SorterCatalog catalog,
        ILogger<SorterMoveExecutor> logger,
        IOwnChangeRegistry? ownChanges = null,
        Activity.IActivityCenter? activities = null) // Aktivitäts-Anzeige (ADR-0067): ein Lauf = ein Vorgang
    {
        _activities = activities;
        _fileOps = fileOps;
        _runLog = runLog;
        _catalog = catalog;
        _logger = logger;
        _ownChanges = ownChanges ?? new OwnChangeRegistry();
    }

    private async Task<SorterExecutionResult> ExecuteCoreAsync(
        SorterPlan plan,
        string runKey,
        IProgress<SorterProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var result = new SorterExecutionResult();
        // Gesperrtes Ziel (ADR-0024): der Plan hat dann keine bereiten Einträge — hier nur zur Sicherheit erneut geprüft.
        var ready = plan.TargetError is null
            ? plan.Items.Where(i => i.IsReady && i.FileId.HasValue).ToList()
            : new List<SorterPlanItem>();
        result.Skipped = plan.SkippedCount + (plan.Items.Count - ready.Count);
        foreach (var item in plan.Items.Where(i => !i.IsReady || !i.FileId.HasValue))
        {
            result.Details.Add(new SorterExecutionDetail(
                item.FileName, item.SourcePath, item.DestPath, false, item.Note ?? SorterPlanItem.StatusText(item.Status)));
        }

        var total = ready.Count;
        var done = 0;
        progress?.Report(new SorterProgress(0, total, total == 0 ? "Nichts zu verschieben." : "Starte …"));

        try
        {
            foreach (var targetGroup in GroupByTarget(ready))
            {
                ct.ThrowIfCancellationRequested();
                var target = targetGroup.Key;
                progress?.Report(new SorterProgress(done, total, $"Verschiebe nach „{target}“ …"));

                var missingChain = CollectMissingChain(target);
                var ids = new List<int>(targetGroup.Value.Count);
                var byId = new Dictionary<int, SorterPlanItem>();

                foreach (var item in targetGroup.Value)
                {
                    var fileId = item.FileId!.Value;
                    if (item.RenameTo is { } newName)
                    {
                        var rename = await _fileOps.RenameAsync(fileId, newName, ct).ConfigureAwait(false);
                        if (rename.BatchId > 0)
                        {
                            result.BatchIds.Add(rename.BatchId);
                        }

                        if (rename.Succeeded == 0)
                        {
                            done++;
                            result.Failed++;
                            var error = rename.Items.FirstOrDefault()?.Error ?? "unbekannter Fehler";
                            result.Details.Add(new SorterExecutionDetail(
                                item.FileName, item.SourcePath, item.DestPath, false, $"Umbenennen in „{newName}“ fehlgeschlagen: {error}"));
                            continue;
                        }
                    }

                    ids.Add(fileId);
                    byId[fileId] = item;
                }

                if (ids.Count > 0)
                {
                    var move = await _fileOps.MoveAsync(ids, target, ct).ConfigureAwait(false);
                    if (move.BatchId > 0)
                    {
                        result.BatchIds.Add(move.BatchId);
                    }

                    foreach (var ir in move.Items)
                    {
                        done++;
                        var item = byId.GetValueOrDefault(ir.FileId);
                        var name = item?.FileName ?? Path.GetFileName(ir.SourcePath);
                        if (ir.Success)
                        {
                            result.Moved++;
                        }
                        else
                        {
                            result.Failed++;
                        }

                        result.Details.Add(new SorterExecutionDetail(name, ir.SourcePath, ir.DestPath, ir.Success, ir.Error));
                    }

                    if (move.Succeeded > 0)
                    {
                        foreach (var dir in missingChain.Where(Directory.Exists))
                        {
                            if (!result.CreatedFolders.Contains(dir, StringComparer.OrdinalIgnoreCase))
                            {
                                result.CreatedFolders.Add(dir);
                            }
                        }
                    }
                }

                progress?.Report(new SorterProgress(done, total, $"{done:N0} von {total:N0} verarbeitet"));
            }

            if (plan.DeleteEmptySourceFolders)
            {
                await DeleteEmptySourceFoldersAsync(plan, result, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (result.BatchIds.Count > 0)
            {
                await SaveRunAsync(runKey, plan, result).ConfigureAwait(false);
            }
        }

        progress?.Report(new SorterProgress(done, total, "Fertig."));
        _logger.LogInformation(
            "{Kind}-Lauf abgeschlossen: {Moved} verschoben, {Failed} fehlgeschlagen, {Skipped} übersprungen, {Batches} Batch(es), {Created} Ordner angelegt, {Deleted} gelöscht",
            plan.Kind, result.Moved, result.Failed, result.Skipped, result.BatchIds.Count, result.CreatedFolders.Count, result.DeletedFolders.Count);
        return result;
    }

    public Task<SorterRunRecord?> GetLastRunAsync(string runKey, CancellationToken ct) => _runLog.GetAsync(runKey, ct);

    private async Task<SorterUndoResult?> UndoLastRunCoreAsync(string runKey, IProgress<SorterProgress>? progress, CancellationToken ct)
    {
        var record = await _runLog.GetAsync(runKey, ct).ConfigureAwait(false);
        if (record is null || record.Undone || record.BatchIds.Count == 0)
        {
            return null;
        }

        var details = new List<SorterExecutionDetail>();
        var restored = 0;
        var failed = 0;
        var batches = Enumerable.Reverse(record.BatchIds).ToList();
        var step = 0;

        foreach (var batchId in batches)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SorterProgress(step, batches.Count, $"Mache Batch {batchId} rückgängig …"));
            var undo = await _fileOps.UndoBatchAsync(batchId, ct).ConfigureAwait(false);
            step++;
            if (undo is null)
            {
                continue;
            }

            restored += undo.Succeeded;
            failed += undo.Failed;
            foreach (var item in undo.Items)
            {
                var name = Path.GetFileName(item.DestPath ?? item.SourcePath);
                details.Add(new SorterExecutionDetail(name, item.SourcePath, item.DestPath, item.Success, item.Error));
            }
        }

        var removed = new List<string>();
        foreach (var folder in record.CreatedFolders.OrderByDescending(SorterPaths.Depth))
        {
            if (await TryDeleteEmptyFolderAsync(record.LibraryId, folder, ct).ConfigureAwait(false))
            {
                removed.Add(folder);
            }
        }

        record.Undone = true;
        await _runLog.SaveAsync(runKey, record, CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new SorterProgress(batches.Count, batches.Count, "Fertig."));
        _logger.LogInformation(
            "{Kind}-Lauf rückgängig: {Restored} zurück, {Failed} fehlgeschlagen, {Removed} Ordner entfernt",
            record.Kind, restored, failed, removed.Count);

        return new SorterUndoResult(restored, failed, removed, details);
    }

    // ------------------------------------------------------------------ Helfer

    /// <summary>Gruppiert nach Zielordner in Reihenfolge des ersten Auftretens.</summary>
    private static List<KeyValuePair<string, List<SorterPlanItem>>> GroupByTarget(IEnumerable<SorterPlanItem> items)
    {
        var order = new List<KeyValuePair<string, List<SorterPlanItem>>>();
        var index = new Dictionary<string, List<SorterPlanItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!index.TryGetValue(item.TargetFolder, out var list))
            {
                list = new List<SorterPlanItem>();
                index[item.TargetFolder] = list;
                order.Add(new KeyValuePair<string, List<SorterPlanItem>>(item.TargetFolder, list));
            }

            list.Add(item);
        }

        return order;
    }

    /// <summary>Noch nicht existierende Ordner vom Ziel aufwärts bis zum ersten vorhandenen Vorfahren (tiefster zuerst).</summary>
    private static List<string> CollectMissingChain(string target)
    {
        var chain = new List<string>();
        var current = target;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || SorterPaths.PathEquals(parent, current))
            {
                break;
            }

            current = parent;
        }

        return chain;
    }

    private async Task DeleteEmptySourceFoldersAsync(SorterPlan plan, SorterExecutionResult result, CancellationToken ct)
    {
        // Wurzeln (Eingang wie Archiv, ADR-0020) und die Zielwurzel werden nie gelöscht.
        var protectedFolders = new HashSet<string>(plan.Roots.Select(r => r.Path), StringComparer.OrdinalIgnoreCase) { plan.TargetRoot };
        var boundary = plan.CleanupBoundary;

        foreach (var folder in plan.SourceFoldersToClean.OrderByDescending(SorterPaths.Depth))
        {
            var current = folder;
            while (!string.IsNullOrEmpty(current)
                   && !protectedFolders.Contains(current)
                   && (boundary is null || (!SorterPaths.PathEquals(current, boundary) && SorterPaths.IsInsideRoot(current, boundary))))
            {
                if (result.DeletedFolders.Contains(current, StringComparer.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!await TryDeleteEmptyFolderAsync(plan.LibraryId, current, ct).ConfigureAwait(false))
                {
                    break;
                }

                result.DeletedFolders.Add(current);
                current = Path.GetDirectoryName(current);
            }
        }
    }

    /// <summary>Löscht den Ordner nur, wenn er wirklich leer ist (auch keine versteckten Dateien), und räumt die Folders-Zeile auf.</summary>
    private async Task<bool> TryDeleteEmptyFolderAsync(int libraryId, string folder, CancellationToken ct)
    {
        if (!SorterPaths.IsTrulyEmptyDirectory(folder))
        {
            return false;
        }

        using var own = _ownChanges.Begin();
        own.Add(folder);
        try
        {
            Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            own.Revoke(folder);
            _logger.LogWarning(ex, "Leerer Ordner konnte nicht gelöscht werden: {Folder}", folder);
            return false;
        }

        await _catalog.TryRemoveEmptyFolderRowAsync(libraryId, folder, ct).ConfigureAwait(false);
        return true;
    }

    private async Task SaveRunAsync(string runKey, SorterPlan plan, SorterExecutionResult result)
    {
        try
        {
            await _runLog.SaveAsync(runKey, new SorterRunRecord
            {
                Kind = plan.Kind,
                LibraryId = plan.LibraryId,
                TimestampUtc = DateTime.UtcNow,
                SourceFolder = plan.SourceFolder,
                TargetRoot = plan.TargetRoot,
                BatchIds = result.BatchIds.ToList(),
                CreatedFolders = result.CreatedFolders.ToList(),
                DeletedFolders = result.DeletedFolders.ToList(),
                Moved = result.Moved,
                Undone = false,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lauf-Protokoll {Key} konnte nicht gespeichert werden — Undo über die Galerie (einzelne Batches) bleibt möglich", runKey);
        }
    }
}
