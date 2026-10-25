using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Domain.Enums;
using Lumora.Services.Scanning.Exclusions;

namespace Lumora.Services.Operations;

/// <summary>
/// „Ausgeschlossene aus der Bibliothek entfernen“ (ADR-0066): vorhandene Dateien in Ordnern, die der Benutzer vom Einlesen
/// ausgeschlossen hat (allgemeine Liste wie <c>.thumbs</c> oder ein Ordner der Bibliothek), verschwinden aus dem Katalog —
/// keine Datei wird angefasst. Derselbe Weg wie „Fehlende entfernen“ (ADR-0065: Batch, Operation und Abzug je Datei, danach
/// Item-Transaktionen), aber als eigener <see cref="OperationType.ExcludeFromCatalog"/>: die Vorbedingung ist eine andere (Datei
/// vorhanden, Ordner ausgeschlossen statt „fehlt“ unter erreichbarer Wurzel), es gibt keinen Offline-Guard (die Platte wird nicht
/// befragt), und Protokoll/Aktivitäts-Anzeige sollen beide Fälle unterscheiden können. „Rückgängig“ läuft über denselben Zweig
/// (<see cref="UndoRemovalAsync"/>) und bringt die Zeilen mit denselben FileIds zurück. Danach leere ausgeschlossene Ordnerzeilen
/// aus dem Baum (mit Abzug). Protokoll auf Info-Level nur mit Zahlen.
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<ExclusionRemovalResult> RemoveExcludedAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return ExclusionRemovalResult.Empty;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var gateLibraries = await LoadLibraryIdsAsync(ctx, ids, ct).ConfigureAwait(false);
            using var lease = await AcquireLibrariesAsync(gateLibraries, ct).ConfigureAwait(false);
            var (known, unknown) = await LoadFilesAsync(ctx, ids, ct).ConfigureAwait(false);

            // Nur, was nach den gespeicherten Ausschlüssen seiner Bibliothek ausgeschlossen ist (der Dienst prüft selbst)
            var sets = new Dictionary<int, ScanExclusionSet>();
            foreach (var libraryId in known.Select(cf => cf.Library.Id).Distinct())
            {
                sets[libraryId] = await _exclusions.LoadSetAsync(libraryId, ct).ConfigureAwait(false);
            }

            var toRemove = known.Where(cf => sets[cf.Library.Id].IsExcludedPath(cf.Folder.Path)).ToList();
            var skipped = known.Count - toRemove.Count;
            if (toRemove.Count == 0)
            {
                LogExcludedRemoval(0, 0, 0, skipped);
                return new ExclusionRemovalResult(0, 0, 0, skipped, unknown.Count);
            }

            var changes = new ChangeSet();
            var description = toRemove.Count == 1
                ? "1 Datei aus ausgeschlossenen Ordnern aus der Bibliothek entfernt"
                : $"{toRemove.Count} Dateien aus ausgeschlossenen Ordnern aus der Bibliothek entfernt";
            var (batch, removed, failedIds, _) = await RemoveCatalogRowsWithUndoAsync(ctx, OperationType.ExcludeFromCatalog, description, toRemove, changes, ct).ConfigureAwait(false);

            var foldersRemoved = 0;
            if (removed > 0)
            {
                try
                {
                    foldersRemoved = await PruneExcludedFoldersAsync(ctx, batch.Id, sets).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or DbUpdateException or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "Ausgeschlossene entfernen: leere Ordner konnten nicht aus dem Baum entfernt werden (Batch {BatchId})", batch.Id);
                }
            }

            await FinishCatalogRemovalBatchAsync(ctx, batch, removed).ConfigureAwait(false);
            Publish(changes);
            foreach (var libraryId in sets.Keys)
            {
                PublishFoldersChanged(libraryId);
            }

            LogExcludedRemoval(batch.Id, removed, foldersRemoved, skipped);
            return new ExclusionRemovalResult(batch.Id, removed, foldersRemoved, skipped, failedIds.Count + unknown.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void LogExcludedRemoval(int batchId, int removed, int folders, int skipped)
        => _logger.LogInformation(
            "Ausgeschlossene entfernen: {Removed} Dateien aus dem Katalog, {Folders} leere Ordner aus dem Baum, {Skipped} nicht ausgeschlossen (Batch {BatchId})",
            removed, folders, skipped, batchId);

    /// <summary>Ausgeschlossene Ordnerzeilen, die jetzt leer sind (keine Dateien, Unterordner-Zeilen, Verknüpfungen), mit Abzug aus dem Baum.</summary>
    private async Task<int> PruneExcludedFoldersAsync(LibraryContext ctx, int batchId, Dictionary<int, ScanExclusionSet> sets)
    {
        var folders = await LoadFolderRowsAsync(ctx, sets.Keys.ToList()).ConfigureAwait(false);
        var candidates = folders.Where(f => sets[f.LibraryId].IsExcludedPath(f.Path)).Select(f => f.Id).ToHashSet();
        return await RemoveEmptyFolderRowsAsync(ctx, batchId, folders, candidates).ConfigureAwait(false);
    }
}
